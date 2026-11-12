"""Your weekly 1:1s with your reports, cancelled or moved when either of you is on leave (12 Nov 2026).

The first rule that changes your own meetings rather than answering someone else's. In its owner's words:
"if there's a free slot just move it, you don't have to ask me". So, for each future occurrence of a 1:1
named in [[one_to_one]] (a meeting you organise, with that person in it), in the next `days_ahead` days:

- **Nobody off at the meeting's time:** nothing. **A half day only counts if it overlaps the meeting** (AM is
  before `midday`, PM after it). A half day whose half isn't known can't be judged, so it's raised.
- **Someone off, for at least `cancel_days` of that Monday-to-Friday week** (bank holidays count as days off,
  a half day as half): the occurrence is cancelled, with a note saying why. A week mostly away isn't worth
  rescheduling into.
- **Someone off, for less of the week:** the occurrence moves, unasked, to the nearest free slot in the same
  week: the same length, within `working_hours`, on a day neither of you is off at all, with nothing that
  holds time (busy, out of office, tentative) in your calendar or theirs (their free/busy, through
  getSchedule). Nearest by time from the original start; on a tie, the later slot.
- **No such slot:** nothing changes; it's queued for you, once, with the reason and a suggestion: cancel, or
  the nearest free slot in the week before or after.

Who's off where:

- **A report's leave** is your own diary entries named `<Full Name> annual leave` or `<Full Name> on leave`,
  optionally with a half: `on leave PM`, `annual leave (AM)`, or for leave over several days `(from PM)` (the
  first day is the afternoon only) and `(to AM)` (the last day is the morning only). A bare `half` is a half
  day of unknown half.
- **Your leave** is your entries matching [invites] `leave_pattern`. People First enters it as `Holiday (<job
  title>)`, 00:00 to 23:59 and not all-day, so every day it touches counts as a full day; `Holiday (half day)
  (<job title>)` is a half day of unknown half.
- **Bank holidays** are your all-day entries in a `bank_holiday_categories` category (Outlook's UK holidays
  are in "Holiday": Christmas Day, Boxing Day...), and your entries named "Bank holiday". Both of you are off.

**Never the series, only the occurrence**, and never anything `protected` (your manager's 1:1, if you send
it, would otherwise look like one of yours). **`act = false` only records what it would do**: each "would
cancel" or "would move" is listed once by `mail-triage deleted`, as each real one is. At most `max_actions`
changes a run. It's idempotent by construction: a cancelled occurrence is gone, and a moved one no longer
clashes with the leave.
"""

from __future__ import annotations

import datetime as dt
import re

from .config import Config, OneToOne
from .invites import HOLDS_TIME, STEP, _organiser, _people, _times

FULL, AM, PM, HALF = "full", "AM", "PM", "half"   # HALF: a half day, but which half isn't known
WEEKDAYS = 5


def _markers(text: str) -> tuple:
    """(each day, first day, last day) halves in a leave entry's subject: "PM", "(AM)", "(from PM)", "(to AM)",
    or "half day" with no half given."""
    first = re.search(r"\bfrom\s+(am|pm)\b", text, re.I)
    last = re.search(r"\bto\s+(am|pm)\b", text, re.I)
    rest = re.sub(r"\b(from|to)\s+(am|pm)\b", " ", text, flags=re.I)
    each = re.search(r"\b(am|pm)\b", rest, re.I)
    each = each.group(1).upper() if each else (HALF if re.search(r"\bhalf\b", rest, re.I) else None)
    return each, first.group(1).upper() if first else None, last.group(1).upper() if last else None


def _merge(days: dict, day: dt.date, kind: str) -> None:
    """Two entries on one day: the morning and the afternoon make a full day; anything with a full day is one."""
    old = days.get(day)
    days[day] = kind if old in (None, kind) else (old if FULL in (old, kind) else FULL)


def _spread(days: dict, event: dict, each: str | None, first: str | None, last: str | None) -> None:
    start, end = _times(event)
    span = []
    day = start.date()
    while day <= (end - dt.timedelta(minutes=1)).date():
        span.append(day)
        day += dt.timedelta(days=1)
    for i, day in enumerate(span):
        kind = each or FULL
        if i == 0 and first:
            kind = first
        if i == len(span) - 1 and last:
            kind = last
        _merge(days, day, kind)


def _mine(config: Config, event: dict) -> bool:
    return _organiser(event)[1] == config.email and not event.get("isCancelled")


def bank_holidays(config: Config, events: list) -> set:
    cats = {c.lower() for c in config.one_to_ones.bank_holiday_categories}
    days: dict = {}
    for e in events:
        if _mine(config, e) and ((e.get("isAllDay") and cats & {c.lower() for c in e.get("categories") or []})
                                 or re.match(r"bank holiday\b", e.get("subject") or "", re.I)):
            _spread(days, e, None, None, None)
    return set(days)


def my_leave(config: Config, events: list) -> dict:
    """Your leave, {day: full | AM | PM | half}. Only "half" makes a half day: a job title in People First's
    "Holiday (<job title>)" could say PM."""
    pattern = re.compile(config.invites.leave_pattern, re.I)
    days: dict = {}
    for e in events:
        subject = e.get("subject") or ""
        if not _mine(config, e) or not pattern.match(subject) or re.match(r"bank holiday\b", subject, re.I):
            continue
        each, first, last = _markers(subject) if re.search(r"\bhalf\b", subject, re.I) else (None, None, None)
        _spread(days, e, each, first, last)
    return days


def report_leave(config: Config, report: OneToOne, events: list) -> dict:
    """A report's leave, from your own entries named "<Full Name> annual leave" / "<Full Name> on leave"."""
    pattern = re.compile(config.one_to_ones.leave_pattern.replace("{name}", re.escape(report.name)), re.I)
    days: dict = {}
    for e in events:
        found = pattern.match(e.get("subject") or "") if _mine(config, e) else None
        if found:
            _spread(days, e, *_markers((e.get("subject") or "")[found.end():]))
    return days


def _off(away: dict, bank: set, day: dt.date) -> str | None:
    return FULL if day in bank else away.get(day)


def _hits(kind: str | None, start: dt.datetime, end: dt.datetime, midday: dt.time) -> bool | None:
    """Whether time off of this kind overlaps the meeting: None if it can't be told (which half isn't known)."""
    if kind is None:
        return False
    if kind == HALF:
        return None
    return kind == FULL or (kind == AM and start.time() < midday) or (kind == PM and end.time() > midday)


def days_off(away: dict, bank: set, monday: dt.date) -> float:
    total = 0.0
    for i in range(WEEKDAYS):
        kind = _off(away, bank, monday + dt.timedelta(days=i))
        total += 0 if kind is None else (1 if kind == FULL else 0.5)
    return total


def _monday(day: dt.date) -> dt.date:
    return day - dt.timedelta(days=day.weekday())


def _busy(events: list, meeting: dict) -> list:
    """Your time that's taken: anything busy, out of office or tentative, other than the 1:1 itself."""
    return [_times(e) for e in events if e["id"] != meeting["id"] and not e.get("isCancelled")
            and e.get("showAs") in HOLDS_TIME and (e.get("responseStatus") or {}).get("response") != "declined"]


def nearest(config: Config, start: dt.datetime, length: dt.timedelta, days: list, busy: list,
            now: dt.datetime) -> tuple | None:
    """The free slot nearest `start` on these days, within working hours; on a tie, the later one."""
    hours = config.invites.working_hours
    options = []
    for day in days:
        t = dt.datetime.combine(day, dt.time.fromisoformat(hours[0]))
        close = dt.datetime.combine(day, dt.time.fromisoformat(hours[1]) if hours[1] != "24:00" else dt.time.max)
        while t + length <= close:
            if t >= now and t != start and not any(s < t + length and e > t for s, e in busy):
                options.append(t)
            t += STEP
    if not options:
        return None
    best = min(options, key=lambda t: (abs((t - start).total_seconds()), -t.timestamp()))
    return best, best + length


class _Schedules:
    """Each report's free/busy, a week at a time, fetched once a run. None: it couldn't be read."""
    def __init__(self, calendar):
        self.calendar, self.cache = calendar, {}

    def week(self, address: str, monday: dt.date, meeting: tuple) -> list | None:
        k = (address, monday)
        if k not in self.cache:
            start = dt.datetime.combine(monday, dt.time())
            self.cache[k] = self.calendar.schedule(address, start, start + dt.timedelta(days=WEEKDAYS))
        items = self.cache[k]
        if items is None:
            return None
        return [(s, e) for s, e, status in items if status in HOLDS_TIME and (s, e) != meeting]


def _free_days(monday: dt.date, mine: dict, theirs: dict, bank: set) -> list:
    """The weekdays neither of you is off at all (a half day rules out the day)."""
    days = [monday + dt.timedelta(days=i) for i in range(WEEKDAYS)]
    return [d for d in days if d not in bank and d not in mine and d not in theirs]


def _slot(config, meeting, report, monday, mine, theirs, bank, events, schedules, now):
    start, end = _times(meeting)
    days = _free_days(monday, mine, theirs, bank)
    if not days:
        return None, "no day that week when neither of you is off"
    their_busy = schedules.week(report.email, monday, (start, end))
    if their_busy is None:
        return None, f"couldn't read {report.name}'s calendar"
    slot = nearest(config, start, end - start, days, _busy(events, meeting) + their_busy, now)
    return slot, None if slot else "no time that week when you're both free"


def plan(config: Config, calendar, now: dt.datetime | None = None, days_ahead: int | None = None) -> list:
    """What to do with each 1:1 occurrence someone is off for. Reads only; changes nothing. Each decision:
    {"meeting", "report", "action": cancel | move | raise, "why", "note" (cancel), "to" (move),
    "suggest" (raise)}."""
    now = now or dt.datetime.now()
    settings = config.one_to_ones
    ahead = days_ahead or settings.days_ahead or config.invites.days_ahead
    first = _monday(now.date())
    last = _monday(now.date() + dt.timedelta(days=ahead)) + dt.timedelta(days=14)   # a week either side, to suggest
    events = calendar.events(first, (last - first).days)
    bank, mine = bank_holidays(config, events), my_leave(config, events)
    midday = dt.time.fromisoformat(settings.midday)
    schedules = _Schedules(calendar)
    out = []
    for report in settings.reports:
        theirs = report_leave(config, report, events)
        for meeting in events:
            start, end = _times(meeting)
            subject = meeting.get("subject") or ""
            if not meeting.get("isOrganizer") or meeting.get("isCancelled") or meeting.get("isAllDay") \
                    or not re.fullmatch(re.escape(report.subject), subject.strip(), re.I) \
                    or any(re.search(p, subject, re.I) for p in config.invites.protected) \
                    or report.email not in {((a.get("emailAddress") or {}).get("address") or "").lower() for a in _people(meeting)} \
                    or start <= now or start.date() >= now.date() + dt.timedelta(days=ahead):
                continue
            day, monday = start.date(), _monday(start.date())
            me_hit = _hits(_off(mine, bank, day), start, end, midday)
            them_hit = _hits(_off(theirs, bank, day), start, end, midday)
            decision = {"meeting": meeting, "report": report, "start": start, "end": end}
            if not me_hit and not them_hit:
                if me_hit is None or them_hit is None:
                    who = "you have" if me_hit is None else f"{report.name} has"
                    out.append({**decision, "action": "raise", "why": f"{who} a half day {day:%a %d %b} and which half isn't known",
                                "suggest": _suggest(config, meeting, report, monday, mine, theirs, bank, events, schedules, now)})
                continue
            counts = {"me": days_off(mine, bank, monday), "them": days_off(theirs, bank, monday)}
            absent = [p for p, hit in (("me", me_hit), ("them", them_hit)) if hit]
            reason = "the bank holiday" if day in bank else "; ".join(
                ("you're on leave" if p == "me" else f"{report.name} is on leave") for p in absent)
            week = ", ".join(f"{'you' if p == 'me' else report.name.split()[0]} off {counts[p]:g} of {WEEKDAYS} days that week" for p in absent)
            why = f"{reason} {day:%a %d %b} ({week})"
            heavy = [p for p in absent if counts[p] >= settings.cancel_days]
            if heavy:
                note = settings.bank_holiday_note if day in bank else (settings.my_note if "me" in heavy else settings.report_note)
                out.append({**decision, "action": "cancel", "why": why, "note": note})
                continue
            slot, problem = _slot(config, meeting, report, monday, mine, theirs, bank, events, schedules, now)
            if slot:
                out.append({**decision, "action": "move", "why": why, "to": slot})
            else:
                out.append({**decision, "action": "raise", "why": f"{why}: {problem}",
                            "suggest": _suggest(config, meeting, report, monday, mine, theirs, bank, events, schedules, now)})
    return sorted(out, key=lambda d: d["start"])


def _suggest(config, meeting, report, monday, mine, theirs, bank, events, schedules, now) -> str:
    """For one that can't be settled within the rules: cancel, or the nearest free slot a week either side."""
    start, _ = _times(meeting)
    found = []
    for other in (monday - dt.timedelta(days=7), monday + dt.timedelta(days=7)):
        slot, _ = _slot(config, meeting, report, other, mine, theirs, bank, events, schedules, now)
        if slot:
            found.append(slot)
    if not found:
        return "cancel it (no free slot in the week either side either)"
    best = min(found, key=lambda s: (abs((s[0] - start).total_seconds()), -s[0].timestamp()))
    return f"cancel it, or move it to {best[0]:%a %d %b %H:%M}–{best[1]:%H:%M} (outside its week)"


def describe(d: dict) -> str:
    when = f"{d['start']:%a %d %b %H:%M}–{d['end']:%H:%M}"
    if d["action"] == "cancel":
        return f"{when}  {d['report'].name}: CANCEL ({d['why']}) note: \"{d['note']}\""
    if d["action"] == "move":
        return f"{when}  {d['report'].name}: MOVE to {d['to'][0]:%a %d %b %H:%M}–{d['to'][1]:%H:%M} ({d['why']})"
    return f"{when}  {d['report'].name}: ASK ({d['why']}). Suggest: {d['suggest']}"


def run(config: Config, calendar, raised: dict, log=print, dry_run: bool = False,
        now: dt.datetime | None = None) -> tuple:
    """Act on plan() (or, with act = false, record what it would do). (queue items, acted, keys seen).
    Each item in acted has "rule", for the deleted record. raised remembers what was queued, and each
    would-do already recorded, so neither comes back run after run."""
    settings = config.one_to_ones
    items, acted, seen, changes = [], [], set(), 0
    for d in plan(config, calendar, now):
        meeting, start, end = d["meeting"], d["start"], d["end"]
        subject = f"{meeting.get('subject')}: {start:%a %d %b %H:%M}–{end:%H:%M}"
        if d["action"] == "raise":
            k = f"one-to-one:{meeting['id']}@{start:%Y-%m-%dT%H:%M}"
            seen.add(k)
            if k not in raised:
                items.append({"folder": "Calendar", "type": "one-to-one", "id": meeting["id"], "key": k,
                              "received": start.isoformat(timespec="minutes"), "from": d["report"].name,
                              "subject": subject, "preview": "", "rule": "1:1 and leave",
                              "check": f"DECIDE: {d['why']}. Suggest: {d['suggest']}", "webLink": meeting.get("webLink")})
                raised[k] = dt.datetime.now().isoformat(timespec="seconds")
                log(f"{'would queue' if dry_run else 'queued'}  [1:1 and leave] {describe(d)}")
            continue
        did = (f"{d['why']}; note: \"{d['note']}\"" if d["action"] == "cancel"
               else f"{d['why']}; moved to {d['to'][0]:%a %d %b %H:%M}–{d['to'][1]:%H:%M}")
        would = f"one-to-one-would:{meeting['id']}@{start:%Y-%m-%dT%H:%M}:{d['action']}" + \
                (f"@{d['to'][0]:%Y-%m-%dT%H:%M}" if d["action"] == "move" else "")
        if not settings.act and would in raised:
            continue   # already recorded as a would-do
        if changes >= settings.max_actions:
            log(f"one-to-ones: max_actions ({settings.max_actions}) reached; the rest wait for the next run")
            break
        changes += 1
        verb = {"cancel": "cancelled", "move": "moved"}[d["action"]]
        if settings.act and not dry_run:
            if d["action"] == "cancel":
                calendar.cancel(meeting["id"], d["note"])     # the occurrence: the series is untouched
            else:
                calendar.move(meeting["id"], *d["to"])
            rule = f"1:1 {verb}: leave"
        else:
            rule = f"1:1 would be {verb}: leave (act = false)" if not settings.act else f"1:1 {verb}: leave"
            if not settings.act and not dry_run:
                raised[would] = dt.datetime.now().isoformat(timespec="seconds")
        acted.append({"received": start.isoformat(timespec="minutes"), "from": d["report"].name, "subject": subject,
                      "rule": rule, "why": did})
        log(f"{'would be ' + verb if dry_run or not settings.act else verb}  [1:1 and leave] {describe(d)}")
    return items, acted, seen
