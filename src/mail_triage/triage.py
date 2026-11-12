"""One triage run, and what happens to the queue afterwards.

- **Delete** marks the email read and moves it to Deleted Items, so it's recoverable.
- **Review** leaves it where it is and queues it (queue_file) for a decision.
- **Check** runs a test on the email (checks.py) and deletes it only if there's nothing to do.
- **Anything no rule names is queued, never deleted.** A new sender is a question until it's a rule.
- **Each email is raised once.** state_file records every email ever queued, by its internet
  message ID (Graph's own id changes when an email is moved), so one you've seen and kept in the
  folder doesn't come back. The queue keeps what's still waiting, run after run, until you
  `delete`, `keep` or `clear` it.
- **What the rules delete is recorded** (deleted_file), so it can be reviewed: `mail-triage deleted` lists
  what went since the queue was last cleared, by rule. Running unattended, that's how a rule that has
  started catching the wrong mail gets noticed (10 Nov 2026). Kept for KEEP_DAYS.

The queue file is JSON, so another program (an assistant, a script) can read it:

    {"checked": "2026-11-10T09:00:00", "review": [{"n": 1, "folder": "...", "id": "...", "key": "...",
      "received": "...", "from": "...", "subject": "...", "preview": "...", "rule": "...",
      "check": "...", "webLink": "..."}]}
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import re
import urllib.parse

from .checks import CHECKS
from .config import Config

KEEP_DAYS = 60
SCOPES = ["Mail.ReadWrite"]
READ_ONLY = ["Mail.Read"]   # a dry run only reads, so it works before Mail.ReadWrite is granted
FIELDS = "id,internetMessageId,subject,from,receivedDateTime,isRead,bodyPreview,webLink"


def key(message: dict) -> str:
    return message.get("internetMessageId") or message["id"]


def sender(message: dict) -> str:
    return message.get("from", {}).get("emailAddress", {}).get("address", "")


def _load(path: pathlib.Path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return default


def _save(path: pathlib.Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(f"{path.name}.tmp")
    temp.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    temp.replace(path)


def queue(config: Config) -> list:
    return _load(config.queue_file, {}).get("review", [])


def save_queue(config: Config, items: list) -> None:
    for n, item in enumerate(items, 1):
        item["n"] = n
    _save(config.queue_file, {"checked": dt.datetime.now().isoformat(timespec="seconds"), "review": items})


class Mailbox:
    """The few Graph calls triage makes. graph is an m365_graph.Client (or anything with request/pages)."""

    def __init__(self, graph, dry_run: bool = False):
        self.graph, self.scopes = graph, READ_ONLY if dry_run else SCOPES

    def folder_id(self, name: str) -> str:
        """By display name, searching the whole tree, as triage folders are often nested."""
        waiting = ["/me/mailFolders?$top=100&includeHiddenFolders=false"]
        while waiting:
            for folder in self.graph.pages(waiting.pop(), scopes=self.scopes):
                if folder["displayName"].lower() == name.lower():
                    return folder["id"]
                if folder.get("childFolderCount"):
                    waiting.append(f"/me/mailFolders/{folder['id']}/childFolders?$top=100")
        raise LookupError(f"No mail folder called {name!r}")

    def folder_path(self, path: str) -> str:
        """A folder by its path from the top, such as "Inbox/Projects" (/ or \\ between)."""
        parts = [p for p in re.split(r"[/\\]", path) if p]
        url, found = "/me/mailFolders?$top=100", None
        for part in parts:
            found = next((f["id"] for f in self.graph.pages(url, scopes=self.scopes)
                          if f["displayName"].lower() == part.lower()), None)
            if found is None:
                raise LookupError(f"No mail folder {path!r} (no {part!r})")
            url = f"/me/mailFolders/{found}/childFolders?$top=100"
        return found

    def file(self, message_id: str, folder_id: str) -> None:
        self.graph.request(f"/me/messages/{message_id}/move", "POST", {"destinationId": folder_id}, scopes=SCOPES)

    def messages(self, folder_id: str, since: str | None) -> list:
        query = urllib.parse.urlencode({
            **({"$filter": f"receivedDateTime ge {since}T00:00:00Z"} if since else {}),
            "$select": FIELDS, "$orderby": "receivedDateTime desc", "$top": "50"})
        return list(self.graph.pages(f"/me/mailFolders/{folder_id}/messages?{query}", scopes=self.scopes))

    def body(self, message_id: str) -> str:
        return self.graph.request(f"/me/messages/{message_id}?$select=body", scopes=self.scopes)["body"]["content"]

    def delete(self, message_id: str) -> None:
        self.graph.request(f"/me/messages/{message_id}", "PATCH", {"isRead": True}, scopes=SCOPES)
        self.graph.request(f"/me/messages/{message_id}/move", "POST", {"destinationId": "deleteditems"}, scopes=SCOPES)


def run(config: Config, mailbox: Mailbox, dry_run: bool = False, log=print) -> dict:
    raised = _load(config.state_file, {})
    previous = queue(config)
    seen, new, deleted, again, filed = set(), [], 0, 0, 0
    record = _load(config.deleted_file, {"items": []})
    for folder in config.folders:
        for message in mailbox.messages(mailbox.folder_id(folder.name), folder.since):
            seen.add(key(message))
            if key(message) in raised:
                again += 1
                continue
            text = f"{message.get('subject', '')}\n{message.get('bodyPreview', '')}"
            rule = config.rule_for(sender(message), text, folder)
            if rule is None and folder.unmatched == "leave":
                continue   # an inbox: only the rules act there
            action, note = (rule.action if rule else "review"), None
            if action == "check":
                needs_body, check = CHECKS[rule.check]
                fine, note = check(message, mailbox.body(message["id"]) if needs_body else "", config)
                action = "delete" if fine else "review"
            label = f"[{rule.name if rule else 'no rule'}] {sender(message)}: {message.get('subject', '')}" + (f" ({note})" if note else "")
            if action == "delete":
                if not dry_run:
                    mailbox.delete(message["id"])
                    record["items"].append({"when": dt.datetime.now().isoformat(timespec="microseconds"), "folder": folder.name,
                                            "received": message.get("receivedDateTime"), "from": sender(message),
                                            "subject": message.get("subject", ""), "rule": rule.name if rule else "",
                                            **({"check": note} if note else {})})
                log(f"{'would delete' if dry_run else 'deleted'}  {label}")
                deleted += 1
                continue
            if action == "file":
                if not dry_run:
                    mailbox.file(message["id"], mailbox.folder_path(rule.to))
                    record["items"].append({"when": dt.datetime.now().isoformat(timespec="microseconds"), "folder": folder.name,
                                            "received": message.get("receivedDateTime"), "from": sender(message),
                                            "subject": message.get("subject", ""), "rule": rule.name, "to": rule.to})
                log(f"{'would file' if dry_run else 'filed'} to {rule.to}  {label}")
                filed += 1
                continue
            new.append({"folder": folder.name, "id": message["id"], "key": key(message),
                        "received": message.get("receivedDateTime"), "from": sender(message),
                        "subject": message.get("subject", ""), "preview": message.get("bodyPreview", "")[:300],
                        "rule": rule.name if rule else "no rule matched", **({"check": note} if note else {}),
                        "webLink": message.get("webLink")})
            raised.setdefault(key(message), dt.datetime.now().isoformat(timespec="seconds"))
            log(f"{'would queue' if dry_run else 'queued'}  {label}")
    invites_new = 0
    if config.invites:
        from . import invites
        # The first run has nothing to compare with, so it only remembers what's accepted.
        accepted, acted = _load(config.accepted_file, {}), []
        items, pending_keys, _ = invites.triage(config, invites.Calendar(mailbox.graph), raised, log,
                                                accepted=accepted, acted=acted, dry_run=dry_run)
        if not dry_run:
            _save(config.accepted_file, accepted)
        for a in acted:
            record["items"].append({"when": dt.datetime.now().isoformat(timespec="microseconds"), "folder": "Calendar",
                                    "received": a["received"], "from": a["from"], "subject": a["subject"],
                                    "rule": "Accepted: already accepted, only shortened", "accepted": a["why"]})
        if config.invites.proposals_folders:
            p_items, p_acted, p_seen = invites.proposals(config, mailbox, invites.Calendar(mailbox.graph), raised, log, dry_run)
            new += p_items
            seen |= p_seen
            for a in p_acted:
                record["items"].append({"when": dt.datetime.now().isoformat(timespec="microseconds"), "folder": "Calendar",
                                        "received": a["received"], "from": a["from"], "subject": a["subject"],
                                        "rule": "Accepted: new time proposed, and free", "accepted": a["why"]})
        seen |= pending_keys   # an invite answered elsewhere (in Outlook) drops off the queue
        new += items
        invites_new = len(items)
        for name in config.invites.requests_folders:
            for m in invites.answered_requests(mailbox.graph, mailbox.folder_id(name)):
                if not dry_run:
                    mailbox.delete(m["id"])
                    record["items"].append({"when": dt.datetime.now().isoformat(timespec="microseconds"), "folder": name,
                                            "received": m.get("receivedDateTime"), "from": sender(m),
                                            "subject": m.get("subject", ""), "rule": "Answered invitations"})
                log(f"{'would delete' if dry_run else 'deleted'}  [answered invitation: {m['answer']}] {m.get('subject')}")
                deleted += 1
    # Still waiting from earlier runs, and still in the folder (one deleted by hand in Outlook drops out).
    carried = [item for item in previous if item.get("key") in seen and item["key"] not in {i["key"] for i in new}]
    if not dry_run:
        _save(config.state_file, raised)
        cutoff = (dt.datetime.now() - dt.timedelta(days=KEEP_DAYS)).isoformat()
        record["items"] = [i for i in record["items"] if i["when"] >= cutoff]
        _save(config.deleted_file, record)
        save_queue(config, carried + new)
    report = {"deleted": deleted, "filed": filed, "new": len(new), "waiting": len(carried),
              "kept": max(0, again - len(carried)), "invites": invites_new}
    log(f"done: {deleted} deleted, " + (f"{filed} filed, " if filed else "") + f"{len(new)} new to review" + (f" ({invites_new} invites)" if invites_new else "")
        + f", {len(carried)} still waiting from earlier runs, {report['kept']} seen before and kept")
    return report


def pick(config: Config, refs: list) -> list:
    """Queue items by their number in `list` (1, 2...) or by Graph id. Unknown references raise."""
    items = queue(config)
    found = []
    for ref in refs:
        item = next((i for i in items if str(i.get("n")) == ref or i["id"] == ref), None)
        if item is None:
            raise LookupError(f"Nothing in the queue is {ref!r}. Run `mail-triage list`.")
        found.append(item)
    return found


def delete(config: Config, mailbox: Mailbox, refs: list, log=print) -> int:
    """Delete emails from the queue (by number or id), or any email by its Graph id."""
    items = {i["id"]: i for i in queue(config)}
    ids = []
    for ref in refs:
        if ref.isdigit() and len(ref) < 6:
            ids.append(pick(config, [ref])[0]["id"])
        else:
            ids.append(ref)
    invites = [items[i]["subject"] for i in ids if items.get(i, {}).get("type") == "invite"]
    if invites:
        raise LookupError(f"{invites[0]} is an invite, not an email: answer it with `mail-triage invite decline <n>`.")
    for message_id in ids:
        mailbox.delete(message_id)
        item = items.get(message_id)
        log(f"deleted by hand  {item['from'] + ': ' + item['subject'] if item else message_id[:24] + '…'}")
    save_queue(config, [i for i in queue(config) if i["id"] not in ids])
    return len(ids)


def file(config: Config, mailbox: Mailbox, refs: list, to: str, log=print) -> int:
    """Move queued emails to a folder (left unread), and off the queue."""
    chosen = pick(config, refs)
    if any(i.get("type") == "invite" for i in chosen):
        raise LookupError("An invite can't be filed: answer it with `mail-triage invite`.")
    folder_id = mailbox.folder_path(to)
    for item in chosen:
        mailbox.file(item["id"], folder_id)
        log(f"filed to {to} by hand  {item['from']}: {item['subject']}")
    gone = {i["id"] for i in chosen}
    save_queue(config, [i for i in queue(config) if i["id"] not in gone])
    return len(chosen)


def answer(config: Config, calendar, refs: list, action: str, note: str = "", send: bool = True, log=print,
           series: bool = False, move: bool = False) -> int:
    """Answer queued invites (by number): accept, tentative or decline.

    - **Accepting a series accepts the whole series**, and each unanswered occurrence changed on its own.
    - **Declining declines instances, never the series**: the occurrences the recommendation names (leave or
      a fixed clash), or else the first one queued. `series=True` declines the whole series.
    - **A decline uses the recommendation's note** (the leave note) unless one is given.
    - **No response is sent where the organiser didn't ask for one** (Graph refuses it otherwise).
    - `move=True` with accept also moves each movable clash to its suggested slot.
    - An accepted series whose recommendation also declines some occurrences stays queued until they're declined.
    """
    chosen = pick(config, refs)
    wrong = [i["subject"] for i in chosen if i.get("type") != "invite"]
    if wrong:
        raise LookupError(f"{wrong[0]} is an email, not an invite.")
    if series and any(not i.get("series") for i in chosen):
        raise LookupError("--series is for a recurring meeting; that one isn't.")
    keep = []
    for item in chosen:
        respond = send and item.get("responseRequested", True)
        if action == "decline":
            targets = [item["series"]] if series else (item.get("decline") or [item["occurrence"]])
            text = note or item.get("note", "")
        else:
            # An occurrence the organiser changed on its own (an exception) isn't answered by the series'
            # answer, so each is answered too (12 Nov 2026: the Alt Net PCB, shortened for one week only).
            targets, text = [item.get("series") or item["occurrence"]] + item.get("exceptions", []), note
        for target in targets:
            calendar.respond(target, action, text, respond)
        done = {"accept": "accepted", "tentative": "accepted tentatively", "decline": "declined"}[action]
        what = ("the whole series" if series or (action != "decline" and item.get("series"))
                else f"{len(targets)} occurrence{'s' if len(targets) > 1 else ''}")
        log(f"invite {done} ({what}){'' if respond else ' (no response sent)'}  "
            f"{item['from']}: {item['subject']}" + (f" | note: {text}" if text and respond else ""))
        if move and action == "accept":
            for c in item.get("clashes", []):
                if c.get("to"):
                    start, end = dt.datetime.fromisoformat(c["to"]), dt.datetime.fromisoformat(c["to_end"])
                    calendar.move(c["id"], start, end)
                    log(f"moved  {c['subject']}: {c['start'][5:16]} -> {start:%a %d %b %H:%M}–{end:%H:%M}")
        if action != "decline" and item.get("decline") and item.get("recommendation", "").startswith("accept, and decline"):
            keep.append(item["id"])
            log(f"  still queued: decline {len(item['decline'])} occurrence(s) with `mail-triage invite decline {item.get('n')}`")
    gone = {i["id"] for i in chosen} - set(keep)
    save_queue(config, [i for i in queue(config) if i["id"] not in gone])
    return len(chosen)


def keep(config: Config, refs: list, log=print) -> int:
    """Take emails off the queue and leave them in their folder. They won't be raised again."""
    gone = {i["id"] for i in pick(config, refs)}
    save_queue(config, [i for i in queue(config) if i["id"] not in gone])
    log(f"kept {len(gone)}: off the queue, left in the folder")
    return len(gone)


def clear(config: Config, log=print) -> int:
    """The whole queue has been decided (or put to whoever decides): empty it. What the rules deleted
    counts as seen too, so `deleted` starts again from here."""
    count = len(queue(config))
    save_queue(config, [])
    record = _load(config.deleted_file, {"items": []})
    record["seen_until"] = dt.datetime.now().isoformat(timespec="microseconds")
    _save(config.deleted_file, record)
    log(f"queue cleared: {count}")
    return count


def deleted(config: Config, everything: bool = False) -> list:
    """What the rules deleted since the queue was last cleared (or all that's kept), oldest first."""
    record = _load(config.deleted_file, {"items": []})
    since = "" if everything else record.get("seen_until", "")
    return [i for i in record["items"] if i["when"] > since]
