"""Meeting invites: raised with a recommendation, answered on your say-so.

Who sent an invite says little about whether to go, so an invite is judged on time and context instead. On
three months of one person's calendar (10 Nov 2026), **every decline was either leave (10) or a clash with
something already accepted (17)**; with no clash, 78 of 100 invites were accepted. So:

- **Leave:** decline, with a note ("On annual leave", or "On annual leave 16/12 to 31/12" when it's longer
  than a day). Leave is your own diary entries named as leave (leave_pattern).
- **A clash with something fixed:** decline. Fixed is someone else's meeting you've accepted, anything
  `protected`, a block in `fixed_blocks`, an out-of-office block, and a `commute_blocks` block (travel) in
  the middle of the day.
- **A clash only with things you can move:** accept, and move them; each comes with a free slot to move it
  to. Movable is your own blocks, meetings you organise, and travel at the start or end of the day (you can
  come in earlier or leave later). A `[[movable_block]]` limits where a block can go: `within = "day"` keeps
  it on its day, `before = '<subject>'` keeps it before the next meeting of that name. **No free slot within
  the limits makes it fixed.**
- **All-day, or shown as free** (a deadline marker, a notice): it holds no time, so it's never judged on
  clashes; accept, unless a rule skips it.
- **No clash:** accept, noting a new recurring series (it costs more than a one-off), being optional or
  invited through a group, a broadcast (over 50 people) or an outside organiser.
- **[[invite_rule]] "skip"** drops what needs no response at all: a colleague's leave sent as an invite, a
  webinar to a distribution list. **Nothing answers on its own yet:** rules that act come later, one class at
  a time, once the advice has proved itself.

**A decline is for the instance, never the series**, unless you ask for the series: declining a series
removes every future occurrence from your calendar. A series is accepted as a whole.

An invite is raised once: a recurring series once for the whole series, a one-off once per start time (so a
moved meeting comes back). Answering one takes it off the queue. Graph with Calendars.ReadWrite.
"""

from __future__ import annotations

import datetime as dt
import re
import urllib.parse

from .config import Config, InviteRule

FIELDS = ("id,subject,start,end,isAllDay,showAs,isCancelled,isOrganizer,responseStatus,responseRequested,"
          "organizer,attendees,type,seriesMasterId,webLink,bodyPreview")
PENDING = ("none", "notResponded")
SCOPES = ["Calendars.ReadWrite"]
COMMITTED = ("accepted", "tentativelyAccepted", "organizer")
MOVABLE = ("your block", "your meeting", "travel")
STEP = dt.timedelta(minutes=15)


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
        body = {"sendResponse": send, **({"comment": note} if note and send else {})}
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


def _any(patterns: list, text: str) -> bool:
    return any(re.search(p, text, re.I) for p in patterns)


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


def leave_note(config: Config, day: dt.date, away: set) -> str:
    """"On annual leave", or "On annual leave 16/12 to 31/12" for leave longer than a day. A weekend inside
    the leave doesn't break it; one at either end isn't counted."""
    def inside(d):
        return d in away or (d.weekday() >= 5 and any(x in away for x in (d - dt.timedelta(days=2), d - dt.timedelta(days=1)))
                             and any(x in away for x in (d + dt.timedelta(days=1), d + dt.timedelta(days=2))))
    first = last = day
    while inside(first - dt.timedelta(days=1)):
        first -= dt.timedelta(days=1)
    while inside(last + dt.timedelta(days=1)):
        last += dt.timedelta(days=1)
    note = config.invites.leave_note
    return note if first == last else f"{note} {first.strftime(config.invites.date_format)} to {last.strftime(config.invites.date_format)}"


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
        if _any(config.invites.protected, subject):
            kind = "protected"
        elif block and _any(config.invites.commute_blocks, subject):
            kind = "travel"   # decided below: movable only at either end of the day
        elif block and (_any(config.invites.fixed_blocks, subject) or e.get("showAs") == "oof"):
            kind = "fixed block"
        elif block:
            kind = "your block"
        elif own:
            kind = "your meeting"
        else:
            kind = "tentative" if response == "tentativelyAccepted" else "accepted"
        start, end = _times(e)
        out.append({"id": e["id"], "subject": subject, "start": start, "end": end, "kind": kind})
    # Travel at the start or end of the day can move (come in earlier, leave later); in the middle, it can't.
    for c in out:
        if c["kind"] == "travel":
            day = [o for o in out if o["start"].date() == c["start"].date() and o["end"].date() == c["start"].date()]
            if c["start"] <= min(o["start"] for o in day):
                c["edge"] = "start"
            elif c["end"] >= max(o["end"] for o in day):
                c["edge"] = "end"
            else:
                c["kind"] = "travel mid-day"
    return out


def clashes(event: dict, held: list) -> list:
    start, end = _times(event)
    return [c for c in held if c["id"] != event["id"] and c["start"] < end and c["end"] > start]


def free_slot(config: Config, c: dict, held: list, avoid: tuple, away: set, now: dt.datetime) -> tuple | None:
    """Where a movable clash could go: the nearest free slot of the same length, within its limits."""
    length = c["end"] - c["start"]
    limit = next((m for m in config.invites.movable if re.search(m.subject, c["subject"], re.I)), None)
    deadline, days = None, [c["start"].date()]
    hours = config.invites.travel_hours if c["kind"] == "travel" else config.invites.working_hours
    if c["kind"] != "travel" and not (limit and limit.within == "day"):
        if limit and limit.before:
            later = [h["start"] for h in held if h["start"] > c["start"] and re.search(limit.before, h["subject"], re.I)]
            deadline = min(later) if later else None
            if deadline:
                days = [c["start"].date() + dt.timedelta(days=i) for i in range((deadline.date() - c["start"].date()).days + 1)]
        else:
            days += [c["start"].date() + dt.timedelta(days=i) for i in range(1, 8)]
    busy = [(h["start"], h["end"]) for h in held if h["id"] != c["id"]] + [avoid]
    for day in days:
        if day.weekday() >= 5 or day in away:
            continue
        open_at = dt.datetime.combine(day, dt.time.fromisoformat(hours[0]))
        close_at = dt.datetime.combine(day, dt.time.fromisoformat(hours[1]))
        options = []
        t = open_at
        while t + length <= close_at:
            # Travel in the morning only moves earlier (come in earlier); in the evening, only later (leave later).
            direction = {"start": t < c["start"], "end": t > c["start"]}.get(c.get("edge"), True)
            if t >= now and direction and (deadline is None or t + length <= deadline) and t != c["start"] \
                    and not any(s < t + length and e > t for s, e in busy):
                options.append(t)
            t += STEP
        if options:
            best = min(options, key=lambda t: abs((t - c["start"]).total_seconds()))
            return best, best + length
    return None


def assess(config: Config, event: dict, held: list, away: set, series_events: list,
           now: dt.datetime | None = None) -> dict:
    """The recommendation for one invite (for a series, across its occurrences in the window)."""
    now = now or dt.datetime.now()
    occurrences = series_events or [event]
    on_leave = [o for o in occurrences if _times(o)[0].date() in away]
    hits = []
    for o in occurrences:
        for c in clashes(o, held):
            c = dict(c)
            if c["kind"] in MOVABLE:
                slot = free_slot(config, c, held, _times(o), away, now)
                if slot:
                    c["to"] = slot
                else:
                    c["kind"] += ", no free slot to move it to"
            hits.append((o, c))
    fixed = [(o, c) for o, c in hits if "to" not in c]
    movable = [(o, c) for o, c in hits if "to" in c]
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

    def label(c):
        moved = f" -> {c['to'][0]:%a %d %b %H:%M}–{c['to'][1]:%H:%M}" if "to" in c else ""
        return f"{c['subject']} ({c['kind']}, {c['start']:%a %d %b %H:%M}–{c['end']:%H:%M}{moved})"

    decline, note = [], ""
    if event.get("isAllDay") or event.get("showAs") == "free":
        # A marker or a notice (a deadline, someone's leave): it holds no time, so nothing clashes with it.
        rec, why, hits = "accept", "holds no time (all-day or shown as free): a marker, not a meeting", []
    elif on_leave and len(on_leave) == len(occurrences):
        rec, why = "decline", "you're on leave"
        decline = [o["id"] for o in occurrences]
        note = leave_note(config, _times(on_leave[0])[0].date(), away)
    elif fixed and len({id(o) for o, _ in fixed}) == len(occurrences):
        rec, why = "decline", "clashes with " + "; ".join(sorted({label(c) for _, c in fixed}))
        decline = [o["id"] for o in occurrences]
    elif movable and not fixed and not on_leave:
        rec, why = "accept and move", "clashes only with things you can move: " + "; ".join(sorted({label(c) for _, c in movable}))
    elif fixed or on_leave:
        part = [f"{len(on_leave)} on leave"] if on_leave else []
        part += [f"{len({id(o) for o, _ in fixed})} clash: " + "; ".join(sorted({label(c) for _, c in fixed}))] if fixed else []
        rec, why = "accept, and decline the clashing ones", f"of {len(occurrences)} occurrences, " + "; ".join(part)
        decline = sorted({o["id"] for o in on_leave} | {o["id"] for o, _ in fixed})
        note = leave_note(config, _times(on_leave[0])[0].date(), away) if on_leave and not fixed else ""
    else:
        rec, why = "accept", "no clash"
    return {"recommendation": rec, "why": why + (f" ({'; '.join(notes)})" if notes else ""), "decline": decline,
            "note": note,
            "clashes": [{"id": c["id"], "subject": c["subject"], "start": c["start"].isoformat(timespec="minutes"),
                         "end": c["end"].isoformat(timespec="minutes"), "kind": c["kind"],
                         **({"to": c["to"][0].isoformat(timespec="minutes"), "to_end": c["to"][1].isoformat(timespec="minutes")}
                            if "to" in c else {})} for _, c in hits]}


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
        verdict = assess(config, first, held, away, group if first.get("seriesMasterId") else [], now)
        start, end = _times(first)
        name, address = _organiser(first)
        items.append({"folder": "Calendar", "type": "invite", "id": first.get("seriesMasterId") or first["id"], "key": k,
                      "series": first.get("seriesMasterId"), "occurrence": first["id"],
                      "occurrences": [e["id"] for e in group], "responseRequested": first.get("responseRequested", True),
                      "received": start.isoformat(timespec="minutes"), "from": name or address,
                      "subject": f"{first.get('subject') or '(no subject)'}: {start:%a %d %b %H:%M}–{end:%H:%M}",
                      "preview": (first.get("bodyPreview") or "")[:300], "rule": rule.name if rule else "invite",
                      "check": f"{verdict['recommendation'].upper()}: {verdict['why']}"
                               + (f' | note: "{verdict["note"]}"' if verdict["note"] else ""),
                      "recommendation": verdict["recommendation"], "decline": verdict["decline"],
                      "note": verdict["note"], "clashes": verdict["clashes"], "webLink": first.get("webLink")})
        raised[k] = dt.datetime.now().isoformat(timespec="seconds")
        log(f"invite queued  {first.get('subject')} ({start:%a %d %b %H:%M}): {verdict['recommendation']}")
    return items, seen, skipped


def leave_sweep(config: Config, calendar: Calendar, start: dt.date, days: int) -> list:
    """Meetings others organise that fall in your leave and that you haven't declined: what to decline before
    you go. Each is an occurrence (calendarView), so declining one never touches the rest of its series.
    Each carries its note under "note"."""
    events = calendar.events(start, days)
    away = leave_days(config, events)
    found = [e for e in events if not e.get("isOrganizer") and not e.get("isCancelled") and not e.get("isAllDay")
             and _times(e)[0].date() in away and (e.get("responseStatus") or {}).get("response") != "declined"]
    for e in found:
        e["note"] = leave_note(config, _times(e)[0].date(), away)
    return found
