"""Run with: python -m unittest discover -s tests   (no Graph: a fake calendar)"""

import datetime as dt
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))

from mail_triage import config as cfg, invites, triage  # noqa: E402

ME = "alex@example.com"
SETTINGS = f"""
email = "{ME}"
queue_file = "queue.json"
state_file = "raised.json"
deleted_file = "deleted.json"
log_file = "log.txt"

[invites]
days_ahead = 30
fixed_blocks = ['^Travel']
protected = ['^Weekly 1-2-1 - Alex$']

[[invite_rule]]
name = "Colleagues' leave and away notices"
subject = 'holiday|annual leave'
all_day = true
show_as = ["free", "oof"]
action = "skip"
"""


def ev(id, subject, day, start, end, response="notResponded", organiser="Sam Jones", org_addr="sam@example.com",
       attendees=(("Alex Smith", ME, "required"),), **extra):
    return {"id": id, "subject": subject, "isAllDay": extra.pop("isAllDay", False), "showAs": extra.pop("showAs", "busy"),
            "isCancelled": False, "isOrganizer": organiser == "Alex Smith",
            "responseStatus": {"response": "organizer" if organiser == "Alex Smith" else response},
            "organizer": {"emailAddress": {"name": organiser, "address": org_addr}},
            "attendees": [{"type": t, "emailAddress": {"name": n, "address": a}} for n, a, t in attendees],
            "start": {"dateTime": f"{day}T{start}:00.0000000"}, "end": {"dateTime": f"{day}T{end}:00.0000000"},
            "type": extra.pop("type", "singleInstance"), "seriesMasterId": extra.pop("seriesMasterId", None), **extra}


class FakeCalendar:
    def __init__(self, events):
        self.items, self.answers, self.moves = events, [], []

    def events(self, start, days):
        return self.items

    def respond(self, event_id, action, note="", send=True):
        self.answers.append((event_id, action, note, send))

    def move(self, event_id, start, end):
        self.moves.append((event_id, start, end))


class Invites(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.temp.name).resolve()
        (self.dir / "config.toml").write_text(SETTINGS, encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")
        self.now = dt.datetime(2026, 11, 11, 9, 0)
        me = ("Alex Smith", "alex@example.com")
        self.calendar = FakeCalendar([
            # Alex's own entries: leave, a protected 1:1, a block, a meeting Alex runs, Travel
            ev("leave", "Holiday", "2026-12-16", "00:00", "23:59", organiser=me[0], org_addr=me[1], isAllDay=True, showAs="oof"),
            ev("john", "Weekly 1-2-1 - Alex", "2026-11-13", "10:30", "11:00", organiser="Pat Boss", org_addr="pat@example.com", response="accepted"),
            ev("block", "Focus time", "2026-11-12", "14:00", "15:00", organiser=me[0], org_addr=me[1], attendees=()),
            ev("mine", "Weekly 1-2-1 - Jordan", "2026-11-11", "15:30", "16:20", organiser=me[0], org_addr=me[1],
               attendees=(("Alex Smith", ME, "required"), ("Jordan", "jordan@example.com", "required"))),
            ev("travel", "Travel", "2026-11-12", "17:00", "18:00", organiser=me[0], org_addr=me[1], attendees=(), showAs="oof"),
            # Invites waiting for an answer
            ev("i-leave", "Planning day", "2026-12-16", "10:00", "12:00"),
            ev("i-john", "Vendor demo", "2026-11-13", "10:00", "11:00", organiser="Rudi", org_addr="rudi@vendor.com"),
            ev("i-block", "Design review", "2026-11-12", "14:30", "15:30"),
            ev("i-mine", "Architecture Catch-up", "2026-11-11", "15:00", "15:50", organiser="Robin", org_addr="robin@example.com"),
            ev("i-travel", "Social", "2026-11-12", "17:00", "19:00", attendees=(("Team", "team@example.com", "required"),)),
            ev("i-free", "Retro", "2026-11-18", "13:00", "14:00", attendees=(("Alex Smith", ME, "optional"),)),
            ev("i-away", "Kurt Holiday - All Day", "2026-11-20", "00:00", "23:59", isAllDay=True, showAs="free"),
            ev("s1", "Leads sync", "2026-11-17", "09:00", "09:30", type="occurrence", seriesMasterId="series-1"),
            ev("s2", "Leads sync", "2026-11-24", "09:00", "09:30", type="occurrence", seriesMasterId="series-1"),
            ev("past", "Yesterday's thing", "2026-11-10", "10:00", "11:00"),
            # A deadline marker on a busy day: all-day, free, to a group
            ev("i-marker", "Salary changes due", "2026-11-12", "00:00", "23:59", isAllDay=True, showAs="free",
               attendees=(("Managers", "managers@example.com", "required"),)),
        ])

    def tearDown(self):
        self.temp.cleanup()

    def run_triage(self, raised=None):
        raised = {} if raised is None else raised
        items, seen, skipped = invites.triage(self.config, self.calendar, raised, log=lambda _: None, now=self.now)
        return {i["subject"].split(":")[0]: i for i in items}, seen, skipped, raised

    def test_recommendations_follow_the_evidence(self):
        items, _, skipped, _ = self.run_triage()
        self.assertEqual(items["Planning day"]["recommendation"], "decline")                   # leave
        self.assertIn("leave", items["Planning day"]["check"])
        self.assertEqual(items["Vendor demo"]["recommendation"], "decline")                    # protected 1:1
        self.assertIn("an outside organiser", items["Vendor demo"]["check"])
        self.assertEqual(items["Design review"]["recommendation"], "accept and move")          # only your block
        self.assertEqual(items["Architecture Catch-up"]["recommendation"], "accept and move")  # a meeting you run
        self.assertEqual(items["Architecture Catch-up"]["clashes"][0]["id"], "mine")
        self.assertEqual(items["Social"]["recommendation"], "decline")                         # Travel is fixed
        self.assertIn("through a group", items["Social"]["check"])
        self.assertEqual(items["Retro"]["recommendation"], "accept")
        self.assertIn("optional", items["Retro"]["check"])
        self.assertEqual(items["Salary changes due"]["recommendation"], "accept")               # holds no time
        self.assertEqual(items["Salary changes due"]["clashes"], [])
        self.assertNotIn("Kurt Holiday - All Day", items)                                      # skipped by rule
        self.assertNotIn("Yesterday's thing", items)
        self.assertEqual(skipped, 1)

    def test_a_series_is_raised_once_as_a_whole(self):
        items, seen, _, raised = self.run_triage()
        series = items["Leads sync"]
        self.assertEqual(series["id"], "series-1")
        self.assertIn("a recurring series (2 in the next 30 days)", series["check"])
        again, _, _, _ = self.run_triage(raised)
        self.assertEqual(again, {})                                                            # each raised once
        self.assertIn("invite:series-1", seen)

    def test_answering_from_the_queue(self):
        items, _, _, _ = self.run_triage()
        triage.save_queue(self.config, list(items.values()))
        number = next(i["n"] for i in triage.queue(self.config) if i["id"] == "i-leave")
        triage.answer(self.config, self.calendar, [str(number)], "decline", "On leave", log=lambda _: None)
        self.assertEqual(self.calendar.answers, [("i-leave", "decline", "On leave", True)])
        self.assertNotIn("i-leave", [i["id"] for i in triage.queue(self.config)])
        with self.assertRaises(LookupError):   # an invite isn't deleted like an email
            triage.delete(self.config, None, ["1"])

    def test_leave_sweep_finds_what_you_havent_declined(self):
        found = invites.leave_sweep(self.config, self.calendar, self.now.date(), 60)
        self.assertEqual([e["id"] for e in found], ["i-leave"])


if __name__ == "__main__":
    unittest.main()
