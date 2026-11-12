"""Run with: python -m unittest discover -s tests   (no Graph: a fake calendar, and fake free/busy)

"Now" is Thu 12 Nov 2026, 09:00. Alex has weekly 1:1s with two reports, Jordan (Wednesdays 15:30) and Robin
(Wednesdays 11:00), and a 1:1 with their own manager that Alex happens to send ("Weekly 1-2-1 - Alex"), which
is protected. Each test adds the leave, bank holidays and busy time it's about.
"""

import datetime as dt
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from mail_triage import config as cfg, invites, one_to_ones, triage  # noqa: E402
from test_invites import ME, SELF, SETTINGS, ev  # noqa: E402

ONE_TO_ONES = """
[one_to_ones]
act = true
days_ahead = 42

[[one_to_one]]
name = "Jordan Lee"
email = "jordan@example.com"

[[one_to_one]]
name = "Robin Hall"
email = "Robin@Example.com"

[[one_to_one]]
name = "Pat Boss"                       # a mistake in the settings: the protected rule still wins
email = "pat@example.com"
subject = "Weekly 1-2-1 - Alex"
"""
WEDNESDAYS = ["2026-11-18", "2026-11-25", "2026-12-02", "2026-12-09", "2026-12-16", "2026-12-23"]


def one_to_one(who, email, day, start, end):
    return ev(f"{who.lower()}-{day[5:]}", f"Weekly 1-2-1 - {who}", day, start, end, organiser="Alex Smith", org_addr=ME,
              attendees=(("Alex Smith", ME, "required"), (who, email, "required")), type="occurrence",
              seriesMasterId=f"{who.lower()}-series")


def away(id, subject, first, last, **extra):
    """An all-day entry in Alex's diary, first to last inclusive."""
    end = (dt.date.fromisoformat(last) + dt.timedelta(days=1)).isoformat()
    return ev(id, subject, first, "00:00", "00:00", end_day=end, isAllDay=True, showAs="free", **SELF, **extra)


def diary():
    return ([one_to_one("Jordan", "jordan@example.com", d, "15:30", "16:20") for d in WEDNESDAYS]
            + [one_to_one("Robin", "robin@example.com", d, "11:00", "11:50") for d in WEDNESDAYS]
            + [ev(f"boss-{d[5:]}", "Weekly 1-2-1 - Alex", d, "10:00", "10:30", organiser="Alex Smith", org_addr=ME,
                  attendees=(("Alex Smith", ME, "required"), ("Pat Boss", "pat@example.com", "required")),
                  type="occurrence", seriesMasterId="boss-series") for d in WEDNESDAYS])


class FakeCalendar:
    def __init__(self, events, busy=None):
        self.items, self.busy = events, busy or {}   # busy: {address: [(start, end, status)]}
        self.cancels, self.moves, self.answers, self.asked = [], [], [], []

    def events(self, start, days):
        return self.items

    def schedule(self, address, start, end):
        self.asked.append(address)
        if address == "unreadable@example.com":
            return None
        return [(s, e, st) for s, e, st in self.busy.get(address, []) if s < end and e > start]

    def cancel(self, event_id, comment=""):
        self.cancels.append((event_id, comment))

    def move(self, event_id, start, end):
        self.moves.append((event_id, start, end))

    def respond(self, event_id, action, note="", send=True):
        self.answers.append((event_id, action, note, send))


def at(day, time):
    return dt.datetime.fromisoformat(f"{day}T{time}")


class Base(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.temp.name).resolve()
        (self.dir / "config.toml").write_text(SETTINGS + ONE_TO_ONES, encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")
        self.now = dt.datetime(2026, 11, 12, 9, 0)

    def tearDown(self):
        self.temp.cleanup()

    def plan(self, extra, busy=None):
        self.calendar = FakeCalendar(diary() + extra, busy)
        return {(d["meeting"]["id"]): d for d in one_to_ones.plan(self.config, self.calendar, self.now)}


class OneToOnes(Base):
    def test_settings(self):
        reports = self.config.one_to_ones.reports
        self.assertEqual([(r.name, r.email, r.subject) for r in reports][:2],
                         [("Jordan Lee", "jordan@example.com", "Weekly 1-2-1 - Jordan"),
                          ("Robin Hall", "robin@example.com", "Weekly 1-2-1 - Robin")])
        self.assertEqual((self.config.one_to_ones.cancel_days, self.config.one_to_ones.max_actions), (3, 20))
        bad = self.dir / "bad.toml"
        bad.write_text(f'email = "{ME}"\n' + ONE_TO_ONES, encoding="utf-8")
        with self.assertRaises(ValueError):   # it needs [invites]
            cfg.load(bad)

    def test_nobody_off_nothing_happens(self):
        self.assertEqual(self.plan([]), {})

    def test_off_three_days_of_the_week_cancels_with_the_report_note(self):
        found = self.plan([away("l", "Jordan Lee annual leave", "2026-11-18", "2026-11-20")])
        self.assertEqual(list(found), ["jordan-11-18"])                       # Robin's that day is untouched
        self.assertEqual(found["jordan-11-18"]["action"], "cancel")
        self.assertEqual(found["jordan-11-18"]["note"], "Cancelling as you're on leave - enjoy the time off")
        self.assertIn("Jordan off 3 of 5 days that week", found["jordan-11-18"]["why"])

    def test_off_less_of_the_week_moves_to_the_nearest_free_slot(self):
        found = self.plan([away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25")])
        # Nearest in time: first thing Thursday (16.5h after) beats the end of Tuesday (22.5h before)
        self.assertEqual(found["jordan-11-25"]["action"], "move")
        self.assertEqual(found["jordan-11-25"]["to"], (at("2026-11-26", "08:00"), at("2026-11-26", "08:50")))

    def test_on_a_tie_the_later_slot(self):
        busy = [ev("am", "Workshop", "2026-11-26", "08:00", "14:00", response="accepted")]
        found = self.plan([away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25")] + busy)
        # Tue 17:00 and Thu 14:00 are both 22.5h away
        self.assertEqual(found["jordan-11-25"]["to"], (at("2026-11-26", "14:00"), at("2026-11-26", "14:50")))

    def test_a_move_needs_both_calendars_free(self):
        # Alex: Monday and Friday taken, Thursday out of the office (oof) in the morning and tentative after
        mine = [ev(f"w{d}", "Workshop", f"2026-11-{d}", "08:00", "18:00", response="accepted") for d in (23, 27)] + [
            ev("site", "Site visit", "2026-11-26", "08:00", "13:00", showAs="oof", **SELF),
            ev("tent", "Maybe this", "2026-11-26", "13:00", "18:00", response="notResponded", showAs="tentative")]
        # Jordan on Tuesday: busy 13:00-18:00, tentative 12:00-13:00, free (doesn't count) 11:00-12:00
        theirs = {"jordan@example.com": [(at("2026-11-24", "13:00"), at("2026-11-24", "18:00"), "busy"),
                                         (at("2026-11-24", "12:00"), at("2026-11-24", "13:00"), "tentative"),
                                         (at("2026-11-24", "11:00"), at("2026-11-24", "12:00"), "free")]}
        found = self.plan([away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25")] + mine, theirs)
        self.assertEqual(found["jordan-11-25"]["to"], (at("2026-11-24", "11:00"), at("2026-11-24", "11:50")))
        self.assertEqual(self.calendar.asked, ["jordan@example.com"])             # their free/busy, once for the week

    def test_a_half_day_counts_only_if_it_overlaps_the_meeting(self):
        found = self.plan([away("r", "Robin Hall on leave PM", "2026-12-02", "2026-12-02"),        # Robin's is at 11:00
                           away("j", "Jordan Lee annual leave (PM)", "2026-12-02", "2026-12-02")])  # Jordan's at 15:30
        self.assertEqual(list(found), ["jordan-12-02"])
        self.assertEqual(found["jordan-12-02"]["action"], "move")               # half a day isn't three
        self.assertIn("Jordan off 0.5 of 5 days", found["jordan-12-02"]["why"])
        self.assertEqual(found["jordan-12-02"]["to"][0].date(), dt.date(2026, 12, 3))

    def test_from_and_to_halves_of_longer_leave(self):
        events = [away("a", "Jordan Lee annual leave (from PM)", "2026-12-07", "2026-12-09"),
                  away("b", "Jordan Lee annual leave (to AM)", "2026-12-14", "2026-12-16"),
                  away("c", "Jordan Lee annual leave half day", "2026-12-21", "2026-12-21"),
                  away("d", "Jordan Lee annual leave AM", "2026-12-22", "2026-12-22"),
                  away("e", "Jordan Lee on leave PM", "2026-12-22", "2026-12-22")]
        report = self.config.one_to_ones.reports[0]
        days = one_to_ones.report_leave(self.config, report, events)
        d = lambda s: dt.date.fromisoformat(s)   # noqa: E731
        self.assertEqual(days, {d("2026-12-07"): "PM", d("2026-12-08"): "full", d("2026-12-09"): "full",
                                d("2026-12-14"): "full", d("2026-12-15"): "full", d("2026-12-16"): "AM",
                                d("2026-12-21"): "half", d("2026-12-22"): "full"})     # morning + afternoon
        self.assertEqual(one_to_ones.report_leave(self.config, self.config.one_to_ones.reports[1], events), {})

    def test_a_half_day_of_unknown_half_is_raised_not_acted_on(self):
        found = self.plan([ev("h", "Holiday (half day) (Principal Architect)", "2026-12-09", "00:00", "23:59",
                              showAs="busy", **SELF)])
        self.assertEqual({k: d["action"] for k, d in found.items()}, {"jordan-12-09": "raise", "robin-12-09": "raise"})
        self.assertIn("you have a half day Wed 09 Dec and which half isn't known", found["jordan-12-09"]["why"])
        self.assertTrue(found["jordan-12-09"]["suggest"].startswith("cancel it, or move it to"))

    def test_your_own_leave_from_people_first_cancels_with_your_note(self):
        # People First: "Holiday (<job title>)", 00:00 to 23:59, busy and private, not all-day
        found = self.plan([ev("h", "Holiday (Principal Architect)", "2026-12-16", "00:00", "23:59", end_day="2026-12-31",
                              showAs="busy", sensitivity="private", **SELF),
                           away("x", "Christmas Day", "2026-12-25", "2026-12-25", categories=["Holiday"])])
        self.assertEqual({k: (d["action"], d["note"]) for k, d in found.items()},
                         {k: ("cancel", "Cancelling as I'm on leave")
                          for k in ("jordan-12-16", "robin-12-16", "jordan-12-23", "robin-12-23")})
        self.assertIn("you off 3 of 5 days that week", found["jordan-12-16"]["why"])        # Wed to Fri
        self.assertIn("you off 5 of 5 days that week", found["jordan-12-23"]["why"])        # leave + Christmas Day

    def test_a_bank_holiday_moves_or_cancels_for_both(self):
        holiday = away("b", "Founders' Day", "2026-11-25", "2026-11-25", categories=["Holiday"])
        found = self.plan([holiday])
        self.assertEqual({k: d["action"] for k, d in found.items()}, {"jordan-11-25": "move", "robin-11-25": "move"})
        self.assertIn("the bank holiday Wed 25 Nov", found["robin-11-25"]["why"])
        # With two more days of Jordan's leave that week, Jordan is off three: cancelled, for the bank holiday
        found = self.plan([holiday, away("l", "Jordan Lee on leave", "2026-11-23", "2026-11-24")])
        self.assertEqual((found["jordan-11-25"]["action"], found["jordan-11-25"]["note"]),
                         ("cancel", "Cancelling for the bank holiday"))
        self.assertEqual(found["robin-11-25"]["action"], "move")
        # Not in the Holiday category, and not all-day: not a bank holiday
        self.assertEqual(self.plan([away("n", "Founders' Day", "2026-11-25", "2026-11-25")]), {})

    def test_no_free_slot_is_raised_with_a_suggestion(self):
        full = [ev(f"w{d}", "Workshop", f"2026-11-{d}", "08:00", "18:00", response="accepted") for d in (23, 24, 26, 27)]
        found = self.plan([away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25")] + full)
        d = found["jordan-11-25"]
        self.assertEqual(d["action"], "raise")
        self.assertIn("no time that week when you're both free", d["why"])
        self.assertIn("Mon 30 Nov 08:00–08:50 (outside its week)", d["suggest"])     # nearest in time, either side

    def test_unreadable_free_busy_is_raised(self):
        self.config.one_to_ones.reports[0].email = "unreadable@example.com"
        events = [dict(e, attendees=[{"type": "required", "emailAddress": {"address": "unreadable@example.com"}}])
                  if e["id"].startswith("jordan") else e for e in diary()]
        self.calendar = FakeCalendar(events + [away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25")])
        found = {d["meeting"]["id"]: d for d in one_to_ones.plan(self.config, self.calendar, self.now)}
        self.assertIn("couldn't read Jordan Lee's calendar", found["jordan-11-25"]["why"])

    def test_never_the_protected_1_1_nor_someone_elses_meeting(self):
        found = self.plan([away("p", "Pat Boss annual leave", "2026-11-16", "2026-11-20"),
                           ev("theirs", "Weekly 1-2-1 - Jordan", "2026-11-18", "09:00", "09:30",
                              organiser="Jordan Lee", org_addr="jordan@example.com", response="accepted"),
                           away("l", "Jordan Lee annual leave", "2026-11-18", "2026-11-20")])
        self.assertEqual(list(found), ["jordan-11-18"])                        # not boss-*, not Jordan's own meeting

    def test_past_and_beyond_the_window_are_left(self):
        self.now = dt.datetime(2026, 11, 18, 16, 0)                             # after Wednesday's 1:1s
        found = self.plan([away("l", "Jordan Lee annual leave", "2026-11-18", "2026-11-20"),
                           away("m", "Robin Hall annual leave", "2027-01-04", "2027-01-08")])
        self.assertEqual(found, {})


class Runs(Base):
    """run(): acting, recording, queueing, the cap and act = false."""
    LEAVE = [away("l", "Jordan Lee annual leave", "2026-11-18", "2026-11-20"),          # cancel 18 Nov
             away("m", "Robin Hall on leave", "2026-11-25", "2026-11-25")]              # move 25 Nov

    def run_with(self, extra, raised=None, dry_run=False):
        self.calendar = FakeCalendar(diary() + extra)
        raised = {} if raised is None else raised
        items, acted, seen = one_to_ones.run(self.config, self.calendar, raised, log=lambda _: None, dry_run=dry_run, now=self.now)
        return items, acted, seen, raised

    def test_act_cancels_and_moves_the_occurrence_only(self):
        items, acted, _, _ = self.run_with(self.LEAVE)
        self.assertEqual(items, [])
        self.assertEqual(self.calendar.cancels, [("jordan-11-18", "Cancelling as you're on leave - enjoy the time off")])
        self.assertEqual(self.calendar.moves, [("robin-11-25", at("2026-11-24", "17:00"), at("2026-11-24", "17:50"))])
        self.assertEqual([a["rule"] for a in acted], ["1:1 cancelled: leave", "1:1 moved: leave"])
        self.assertIn("moved to Tue 24 Nov 17:00–17:50", acted[1]["why"])
        self.assertEqual(self.calendar.answers, [])

    def test_act_false_records_each_would_do_once_and_changes_nothing(self):
        self.config.one_to_ones.act = False
        _, acted, _, raised = self.run_with(self.LEAVE)
        self.assertEqual((self.calendar.cancels, self.calendar.moves), ([], []))
        self.assertEqual([a["rule"] for a in acted], ["1:1 would be cancelled: leave (act = false)",
                                                      "1:1 would be moved: leave (act = false)"])
        _, again, _, _ = self.run_with(self.LEAVE, raised)
        self.assertEqual(again, [])                                            # recorded once, not every 15 minutes

    def test_a_dry_run_changes_and_remembers_nothing(self):
        _, acted, _, raised = self.run_with(self.LEAVE, dry_run=True)
        self.assertEqual((self.calendar.cancels, self.calendar.moves, raised), ([], [], {}))
        self.assertEqual(len(acted), 2)

    def test_at_most_max_actions_a_run(self):
        self.config.one_to_ones.max_actions = 1
        _, acted, _, _ = self.run_with(self.LEAVE)
        self.assertEqual((len(acted), len(self.calendar.cancels), self.calendar.moves), (1, 1, []))

    def test_what_cant_be_settled_is_queued_once(self):
        full = [ev(f"w{d}", "Workshop", f"2026-11-{d}", "08:00", "18:00", response="accepted") for d in (23, 24, 26, 27)]
        items, acted, seen, raised = self.run_with([self.LEAVE[1]] + full)
        self.assertEqual((acted, self.calendar.moves), ([], []))
        self.assertEqual([(i["type"], i["id"], i["from"], i["rule"]) for i in items],
                         [("one-to-one", "robin-11-25", "Robin Hall", "1:1 and leave")])
        self.assertTrue(items[0]["check"].startswith("DECIDE: Robin Hall is on leave Wed 25 Nov"))
        again, _, seen_again, _ = self.run_with([self.LEAVE[1]] + full, raised)
        self.assertEqual(again, [])
        self.assertEqual(seen, seen_again)                                     # still waiting, so it stays queued

    def test_a_queued_meeting_isnt_deleted_or_answered_like_an_email_or_invite(self):
        full = [ev(f"w{d}", "Workshop", f"2026-11-{d}", "08:00", "18:00", response="accepted") for d in (23, 24, 26, 27)]
        items, _, _, _ = self.run_with([self.LEAVE[1]] + full)
        triage.save_queue(self.config, items)
        for attempt in (lambda: triage.delete(self.config, None, ["1"]),
                        lambda: triage.answer(self.config, self.calendar, ["1"], "decline", log=lambda _: None),
                        lambda: triage.file(self.config, None, ["1"], "Inbox/X")):
            with self.assertRaises(LookupError):
                attempt()


class InARun(Base):
    """Through triage.run, as the watcher runs it: recorded for `mail-triage deleted`, invites unaffected."""
    def test_recorded_alongside_invites(self):
        calendar = FakeCalendar(diary() + Runs.LEAVE + [ev("i", "Planning", "2026-11-19", "10:00", "11:00")])

        class Mailbox:
            graph = None
        real = invites.Calendar
        invites.Calendar = lambda graph: calendar
        try:
            report = triage.run(self.config, Mailbox(), log=lambda _: None)
        finally:
            invites.Calendar = real
        self.assertEqual(report["invites"], 1)                                 # the invite, queued as before
        gone = triage.deleted(self.config)
        self.assertEqual([(g["rule"], g["from"]) for g in gone],
                         [("1:1 cancelled: leave", "Jordan Lee"), ("1:1 moved: leave", "Robin Hall")])
        self.assertIn("note:", gone[0]["did"])


if __name__ == "__main__":
    unittest.main()
