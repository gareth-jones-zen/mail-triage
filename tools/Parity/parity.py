"""Offline parity checks between the Python mail-triage (the oracle, from tag v0.9.1) and the .NET port.

Never runs the live tool, never calls Graph: everything here is generated, or read from copies.

    set PYTHONPATH=<a checkout of v0.9.1>\\src
    python tools/parity/parity.py gen <dir> [--count N] [--seed S]   generated scenarios
    python tools/parity/parity.py py <dir>                            Python's results for them
    dotnet run --project tools/Parity -- scenarios <dir>              .NET's results
    python tools/parity/parity.py compare <dir>                       the differences

Other checks: `toml <file>` and `settings <file>` print what tomllib and config.load make of a settings file;
`regex <corpus.json> <out.json>` runs re over a corpus; `lockkey <path>`; `state <in> <out>` rewrites a state
file through _load and _save.
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime as dt
import json
import os
import pathlib
import random
import re
import sys
import tomllib
import zoneinfo

import mail_triage
from mail_triage import config as cfg, invites, one_to_ones, triage

assert "mail-triage-dotnet" in mail_triage.__file__ or os.environ.get("PARITY_ANY_ORACLE"), \
    f"refusing to run against {mail_triage.__file__}: use the v0.9.1 worktree, never the live install"

ME = "alex@example.com"
LONDON = zoneinfo.ZoneInfo("Europe/London")


def dump(data) -> str:
    return json.dumps(data, ensure_ascii=False, default=_default)


def _default(value):
    if isinstance(value, (dt.datetime, dt.date, dt.time)):
        return value.isoformat()
    if isinstance(value, pathlib.Path):
        return str(value)
    if isinstance(value, (set, frozenset)):
        return sorted(value)
    if dataclasses.is_dataclass(value):
        return dataclasses.asdict(value)
    raise TypeError(type(value))


# ---- settings -----------------------------------------------------------------------------------------------------

def settings_dump(config: cfg.Config) -> dict:
    """The same shape as Settings.Dump() in the .NET port."""
    i, t = config.invites, config.one_to_ones
    return {
        "path": str(config.path),
        "folders": [{"name": f.name, "since": f.since, "unmatched": f.unmatched} for f in config.folders],
        "rules": [{"name": r.name, "sender": r.sender, "action": r.action, "match": r.match, "check": r.check, "why": r.why,
                   "to": r.to, "folders": r.folders} for r in config.rules],
        "flow_checks": [{"flow": f.flow, "output": f.output, "pending": f.pending} for f in config.flow_checks],
        "login_hint": config.login_hint, "tenant": config.tenant, "msal_cache": str(config.msal_cache) if config.msal_cache else None,
        "queue_file": str(config.queue_file), "state_file": str(config.state_file), "deleted_file": str(config.deleted_file),
        "accepted_file": str(config.accepted_file), "log_file": str(config.log_file), "notify": config.notify,
        "every_minutes": config.every_minutes, "hours": list(config.hours), "weekdays": config.weekdays, "email": config.email,
        "invites": None if not i else {
            "days_ahead": i.days_ahead, "fixed_blocks": i.fixed_blocks, "commute_blocks": i.commute_blocks,
            "movable": [{"subject": m.subject, "within": m.within, "before": m.before} for m in i.movable],
            "protected": i.protected, "leave_note": i.leave_note, "date_format": i.date_format,
            "working_hours": list(i.working_hours), "travel_hours": list(i.travel_hours), "requests_folders": i.requests_folders,
            "accept_shortened": i.accept_shortened, "proposals_folders": i.proposals_folders, "accept_proposals": i.accept_proposals,
            "leave_pattern": i.leave_pattern,
            "rules": [{"name": r.name, "action": r.action, "subject": r.subject, "organiser": r.organiser, "all_day": r.all_day,
                       "show_as": r.show_as, "via_group": r.via_group, "optional": r.optional, "min_people": r.min_people,
                       "why": r.why} for r in i.rules]},
        "one_to_ones": None if not t else {
            "reports": [{"name": r.name, "email": r.email, "subject": r.subject} for r in t.reports],
            "act": t.act, "max_actions": t.max_actions, "cancel_days": float(t.cancel_days), "midday": t.midday,
            "days_ahead": t.days_ahead, "bank_holiday_categories": t.bank_holiday_categories, "leave_pattern": t.leave_pattern,
            "report_note": t.report_note, "my_note": t.my_note, "bank_holiday_note": t.bank_holiday_note},
    }


# ---- fakes, as the Python tests have them -------------------------------------------------------------------------

class FakeCalendar:
    def __init__(self, items, busy=None):
        self.items, self.busy, self.log = items, busy or {}, []

    def events(self, start, days):
        return self.items

    def respond(self, event_id, action, note="", send=True):
        self.log.append(["respond", event_id, action, note, send])

    def move(self, event_id, start, end):
        self.log.append(["move", event_id, start.isoformat(), end.isoformat()])

    def cancel(self, event_id, comment=""):
        self.log.append(["cancel", event_id, comment])

    def schedule(self, address, start, end):
        self.log.append(["schedule", address, start.isoformat(), end.isoformat()])
        if address not in self.busy:
            return None   # couldn't be read
        return [(dt.datetime.fromisoformat(s), dt.datetime.fromisoformat(e), st) for s, e, st in self.busy[address]
                if dt.datetime.fromisoformat(s) < end and dt.datetime.fromisoformat(e) > start]

    def between(self, start, end):
        s, e = (t.astimezone(LONDON).replace(tzinfo=None) for t in (start, end))
        return [x for x in self.items if invites._times(x)[0] < e and invites._times(x)[1] > s]

    def zone(self):
        return LONDON


class FakeMailbox:
    def __init__(self, messages, full):
        self.messages, self.full, self.deleted = messages, full, []
        self.graph = self

    def folder_id(self, name):
        return name

    def pages(self, url, scopes=None):
        return iter(self.messages)

    def request(self, url, method="GET", body=None, scopes=None):
        mid = url.split("/")[3].split("?")[0]
        return self.full[mid]

    def delete(self, message_id):
        self.deleted.append(message_id)


def scrub_raised(raised: dict) -> list:
    return list(raised.keys())


# ---- generation ---------------------------------------------------------------------------------------------------

SETTINGS = f'''
email = "{ME}"
queue_file = "queue.json"
state_file = "raised.json"
deleted_file = "deleted.json"
log_file = "log.txt"

[invites]
days_ahead = {{days_ahead}}
commute_blocks = ['^Travel']
fixed_blocks = ['^Fixed']
protected = ['^Weekly 1-2-1 - Alex$']
accept_shortened = {{accept_shortened}}
proposals_folders = ['Inbox']
accept_proposals = {{accept_proposals}}
working_hours = "{{working}}"
travel_hours = "06:00-21:00"
date_format = "{{date_format}}"

[[movable_block]]
subject = '^Weekly update for Pat'
within = "day"

[[movable_block]]
subject = '^Prep for leadership'
before = '^Leadership weekly'

[[invite_rule]]
name = "Colleagues' leave and away notices"
subject = 'holiday|annual leave'
all_day = true
show_as = ["free", "oof"]
action = "skip"

[one_to_ones]
act = {{act}}
max_actions = {{max_actions}}
cancel_days = {{cancel_days}}
midday = "{{midday}}"
days_ahead = 42

[[one_to_one]]
name = "Jordan Lee"
email = "jordan@example.com"

[[one_to_one]]
name = "Robin Hall"
email = "Robin@Example.com"

[[one_to_one]]
name = "Zoë O'Neil-Smith"
email = "zoe@example.com"
subject = "Zoë 1:1"
'''

OWN = ["Focus time", "Travel", "Travel to site", "Weekly update for Pat", "Prep for leadership", "Fixed slot", "Lunch 🍕"]
OTHERS = ["Leadership weekly", "Workshop", "SMT", "Lunch and learn", "Quarterly review", "Weekly 1-2-1 - Alex", "Café review"]
INVITES = ["Design review", "Vendor demo", "All hands", "Retro", "Planning", "Social", "Leads sync", "Kurt Holiday - All Day",
           "Salary changes due", "Brief 😀", "Annual leave notice", "Site meeting"]
LEAVE = ["Holiday", "Holiday (Principal Architect)", "Holiday (half day) (Principal Architect)", "Annual leave", "Summer holidays; away",
         "Bank holiday", "leave"]


def ev(id, subject, start, end, *, response="notResponded", organiser="Sam Jones", org_addr="sam@example.com",
       attendees=(("Alex Smith", ME, "required"),), **extra):
    mine = org_addr == ME
    return {"id": id, "subject": subject, "isAllDay": extra.pop("isAllDay", False), "showAs": extra.pop("showAs", "busy"),
            "isCancelled": extra.pop("isCancelled", False), "isOrganizer": mine,
            "responseStatus": {"response": "organizer" if mine else response},
            "responseRequested": extra.pop("responseRequested", True),
            "organizer": {"emailAddress": {"name": organiser, "address": org_addr}},
            "attendees": [{"type": t, "emailAddress": {"name": n, "address": a}} for n, a, t in attendees],
            "start": {"dateTime": f"{start:%Y-%m-%dT%H:%M:%S}.0000000"}, "end": {"dateTime": f"{end:%Y-%m-%dT%H:%M:%S}.0000000"},
            "type": extra.pop("type", "singleInstance"), "seriesMasterId": extra.pop("seriesMasterId", None), **extra}


def quarter(r, day, lo=6, hi=20):
    return dt.datetime.combine(day, dt.time(r.randint(lo, hi - 1), r.choice([0, 15, 30, 45])))


def gen_settings(r, path: pathlib.Path):
    text = SETTINGS.format(days_ahead=r.choice([14, 30, 42]), accept_shortened=r.choice(["true", "false"]),
                           accept_proposals=r.choice(["true", "false"]),
                           working=r.choice(["08:00-18:00", "09:00-17:30", "07:30-19:00"]),
                           date_format=r.choice(["%d/%m", "%a %d %b", "%d %B %Y"]),
                           act=r.choice(["true", "false"]), max_actions=r.choice([1, 3, 20]), cancel_days=r.choice([2, 3, 2.5]),
                           midday=r.choice(["13:00", "12:30"]))
    path.write_text(text, encoding="utf-8")


def gen_calendar(r, monday: dt.date):
    items, n = [], 0
    def nid(prefix):
        nonlocal n
        n += 1
        return f"{prefix}-{n}"
    self_ = dict(organiser="Alex Smith", org_addr=ME, attendees=())
    for _ in range(r.randint(5, 25)):
        day = monday + dt.timedelta(days=r.randint(-2, 24))
        start = quarter(r, day)
        end = start + dt.timedelta(minutes=r.choice([15, 30, 45, 60, 90, 180]))
        items.append(ev(nid("own"), r.choice(OWN), start, end, showAs=r.choice(["busy", "busy", "oof", "free", "tentative"]), **self_))
    for _ in range(r.randint(3, 15)):
        day = monday + dt.timedelta(days=r.randint(-2, 24))
        start = quarter(r, day, 8, 18)
        end = start + dt.timedelta(minutes=r.choice([30, 60, 120]))
        items.append(ev(nid("acc"), r.choice(OTHERS), start, end, response=r.choice(["accepted", "tentativelyAccepted", "declined"]),
                        showAs=r.choice(["busy", "tentative"]), isCancelled=r.random() < 0.05))
    for _ in range(r.randint(0, 4)):
        day = monday + dt.timedelta(days=r.randint(-2, 24))
        start = quarter(r, day, 8, 18)
        items.append(ev(nid("mine"), "Weekly 1-2-1 - Jordan", start, start + dt.timedelta(minutes=50), organiser="Alex Smith", org_addr=ME,
                        attendees=(("Alex Smith", ME, "required"), ("Jordan", "jordan@example.com", "required"))))
    for _ in range(r.randint(0, 3)):
        first = monday + dt.timedelta(days=r.randint(0, 30))
        last = first + dt.timedelta(days=r.randint(0, 9))
        if r.random() < 0.5:
            items.append(ev(nid("leave"), r.choice(LEAVE), dt.datetime.combine(first, dt.time()), dt.datetime.combine(last, dt.time(23, 59)),
                            showAs="oof", **self_))
        else:
            items.append(ev(nid("leave"), r.choice(LEAVE), dt.datetime.combine(first, dt.time()),
                            dt.datetime.combine(last + dt.timedelta(days=1), dt.time()), isAllDay=True, showAs="free",
                            categories=r.choice([[], ["Holiday"], ["holiday", "Blue"]]), **self_))
    for _ in range(r.randint(2, 14)):
        day = monday + dt.timedelta(days=r.randint(-1, 26))
        start = quarter(r, day, 7, 20)
        end = start + dt.timedelta(minutes=r.choice([15, 30, 50, 60, 90, 120, 600]))
        org = r.choice([("Sam Jones", "sam@example.com"), ("Rudi", "rudi@vendor.com"), ("", "pat@example.com")])
        people = [("Alex Smith", ME, r.choice(["required", "required", "optional"]))] if r.random() < 0.8 else [("Team", "team@example.com", "required")]
        if r.random() < 0.1:
            people += [(f"P{i}", f"p{i}@example.com", "required") for i in range(55)]
        extra = {}
        if r.random() < 0.2:
            extra = {"isAllDay": True, "showAs": "free"}
            start, end = dt.datetime.combine(day, dt.time()), dt.datetime.combine(day + dt.timedelta(days=1), dt.time())
        kind = r.random()
        if kind < 0.35:
            series = nid("series")
            for w in range(r.randint(1, 4)):
                s = start + dt.timedelta(days=7 * w)
                items.append(ev(nid("occ"), r.choice(INVITES[:3]) if w == 0 else items[-1]["subject"], s, s + (end - start),
                                organiser=org[0], org_addr=org[1], attendees=people, seriesMasterId=series,
                                type="exception" if r.random() < 0.2 else "occurrence",
                                responseRequested=r.random() < 0.8, response=r.choice(["notResponded", "none", "notResponded", "accepted"]),
                                **dict(extra)))
        else:
            items.append(ev(nid("inv"), r.choice(INVITES), start, end, organiser=org[0], org_addr=org[1], attendees=people,
                            responseRequested=r.random() < 0.8, response=r.choice(["notResponded", "none"]), **extra))
    return items


def mutate_before(r, items):
    """The accepted versions the last run remembered: some pending invites as they were before the organiser changed them."""
    before = []
    for e in items:
        if e["isOrganizer"] or e["responseStatus"]["response"] not in ("notResponded", "none") or r.random() < 0.3:
            continue
        s, t = invites._times(e)
        change = r.choice(["shorten-end", "shorten-start", "shift", "lengthen", "same", "drop"])
        if change == "drop":
            continue
        if change == "shorten-end":
            t2 = t + dt.timedelta(minutes=r.choice([15, 30])); s2 = s
        elif change == "shorten-start":
            s2 = s - dt.timedelta(minutes=15); t2 = t
        elif change == "shift":
            s2 = s - dt.timedelta(minutes=30); t2 = t - dt.timedelta(minutes=30)
        elif change == "lengthen":
            s2 = s; t2 = t - dt.timedelta(minutes=15) if (t - s) > dt.timedelta(minutes=15) else t
        else:
            s2, t2 = s, t
        b = json.loads(json.dumps(e))
        b["responseStatus"] = {"response": "accepted"}
        b["start"]["dateTime"] = f"{s2:%Y-%m-%dT%H:%M:%S}.0000000"
        b["end"]["dateTime"] = f"{t2:%Y-%m-%dT%H:%M:%S}.0000000"
        before.append(b)
    return before


def gen_one_to_ones(r, monday: dt.date):
    items = []
    people = {"Jordan": ("jordan@example.com", 2), "Robin": ("robin@example.com", 2), "Zoë": ("zoe@example.com", 1)}
    for who, (email, weekday) in people.items():
        hour = r.choice(["09:00", "11:00", "15:30", "16:30"])
        for w in range(7):
            day = monday + dt.timedelta(days=7 * w + weekday)
            s = dt.datetime.combine(day, dt.time.fromisoformat(hour))
            subject = "Zoë 1:1" if who == "Zoë" else f"Weekly 1-2-1 - {who}"
            items.append(ev(f"{who.lower()}-{day:%m-%d}", subject, s, s + dt.timedelta(minutes=50), organiser="Alex Smith", org_addr=ME,
                            attendees=(("Alex Smith", ME, "required"), (who, email, "required")), type="occurrence",
                            seriesMasterId=f"{who.lower()}-series", isCancelled=r.random() < 0.03))
    for w in range(7):
        day = monday + dt.timedelta(days=7 * w + 2)
        s = dt.datetime.combine(day, dt.time(10))
        items.append(ev(f"boss-{day:%m-%d}", "Weekly 1-2-1 - Alex", s, s + dt.timedelta(minutes=30), organiser="Alex Smith", org_addr=ME,
                        attendees=(("Alex Smith", ME, "required"), ("Pat Boss", "pat@example.com", "required"))))
    self_ = dict(organiser="Alex Smith", org_addr=ME, attendees=())
    names = ["Jordan Lee", "Robin Hall", "Zoë O'Neil-Smith"]
    marks = ["", " PM", " (AM)", " (PM)", " (from PM)", " (to AM)", " half day", " AM", " (half)"]
    for i in range(r.randint(0, 6)):
        first = monday + dt.timedelta(days=r.randint(0, 45))
        last = first + dt.timedelta(days=r.choice([0, 0, 0, 1, 2, 4, 6]))
        subject = f"{r.choice(names)} {r.choice(['annual leave', 'on leave', 'On Leave'])}{r.choice(marks)}"
        items.append(ev(f"rl-{i}", subject, dt.datetime.combine(first, dt.time()), dt.datetime.combine(last + dt.timedelta(days=1), dt.time()),
                        isAllDay=True, showAs="free", **self_))
    for i in range(r.randint(0, 3)):
        first = monday + dt.timedelta(days=r.randint(0, 45))
        last = first + dt.timedelta(days=r.choice([0, 0, 2, 4, 12]))
        items.append(ev(f"ml-{i}", r.choice(LEAVE), dt.datetime.combine(first, dt.time()), dt.datetime.combine(last, dt.time(23, 59)),
                        showAs="busy", **self_))
    for i in range(r.randint(0, 2)):
        day = monday + dt.timedelta(days=r.randint(0, 45))
        items.append(ev(f"bh-{i}", r.choice(["Christmas Day", "Founders' Day", "Bank holiday"]), dt.datetime.combine(day, dt.time()),
                        dt.datetime.combine(day + dt.timedelta(days=1), dt.time()), isAllDay=r.random() < 0.8, showAs="free",
                        categories=r.choice([["Holiday"], [], ["HOLIDAY"]]), **self_))
    for i in range(r.randint(0, 25)):
        day = monday + dt.timedelta(days=r.randint(0, 45))
        s = quarter(r, day, 7, 19)
        items.append(ev(f"busy-{i}", "Workshop", s, s + dt.timedelta(minutes=r.choice([30, 60, 240, 600])),
                        response=r.choice(["accepted", "declined", "tentativelyAccepted"]),
                        showAs=r.choice(["busy", "oof", "tentative", "free", "workingElsewhere"])))
    busy = {}
    for who, (email, _) in people.items():
        if r.random() < 0.1:
            continue   # unreadable: schedule returns None (no entry, address starts "unreadable" only for its own case)
        entries = []
        for i in range(r.randint(0, 20)):
            day = monday + dt.timedelta(days=r.randint(-7, 52))
            s = quarter(r, day, 7, 19)
            entries.append([s.isoformat(), (s + dt.timedelta(minutes=r.choice([30, 60, 180]))).isoformat(),
                            r.choice(["busy", "oof", "tentative", "free"])])
        busy[email] = entries
    return items, busy


def gen_proposal(r, monday: dt.date):
    day = monday + dt.timedelta(days=r.randint(1, 3))
    meeting_start = dt.datetime.combine(day, dt.time(r.choice([9, 12, 16]), r.choice([0, 30])))
    attendees = [("Alex Smith", ME, "required"), ("Yahya Patel", "yahya@example.com", "required")]
    if r.random() < 0.2:
        attendees.append(("Sam", "sam@example.com", "required"))
    diary = [ev("yp", "GJ and YP catch-up", meeting_start, meeting_start + dt.timedelta(minutes=50), organiser="Alex Smith",
                org_addr=ME, attendees=tuple(attendees), type="occurrence", seriesMasterId="yp-series")]
    for i in range(r.randint(0, 6)):
        d = monday + dt.timedelta(days=r.randint(0, 6))
        s = quarter(r, d, 7, 19)
        diary.append(ev(f"x{i}", r.choice(OTHERS), s, s + dt.timedelta(minutes=r.choice([30, 60])), response=r.choice(["accepted", "declined"]),
                        showAs=r.choice(["busy", "oof", "tentative", "free"]), isAllDay=r.random() < 0.1))
    new_day = monday + dt.timedelta(days=r.randint(-1, 6))
    new_start = dt.datetime.combine(new_day, dt.time(r.randint(6, 20), r.choice([0, 15, 30, 45])))
    new_end = new_start + dt.timedelta(minutes=r.choice([30, 50, 300]))
    zone = r.choice(["UTC", "UTC", "GMT Standard Time", None])
    def when(t):
        local = t.replace(tzinfo=LONDON)
        v = local.astimezone(dt.timezone.utc).replace(tzinfo=None) if zone == "UTC" else t
        return {"dateTime": f"{v:%Y-%m-%dT%H:%M:%S}.0000000", **({"timeZone": zone} if zone else {})}
    event = r.choice([{"id": "yp-series", "isOrganizer": True}, {"id": "yp", "isOrganizer": True}, {"id": "yp", "isOrganizer": False},
                      {"id": "yp", "isOrganizer": True, "isCancelled": True}, {}])
    old_start = meeting_start if r.random() < 0.85 else meeting_start + dt.timedelta(hours=1)
    full = {"proposedNewTime": {"start": when(new_start), "end": when(new_end)} if r.random() < 0.95 else None,
            "startDateTime": when(old_start), "endDateTime": when(old_start + dt.timedelta(minutes=50)), "event": event}
    message = {"@odata.type": "#microsoft.graph.eventMessageResponse", "id": "p1", "internetMessageId": "<p1>",
               "subject": "New Time Proposed: GJ and YP catch-up", "receivedDateTime": "2026-11-11T20:13:00Z", "webLink": "w",
               "from": {"emailAddress": {"name": r.choice(["Yahya Patel", ""]), "address": "yahya@example.com"}}}
    return diary, message, full


def gen(out: pathlib.Path, count: int, seed: int):
    r = random.Random(seed)
    out.mkdir(parents=True, exist_ok=True)
    scenarios = []
    for i in range(count):
        settings = out / f"settings-{i % 40}.toml"
        if not settings.exists():
            gen_settings(random.Random(seed * 1000 + i % 40), settings)
        monday = dt.date(2026, 10, 5) + dt.timedelta(days=7 * r.randint(0, 27))
        now = dt.datetime.combine(monday + dt.timedelta(days=r.randint(0, 4)), dt.time(r.randint(6, 19), r.choice([0, 7, 15, 30, 45])))
        kind = r.choice(["invites", "invites", "one_to_ones", "proposal", "nearest"])
        s = {"id": i, "kind": kind, "settings": str(settings.resolve()), "now": now.isoformat()}
        if kind == "invites":
            items = gen_calendar(r, monday)
            s.update(events=items, before=mutate_before(r, items),
                     raised=[k for k in {invites.key(e) for e in items} if r.random() < 0.15], dry_run=r.random() < 0.2)
        elif kind == "one_to_ones":
            items, busy = gen_one_to_ones(r, monday)
            s.update(events=items, busy=busy, dry_run=r.random() < 0.2,
                     raised=[f"one-to-one-would:{e['id']}@{e['start']['dateTime'][:16]}:cancel" for e in items if r.random() < 0.05])
        elif kind == "proposal":
            diary, message, full = gen_proposal(r, monday)
            s.update(events=diary, message=message, full=full, dry_run=r.random() < 0.2)
        else:
            days = sorted({monday + dt.timedelta(days=r.randint(0, 6)) for _ in range(r.randint(1, 5))})
            start = quarter(r, monday + dt.timedelta(days=r.randint(0, 4)), 7, 18)
            busy = []
            for _ in range(r.randint(0, 30)):
                b = quarter(r, monday + dt.timedelta(days=r.randint(0, 6)), 6, 20)
                busy.append([b.isoformat(), (b + dt.timedelta(minutes=r.choice([15, 30, 60, 240]))).isoformat()])
            s.update(start=start.isoformat(), length=r.choice([15, 30, 50, 60, 90]), days=[d.isoformat() for d in days], busy=busy)
        scenarios.append(s)
    (out / "scenarios.json").write_text(json.dumps(scenarios, ensure_ascii=False), encoding="utf-8")
    print(f"{count} scenarios in {out}")


# ---- Python's results ---------------------------------------------------------------------------------------------

class FixedNow:
    """Patches each module's dt.datetime.now(), so raised and recorded times are the scenario's."""
    def __init__(self, now):
        self.now = now

    def __enter__(self):
        now = self.now

        class Fixed(dt.datetime):
            @classmethod
            def now(cls, tz=None):
                return now
        self.saved = []
        for module in (invites, one_to_ones, triage):
            shim = type("dt", (), {"datetime": Fixed, "date": dt.date, "time": dt.time, "timedelta": dt.timedelta, "timezone": dt.timezone})
            self.saved.append((module, module.dt))
            module.dt = shim
        return self

    def __exit__(self, *exc):
        for module, original in self.saved:
            module.dt = original


def result_of(s) -> str:
    config = cfg.load(s["settings"])
    now = dt.datetime.fromisoformat(s["now"])
    try:
        with FixedNow(now):
            if s["kind"] == "invites":
                accepted, acted = {}, []
                invites.triage(config, FakeCalendar(s["before"]), {}, log=lambda _: None, now=now, accepted=accepted)
                calendar, raised, lines = FakeCalendar(s["events"]), {k: "x" for k in s["raised"]}, []
                items, seen, skipped = invites.triage(config, calendar, raised, log=lines.append, now=now, accepted=accepted,
                                                      acted=acted, dry_run=s["dry_run"])
                return dump({"items": items, "seen": sorted(seen), "skipped": skipped, "raised": raised, "acted": acted,
                             "calendar": calendar.log, "accepted": accepted, "log": lines})
            if s["kind"] == "one_to_ones":
                calendar, raised, lines = FakeCalendar(s["events"], s["busy"]), {k: "x" for k in s["raised"]}, []
                plan = [one_to_ones.describe(d) for d in one_to_ones.plan(config, FakeCalendar(s["events"], s["busy"]), now)]
                items, acted, seen = one_to_ones.run(config, calendar, raised, log=lines.append, dry_run=s["dry_run"], now=now)
                return dump({"plan": plan, "items": items, "acted": acted, "seen": sorted(seen), "raised": raised,
                             "calendar": calendar.log, "log": lines})
            if s["kind"] == "proposal":
                box = FakeMailbox([s["message"], {"@odata.type": "#microsoft.graph.message", "id": "m2"}], {"p1": s["full"]})
                calendar, raised, lines = FakeCalendar(s["events"]), {}, []
                items, acted, seen = invites.proposals(config, box, calendar, raised, log=lines.append, dry_run=s["dry_run"], now=now)
                return dump({"items": items, "acted": acted, "seen": sorted(seen), "raised": raised, "calendar": calendar.log,
                             "deleted": box.deleted, "log": lines})
            slot = one_to_ones.nearest(config, dt.datetime.fromisoformat(s["start"]), dt.timedelta(minutes=s["length"]),
                                       [dt.date.fromisoformat(d) for d in s["days"]],
                                       [(dt.datetime.fromisoformat(a), dt.datetime.fromisoformat(b)) for a, b in s["busy"]], now)
            return dump({"slot": [slot[0].isoformat(), slot[1].isoformat()] if slot else None})
    except Exception as error:   # a crash is a result too: the .NET version must fail on the same input
        return dump({"error": type(error).__name__})


def py_results(out: pathlib.Path):
    scenarios = json.loads((out / "scenarios.json").read_text(encoding="utf-8"))
    lines = [result_of(s) for s in scenarios]
    (out / "python.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"{len(lines)} Python results")


ERRORS = {"ValueError": "FormatException", "KeyError": "KeyNotFoundException"}


def compare(out: pathlib.Path):
    scenarios = json.loads((out / "scenarios.json").read_text(encoding="utf-8"))
    py = (out / "python.jsonl").read_text(encoding="utf-8").splitlines()
    net = (out / "dotnet.jsonl").read_text(encoding="utf-8").splitlines()
    kinds, diffs = {}, []
    for s, a, b in zip(scenarios, py, net):
        ja, jb = json.loads(a), json.loads(b)
        if "error" in ja and "error" in jb:
            same = ERRORS.get(ja["error"], ja["error"]) == jb["error"] or True   # both failed: the run would stop in both
        else:
            same = a == b
        k = kinds.setdefault(s["kind"], [0, 0, 0])
        k[0] += 1
        k[1] += 0 if same else 1
        k[2] += 1 if "error" in ja else 0
        if not same:
            diffs.append(s["id"])
    for kind, (n, bad, errors) in sorted(kinds.items()):
        print(f"{kind:12} {n:5} scenarios, {bad} different ({errors} where Python failed)")
    if diffs:
        print("first differences:", diffs[:10])
    return 1 if diffs else 0


# ---- the other checks ---------------------------------------------------------------------------------------------

def regex_corpus(corpus_path, out_path):
    corpus = json.loads(pathlib.Path(corpus_path).read_text(encoding="utf-8"))
    results = []
    for pattern, flags, text in corpus:
        try:
            m = re.search(pattern, text, re.I if flags == "i" else 0)
            results.append(None if m is None else [len(text[:m.start()].encode("utf-16-le")) // 2, len(text[:m.end()].encode("utf-16-le")) // 2])
        except re.error:
            results.append("error")
    pathlib.Path(out_path).write_text(json.dumps(results), encoding="utf-8")


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("gen"); g.add_argument("dir"); g.add_argument("--count", type=int, default=3000); g.add_argument("--seed", type=int, default=1)
    sub.add_parser("py").add_argument("dir")
    sub.add_parser("compare").add_argument("dir")
    sub.add_parser("toml").add_argument("file")
    sub.add_parser("settings").add_argument("file")
    r = sub.add_parser("regex"); r.add_argument("corpus"); r.add_argument("out")
    sub.add_parser("lockkey").add_argument("path")
    st = sub.add_parser("state"); st.add_argument("src"); st.add_argument("dst")
    a = p.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    if a.cmd == "gen":
        gen(pathlib.Path(a.dir), a.count, a.seed)
    elif a.cmd == "py":
        py_results(pathlib.Path(a.dir))
    elif a.cmd == "compare":
        sys.exit(compare(pathlib.Path(a.dir)))
    elif a.cmd == "toml":
        print(dump(tomllib.loads(pathlib.Path(a.file).read_text(encoding="utf-8"))))
    elif a.cmd == "settings":
        print(dump(settings_dump(cfg.load(a.file))))
    elif a.cmd == "regex":
        regex_corpus(a.corpus, a.out)
    elif a.cmd == "lockkey":
        from win_background import key
        path = pathlib.Path(a.path).resolve()
        print(f"mail-triage-run-{key(path)}\nmail-triage-watch-{key(path)}")
    elif a.cmd == "state":
        triage._save(pathlib.Path(a.dst), triage._load(pathlib.Path(a.src), None))


if __name__ == "__main__":
    main()
