"""Meeting invites: raised with a recommendation, answered on your say-so.

Who sent an invite says little about whether to go, so an invite is judged on time and context instead. On
three months of one person's calendar (10 Nov 2026), **every decline was either leave (10) or a clash with
something already accepted (17)**; with no clash, 78 of 100 invites were accepted. So:

- **Leave:** decline. Leave is your own diary entries named as leave (leave_pattern).
- **A clash with something fixed** (someone else's meeting you've accepted, anything `protected`, or a block
  in `fixed_blocks`, such as Travel): decline, or propose another time.
- **A clash only with things you can move** (your own blocks, and meetings you organise): accept, and move
  them. Blocked-out time can usually move, unless it's prep with a due date; those belong
  in fixed_blocks.
- **All-day, or shown as free** (a deadline marker, a notice): it holds no time, so it's never judged on
  clashes; accept, unless a rule skips it.
- **No clash:** accept, noting a new recurring series (it costs more than a one-off), being optional or
  invited through a group, a broadcast (over 50 people) or an outside organiser.
- **[[invite_rule]] "skip"** drops what needs no response at all: a colleague's leave sent as an invite, a
  webinar to a distribution list. **Nothing answers on its own yet:** rules that act come later, one class at
  a time, once the advice has proved itself.

An invite is raised once: a recurring series once for the whole series, a one-off once per start time (so a
moved meeting comes back). Answering one takes it off the queue. Graph with Calendars.ReadWrite.
"""

from __future__ import annotations

import datetime as dt
import re
import urllib.parse

from .config import Config, InviteRule

FIELDS = ("id,subject,start,end,isAllDay,showAs,isCancelled,isOrganizer,responseStatus,organizer,attendees,"
          "type,seriesMasterId,webLink,bodyPreview")
PENDING = ("none", "notResponded")
SCOPES = ["Calendars.ReadWrite"]
COMMITTED = ("accepted", "tentativelyAccepted", "organizer")


class Calendar:
    def __init__(self, graph, timezone: str = "GMT Standard Time"):
        self.graph, self.timezone = graph, timezone

    def events(self, start: dt.date, days: int) -> list:
        query = urllib.parse.urlencode({
            "startDateTime": f"{start.isoformat()}T00:00:00",
            "endDateTime": f"{(start + dt.timedelta(days=days)).isoformat()}T00:00:00",
            "$top": "200", "$orderby": "start/dateTime", "$select": FIELDS})
        return list(self.graph.pages(f"/me/calendarView?{query}", scopes=SCOPES,
                                     headers={"Prefer": f'outlook.timezone="{self.timezone}"'}))

    def respond(self, event_id: str, action: str, note: str = "", send: bool = True) -> None:
        verb = {"accept": "accept", "tentative": "tentativelyAccept", "decline": "decline"}[action]
        body = {"sendResponse": send, **({"comment": note} if note else {})}
        self.graph.request(f"/me/events/{event_id}/{verb}", "POST", body, scopes=SCOPES)

    def move(self, event_id: str, start: dt.datetime, end: dt.datetime) -> None:
        when = lambda t: {"dateTime": t.strftime("%Y-%m-%dT%H:%M:00"), "timeZone": self.timezone}   # noqa: E731
        self.graph.request(f"/me/events/{event_id}", "PATCH", {"start": when(start), "end": when(end)}, scopes=SCOPES)

    def event(self, event_id: str) -> dict:
        return self.graph.request(f"/me/events/{event_id}?$select={FIELDS}", scopes=SCOPES,
                                  headers={"Prefer": f'outlook.timezone="{self.timezone}"'})


def _times(event: dict) -> tuple:
    return (dt.datetime.fromisoformat(event["start"]["dateTime"][:19]), dt.datetime.fromisoformat(event["end"]["dateTime"][:19]))


def _people(event: dict) -> list:
    return [a for a in event.get("attendees") or [] if a.get("type") != "resource"]


def _organiser(event: dict) -> tuple:
    who = (event.get("organizer") or {}).get("emailAddress") or {}
    return who.get("name") or "", (who.get("address") or "").lower()


def my_place(event: dict, email: str) -> str:
    """required, optional, or group (you're not on the list by name: a distribution list)."""
    mine = next((a for a in _people(event) if ((a.get("emailAddress") or {}).get("address") or "").lower() == email), None)
    return mine.get("type", "required") if mine else "group"


def leave_days(config: Config, events: list) -> set:
    pattern = re.compile(config.invites.leave_pattern, re.I)
    days = set()
    for event in events:
        name, address = _organiser(event)
        if address != config.email or event.get("isCancelled") or not pattern.match(event.get("subject") or ""):
            continue
        start, end = _times(event)
        day = start.date()
        while day <= (end - dt.timedelta(minutes=1)).date():
            days.add(day)
            day += dt.timedelta(days=1)
    return days


def matches(rule: InviteRule, event: dict, email: str) -> bool:
    name, address = _organiser(event)
    checks = [
        rule.subject is None or re.search(rule.subject, event.get("subject") or "", re.I),
        rule.organiser is None or re.search(rule.organiser, f"{name} {address}", re.I),
        rule.all_day is None or bool(event.get("isAllDay")) == rule.all_day,
        rule.show_as is None or event.get("showAs") in rule.show_as,
        rule.via_group is None or (my_place(event, email) == "group") == rule.via_group,
        rule.optional is None or (my_place(event, email) == "optional") == rule.optional,
        rule.min_people is None or len(_people(event)) >= rule.min_people,
    ]
    return all(checks)


def commitments(config: Config, events: list) -> list:
    """What you've already given time to: meetings you organise or have accepted (tentative counts, as
    "soft"), and your own blocks. All-day markers and anything shown as free don't hold time."""
    out = []
    for e in events:
        response = (e.get("responseStatus") or {}).get("response")
        if e.get("isCancelled") or e.get("isAllDay") or e.get("showAs") == "free" or response not in COMMITTED:
            continue
        subject = e.get("subject") or ""
        own = bool(e.get("isOrganizer"))
        block = own and not any(((a.get("emailAddress") or {}).get("address") or "").lower() != config.email for a in _people(e))
        if any(re.search(p, subject, re.I) for p in config.invites.protected):
            kind = "protected"
        elif block:
            kind = "fixed block" if any(re.search(p, subject, re.I) for p in config.invites.fixed_blocks) else "your block"
        elif own:
            kind = "your meeting"
        else:
            kind = "tentative" if response == "tentativelyAccepted" else "accepted"
        start, end = _times(e)
        out.append({"id": e["id"], "subject": subject, "start": start, "end": end, "kind": kind})
    return out


MOVABLE = ("your block", "your meeting")


def clashes(event: dict, held: list) -> list:
    start, end = _times(event)
    return [c for c in held if c["id"] != event["id"] and c["start"] < end and c["end"] > start]


def assess(config: Config, event: dict, held: list, away: set, series_events: list) -> dict:
    """The recommendation for one invite (for a series, across its occurrences in the window)."""
    occurrences = series_events or [event]
    on_leave = [o for o in occurrences if _times(o)[0].date() in away]
    hits = [(o, c) for o in occurrences for c in clashes(o, held)]
    fixed = [(o, c) for o, c in hits if c["kind"] not in MOVABLE]
    movable = [(o, c) for o, c in hits if c["kind"] in MOVABLE]
    notes = []
    place = my_place(event, config.email)
    if place != "required":
        notes.append("you're optional" if place == "optional" else "you're invited through a group")
    if event.get("type") in ("occurrence", "exception", "seriesMaster"):
        notes.append(f"a recurring series ({len(occurrences)} in the next {config.invites.days_ahead} days)")
    if len(_people(event)) > 50:
        notes.append(f"a broadcast ({len(_people(event))} people)")
    name, address = _organiser(event)
    if config.email and address and address.split("@")[-1] != config.email.split("@")[-1]:
        notes.append("an outside organiser")

    def label(o, c):
        return f"{c['subject']} ({c['kind']}, {c['start']:%a %d %b %H:%M}–{c['end']:%H:%M})"

    if event.get("isAllDay") or event.get("showAs") == "free":
        # A marker or a notice (a deadline, someone's leave): it holds no time, so nothing clashes with it.
        rec, why, hits = "accept", "holds no time (all-day or shown as free): a marker, not a meeting", []
    elif on_leave and len(on_leave) == len(occurrences):
        rec, why = "decline", "you're on leave"
    elif fixed and len({id(o) for o, _ in fixed}) == len(occurrences):
        rec, why = "decline", "clashes with " + "; ".join(sorted({label(o, c) for o, c in fixed}))
    elif movable and not fixed:
        rec, why = "accept and move", "clashes only with things you can move: " + "; ".join(sorted({label(o, c) for o, c in movable}))
    elif fixed or on_leave:
        part = [f"{len(on_leave)} on leave"] if on_leave else []
        part += [f"{len({id(o) for o, _ in fixed})} clash: " + "; ".join(sorted({label(o, c) for o, c in fixed}))] if fixed else []
        rec, why = "accept, and decline the clashing ones", f"of {len(occurrences)} occurrences, " + "; ".join(part)
    else:
        rec, why = "accept", "no clash"
    return {"recommendation": rec, "why": why + (f" ({'; '.join(notes)})" if notes else ""),
            "clashes": [{"id": c["id"], "subject": c["subject"], "start": c["start"].isoformat(timespec="minutes"),
                         "end": c["end"].isoformat(timespec="minutes"), "kind": c["kind"]} for _, c in hits]}


def key(event: dict) -> str:
    """A series is raised once; a one-off once per start time, so a moved meeting comes back."""
    if event.get("seriesMasterId"):
        return f"invite:{event['seriesMasterId']}"
    return f"invite:{event['id']}@{event['start']['dateTime'][:16]}"


def triage(config: Config, calendar: Calendar, raised: dict, log=print, now: dt.datetime | None = None) -> tuple:
    """(new queue items, the keys of every pending invite seen, skipped count)."""
    now = now or dt.datetime.now()
    events = calendar.events(now.date(), config.invites.days_ahead)
    away, held = leave_days(config, events), commitments(config, events)
    pending = [e for e in events if not e.get("isOrganizer") and not e.get("isCancelled")
               and (e.get("responseStatus") or {}).get("response") in PENDING and _times(e)[0] > now]
    series: dict = {}
    for e in pending:
        series.setdefault(key(e), []).append(e)
    items, seen, skipped = [], set(), 0
    for k, group in series.items():
        seen.add(k)
        first = group[0]
        if k in raised:
            continue
        rule = next((r for r in config.invites.rules if matches(r, first, config.email)), None)
        if rule and rule.action == "skip":
            skipped += 1
            raised[k] = dt.datetime.now().isoformat(timespec="seconds")
            log(f"invite needs no response  [{rule.name}] {first.get('subject')}")
            continue
        verdict = assess(config, first, held, away, group if first.get("seriesMasterId") else [])
        start, end = _times(first)
        name, address = _organiser(first)
        items.append({"folder": "Calendar", "type": "invite", "id": first.get("seriesMasterId") or first["id"], "key": k,
                      "occurrence": first["id"], "received": start.isoformat(timespec="minutes"), "from": name or address,
                      "subject": f"{first.get('subject') or '(no subject)'}: {start:%a %d %b %H:%M}–{end:%H:%M}",
                      "preview": (first.get("bodyPreview") or "")[:300], "rule": rule.name if rule else "invite",
                      "check": f"{verdict['recommendation'].upper()}: {verdict['why']}",
                      "recommendation": verdict["recommendation"], "clashes": verdict["clashes"],
                      "webLink": first.get("webLink")})
        raised[k] = dt.datetime.now().isoformat(timespec="seconds")
        log(f"invite queued  {first.get('subject')} ({start:%a %d %b %H:%M}): {verdict['recommendation']}")
    return items, seen, skipped


def leave_sweep(config: Config, calendar: Calendar, start: dt.date, days: int) -> list:
    """Meetings others organise that fall in your leave and that you haven't declined: what to decline before you go."""
    events = calendar.events(start, days)
    away = leave_days(config, events)
    return [e for e in events if not e.get("isOrganizer") and not e.get("isCancelled") and not e.get("isAllDay")
            and _times(e)[0].date() in away and (e.get("responseStatus") or {}).get("response") != "declined"]
