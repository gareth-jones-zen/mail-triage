"""Run with: python -m unittest discover -s tests   (no Graph: a fake calendar)

"Now" is Wed 11 Nov 2026, 09:00. Alex's week: travel in and out of the office on Thursday, a block on
Thursday afternoon, a Friday block that must stay on Friday, Monday prep that must happen before Tuesday's
leadership meeting, and three days' leave in December.
"""

import datetime as dt
import pathlib
import sys
import tempfile
import unittest
import urllib.error
import io

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
commute_blocks = ['^Travel']
protected = ['^Weekly 1-2-1 - Alex$']

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
"""
SELF = dict(organiser="Alex Smith", org_addr=ME, attendees=())


def ev(id, subject, day, start, end, response="notResponded", organiser="Sam Jones", org_addr="sam@example.com",
       attendees=(("Alex Smith", ME, "required"),), end_day=None, **extra):
    return {"id": id, "subject": subject, "isAllDay": extra.pop("isAllDay", False), "showAs": extra.pop("showAs", "busy"),
            "isCancelled": False, "isOrganizer": organiser == "Alex Smith",
            "responseStatus": {"response": "organizer" if organiser == "Alex Smith" else response},
            "responseRequested": extra.pop("responseRequested", True),
            "organizer": {"emailAddress": {"name": organiser, "address": org_addr}},
            "attendees": [{"type": t, "emailAddress": {"name": n, "address": a}} for n, a, t in attendees],
            "start": {"dateTime": f"{day}T{start}:00.0000000"}, "end": {"dateTime": f"{end_day or day}T{end}:00.0000000"},
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
        self.calendar = FakeCalendar([
            # Alex's own diary
            ev("leave", "Holiday", "2026-12-16", "00:00", "23:59", end_day="2026-12-18", showAs="oof", **SELF),
            ev("john", "Weekly 1-2-1 - Alex", "2026-11-13", "10:30", "11:00", organiser="Pat Boss", org_addr="pat@example.com",
               response="accepted"),
            ev("mine", "Weekly 1-2-1 - Jordan", "2026-11-11", "15:30", "16:20", organiser="Alex Smith", org_addr=ME,
               attendees=(("Alex Smith", ME, "required"), ("Jordan", "jordan@example.com", "required"))),
            ev("travel-am", "Travel", "2026-11-12", "07:00", "08:00", showAs="oof", **SELF),
            ev("block", "Focus time", "2026-11-12", "14:00", "15:00", **SELF),
            ev("travel-pm", "Travel", "2026-11-12", "17:00", "18:00", showAs="oof", **SELF),
            ev("update", "Weekly update for Pat", "2026-11-13", "16:00", "16:30", **SELF),
            ev("prep", "Prep for leadership", "2026-11-16", "15:30", "16:00", **SELF),
            ev("tue", "Tuesday workshop", "2026-11-17", "08:00", "11:00", response="accepted"),
            ev("lead", "Leadership weekly", "2026-11-17", "11:00", "12:00", response="accepted"),
            ev("talk", "Lunch and learn", "2026-11-18", "10:00", "11:00", response="accepted"),
            ev("travel-mid", "Travel to site", "2026-11-18", "12:00", "13:00", showAs="oof", **SELF),
            ev("review", "Quarterly review", "2026-11-18", "15:00", "16:00", response="accepted"),
            # Invites waiting for an answer
            ev("i-leave", "Planning day", "2026-12-16", "10:00", "12:00"),
            ev("i-john", "Vendor demo", "2026-11-13", "10:00", "11:00", organiser="Rudi", org_addr="rudi@vendor.com"),
            ev("i-block", "Design review", "2026-11-12", "14:30", "15:30"),
            ev("i-mine", "Architecture catch-up", "2026-11-11", "15:00", "15:50", organiser="Robin", org_addr="robin@example.com"),
            ev("i-early", "Breakfast briefing", "2026-11-12", "07:30", "08:30"),
            ev("i-evening", "Social", "2026-11-12", "17:00", "19:00", attendees=(("Team", "team@example.com", "required"),)),
            ev("i-mid", "Site meeting", "2026-11-18", "12:30", "13:30"),
            ev("i-update", "All hands", "2026-11-13", "15:30", "17:00"),
            ev("i-prep", "Away day", "2026-11-16", "08:00", "18:00"),
            ev("i-free", "Retro", "2026-11-18", "13:00", "14:00", attendees=(("Alex Smith", ME, "optional"),),
               responseRequested=False),
            ev("i-away", "Kurt Holiday - All Day", "2026-11-20", "00:00", "23:59", isAllDay=True, showAs="free"),
            ev("i-marker", "Salary changes due", "2026-11-12", "00:00", "23:59", isAllDay=True, showAs="free",
               attendees=(("Managers", "managers@example.com", "required"),)),
            ev("s1", "Leads sync", "2026-11-17", "09:00", "09:30", type="occurrence", seriesMasterId="series-1"),
            ev("s2", "Leads sync", "2026-11-24", "09:00", "09:30", type="occurrence", seriesMasterId="series-1"),
            ev("past", "Yesterday's thing", "2026-11-10", "10:00", "11:00"),
        ])

    def tearDown(self):
        self.temp.cleanup()

    def run_triage(self, raised=None):
        raised = {} if raised is None else raised
        items, seen, skipped = invites.triage(self.config, self.calendar, raised, log=lambda _: None, now=self.now)
        return {i["subject"].split(":")[0]: i for i in items}, seen, skipped, raised

    def test_recommendations_follow_the_evidence(self):
        items, _, skipped, _ = self.run_triage()
        rec = {name: item["recommendation"] for name, item in items.items()}
        self.assertEqual(rec["Planning day"], "decline")                     # leave
        self.assertEqual(items["Planning day"]["note"], "On annual leave 16/12 to 18/12")
        self.assertEqual(rec["Vendor demo"], "decline")                      # protected 1:1
        self.assertIn("an outside organiser", items["Vendor demo"]["check"])
        self.assertEqual(rec["Site meeting"], "decline")                     # travel in the middle of the day
        self.assertIn("travel mid-day", items["Site meeting"]["check"])
        self.assertEqual(rec["Away day"], "decline")                         # prep has nowhere to go before Tuesday's meeting
        self.assertIn("no free slot", items["Away day"]["check"])
        self.assertEqual(rec["Retro"], "accept")
        self.assertIn("optional", items["Retro"]["check"])
        self.assertEqual(rec["Salary changes due"], "accept")                # holds no time
        self.assertEqual(items["Salary changes due"]["clashes"], [])
        self.assertNotIn("Kurt Holiday - All Day", items)                    # skipped by rule
        self.assertNotIn("Yesterday's thing", items)
        self.assertEqual(skipped, 1)

    def test_what_can_move_comes_with_where_to(self):
        items, _, _, _ = self.run_triage()

        def moved(name):
            self.assertEqual(items[name]["recommendation"], "accept and move", items[name]["check"])
            return [(c["id"], c["to"], c["to_end"]) for c in items[name]["clashes"]]
        self.assertEqual(moved("Design review"), [("block", "2026-11-12T13:30", "2026-11-12T14:30")])   # nearest free
        self.assertEqual(moved("Architecture catch-up"), [("mine", "2026-11-11T16:00", "2026-11-11T16:50")])
        self.assertEqual(moved("Breakfast briefing"), [("travel-am", "2026-11-12T06:30", "2026-11-12T07:30")])  # in earlier
        self.assertEqual(moved("Social"), [("travel-pm", "2026-11-12T19:00", "2026-11-12T20:00")])  # home later
        self.assertEqual(moved("All hands"), [("update", "2026-11-13T15:00", "2026-11-13T15:30")])  # stays on Friday

    def test_a_series_is_raised_once_and_only_clashing_instances_are_declined(self):
        items, seen, _, raised = self.run_triage()
        series = items["Leads sync"]
        self.assertEqual((series["id"], series["series"]), ("series-1", "series-1"))
        self.assertEqual(series["recommendation"], "accept, and decline the clashing ones")
        self.assertEqual(series["decline"], ["s1"])                           # Tuesday's workshop
        again, _, _, _ = self.run_triage(raised)
        self.assertEqual(again, {})
        self.assertIn("invite:series-1", seen)

    def answer(self, name, action, **kwargs):
        number = next(i["n"] for i in triage.queue(self.config) if i["subject"].startswith(name))
        triage.answer(self.config, self.calendar, [str(number)], action, log=lambda _: None, **kwargs)

    def test_answers_decline_instances_never_the_series_unless_asked(self):
        items, _, _, _ = self.run_triage()
        triage.save_queue(self.config, list(items.values()))
        self.answer("Leads sync", "accept")
        self.assertEqual(self.calendar.answers[-1], ("series-1", "accept", "", True))       # a series is accepted whole
        self.assertIn("series-1", [i["id"] for i in triage.queue(self.config)])              # still to decline s1
        self.answer("Leads sync", "decline")
        self.assertEqual(self.calendar.answers[-1], ("s1", "decline", "", True))             # the instance only
        self.assertNotIn("series-1", [i["id"] for i in triage.queue(self.config)])
        triage.save_queue(self.config, list(items.values()))
        self.answer("Leads sync", "decline", series=True)
        self.assertEqual(self.calendar.answers[-1], ("series-1", "decline", "", True))       # only when asked

    def test_leave_note_no_response_where_none_asked_and_moves(self):
        items, _, _, _ = self.run_triage()
        triage.save_queue(self.config, list(items.values()))
        self.answer("Planning day", "decline")
        self.assertEqual(self.calendar.answers[-1], ("i-leave", "decline", "On annual leave 16/12 to 18/12", True))
        self.answer("Retro", "decline")
        self.assertEqual(self.calendar.answers[-1], ("i-free", "decline", "", False))
        self.answer("Architecture catch-up", "accept", move=True)
        self.assertEqual(self.calendar.moves, [("mine", dt.datetime(2026, 11, 11, 16, 0), dt.datetime(2026, 11, 11, 16, 50))])
        with self.assertRaises(LookupError):   # an invite isn't deleted like an email
            triage.delete(self.config, None, ["1"])

    def test_leave_sweep_finds_what_you_havent_declined_with_its_note(self):
        found = invites.leave_sweep(self.config, self.calendar, self.now.date(), 60)
        self.assertEqual([(e["id"], e["note"]) for e in found], [("i-leave", "On annual leave 16/12 to 18/12")])

    def test_a_single_day_of_leave_has_the_plain_note(self):
        away = {dt.date(2026, 11, 20)}
        self.assertEqual(invites.leave_note(self.config, dt.date(2026, 11, 20), away), "On annual leave")
        away = {dt.date(2026, 11, 20), dt.date(2026, 11, 23)}   # Friday and Monday: the weekend doesn't break it
        self.assertEqual(invites.leave_note(self.config, dt.date(2026, 11, 20), away), "On annual leave 20/11 to 23/11")


class RequestGraph:
    """Invitation emails in one folder, each linked to its meeting (or to none: declined, so removed)."""
    def __init__(self):
        req = "#microsoft.graph.eventMessageRequest"
        self.messages = [{"@odata.type": req, "id": "m1", "subject": "Accepted one"},
                         {"@odata.type": req, "id": "m2", "subject": "Declined one"},
                         {"@odata.type": req, "id": "m3", "subject": "Not answered yet"},
                         {"@odata.type": "#microsoft.graph.message", "id": "m4", "subject": "An ordinary email"}]
        self.events = {"m1": {"id": "e1", "responseStatus": {"response": "accepted"}}, "m2": {},
                       "m3": {"id": "e3", "responseStatus": {"response": "notResponded"}}}

    def pages(self, url, scopes=None):
        return iter(self.messages)

    def request(self, url, method="GET", body=None, scopes=None):
        return {"id": url.split("/")[3].split("?")[0], "event": self.events[url.split("/")[3].split("?")[0]]}


class Shortened(unittest.TestCase):
    """accept_shortened: a meeting you'd accepted, whose organiser only shortened it, is accepted unasked."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        path = pathlib.Path(self.temp.name).resolve() / "config.toml"
        path.write_text(SETTINGS.replace("[invites]\n", "[invites]\naccept_shortened = true\n"), encoding="utf-8")
        self.config = cfg.load(path)
        self.now = dt.datetime(2026, 11, 11, 9, 0)

    def tearDown(self):
        self.temp.cleanup()

    def run_with(self, before, after, dry_run=False):
        """Remember `before` (accepted), then triage `after` (the organiser's change)."""
        accepted, acted = {}, []
        invites.triage(self.config, FakeCalendar(before), {}, log=lambda _: None, now=self.now, accepted=accepted)
        calendar = FakeCalendar(after)
        items, seen, _ = invites.triage(self.config, calendar, {}, log=lambda _: None, now=self.now,
                                        accepted=accepted, acted=acted, dry_run=dry_run)
        return items, seen, acted, calendar, accepted

    def series(self, response, slots, ids="abc"):
        return [ev(f"pcb-{i}", "Alt Net PCB", day, start, end, response=response, type="occurrence", seriesMasterId="pcb")
                for i, (day, start, end) in zip(ids, slots)]

    def test_a_shortened_series_is_accepted_as_a_whole(self):
        before = self.series("accepted", [("2026-11-12", "10:00", "10:45"), ("2026-11-26", "10:00", "10:45")])
        # Rich shortens it, and extends the series by one: the new occurrence comes after the remembered ones
        after = self.series("notResponded", [("2026-11-12", "10:00", "10:30"), ("2026-11-26", "10:00", "10:30"),
                                             ("2026-12-10", "10:00", "10:30")])
        items, seen, acted, calendar, accepted = self.run_with(before, after)
        self.assertEqual(items, [])
        self.assertEqual(seen, set())                      # off the queue, if it was ever on it
        self.assertEqual(calendar.answers, [("pcb", "accept", "", True)])
        self.assertIn("10:00–10:45 to 10:00–10:30 (2 occurrences)", acted[0]["why"])
        self.assertEqual(accepted["pcb@2026-12-10"], ["2026-12-10T10:00", "2026-12-10T10:30"])   # remembered for next time

    def test_a_shortened_one_off_is_accepted_quietly_if_no_response_was_asked_for(self):
        _, _, acted, calendar, _ = self.run_with([ev("one", "Options", "2026-11-13", "14:00", "15:00", response="accepted")],
                                                 [ev("one", "Options", "2026-11-13", "14:15", "15:00", responseRequested=False)])
        self.assertEqual(calendar.answers, [("one", "accept", "", False)])
        self.assertEqual(len(acted), 1)

    def test_anything_more_than_a_shortening_is_raised(self):
        cases = {
            "moved later": ("14:00", "15:00", "14:30", "15:30"),
            "lengthened": ("14:00", "15:00", "14:00", "15:30"),
            "unchanged": ("14:00", "15:00", "14:00", "15:00"),
        }
        for name, (s0, e0, s1, e1) in cases.items():
            with self.subTest(name):
                items, _, acted, calendar, _ = self.run_with(
                    [ev("one", "Options", "2026-11-13", s0, e0, response="accepted")], [ev("one", "Options", "2026-11-13", s1, e1)])
                self.assertEqual((len(items), acted, calendar.answers), (1, [], []))

    def test_only_meetings_you_had_accepted_count(self):
        for response in ("tentativelyAccepted", "notResponded", "declined"):
            with self.subTest(response):
                items, _, acted, _, _ = self.run_with([ev("one", "Options", "2026-11-13", "14:00", "15:00", response=response)],
                                                      [ev("one", "Options", "2026-11-13", "14:00", "14:30")])
                self.assertEqual((len(items), acted), (1, []))

    def test_a_series_with_a_gap_in_what_was_accepted_is_raised(self):
        before = self.series("accepted", [("2026-11-12", "10:00", "10:45"), ("2026-11-26", "10:00", "10:45")], ids="ac")
        after = self.series("notResponded", [("2026-11-12", "10:00", "10:30"), ("2026-11-19", "10:00", "10:30"),
                                             ("2026-11-26", "10:00", "10:30")])   # a new week in the middle: the pattern changed
        items, _, acted, _, _ = self.run_with(before, after)
        self.assertEqual((len(items), acted), (1, []))

    def test_a_dry_run_answers_nothing_and_remembers_nothing(self):
        before = [ev("one", "Options", "2026-11-13", "14:00", "15:00", response="accepted")]
        _, _, acted, calendar, accepted = self.run_with(before, [ev("one", "Options", "2026-11-13", "14:00", "14:30")], dry_run=True)
        self.assertEqual((len(acted), calendar.answers), (1, []))
        self.assertEqual(accepted, {"one": ["2026-11-13T14:00", "2026-11-13T15:00"]})

    def test_one_week_shortened_is_accepted_on_its_own_even_though_the_series_was_raised_long_ago(self):
        # The real case (12 Nov 2026): Rich cut one week's Alt Net PCB to 30 minutes. The rest stays accepted.
        before = self.series("accepted", [("2026-11-12", "10:00", "10:45"), ("2026-11-26", "10:00", "10:45")])
        after = [ev("pcb-x", "Alt Net PCB", "2026-11-12", "10:00", "10:30", type="exception", seriesMasterId="pcb"),
                 ev("pcb-b", "Alt Net PCB", "2026-11-26", "10:00", "10:45", response="accepted", type="occurrence", seriesMasterId="pcb")]
        accepted, acted = {}, []
        invites.triage(self.config, FakeCalendar(before), {}, log=lambda _: None, now=self.now, accepted=accepted)
        calendar = FakeCalendar(after)
        items, _, _ = invites.triage(self.config, calendar, {"invite:pcb": "2026-11-01T09:00:00"}, log=lambda _: None,
                                     now=self.now, accepted=accepted, acted=acted)
        self.assertEqual(items, [])
        self.assertEqual(calendar.answers, [("pcb-x", "accept", "", True)])   # the occurrence: the series' answer misses it

    def test_off_unless_switched_on(self):
        self.config.invites.accept_shortened = False
        items, _, acted, calendar, _ = self.run_with([ev("one", "Options", "2026-11-13", "14:00", "15:00", response="accepted")],
                                                     [ev("one", "Options", "2026-11-13", "14:00", "14:30")])
        self.assertEqual((len(items), acted, calendar.answers), (1, [], []))


class Exceptions(unittest.TestCase):
    """An occurrence the organiser changed on its own isn't answered by answering its series."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.temp.name).resolve()
        (self.dir / "config.toml").write_text(SETTINGS, encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")
        self.now = dt.datetime(2026, 11, 11, 9, 0)

    def tearDown(self):
        self.temp.cleanup()

    def queue_and_accept(self, events, raised):
        calendar = FakeCalendar(events)
        items, _, _ = invites.triage(self.config, calendar, raised, log=lambda _: None, now=self.now)
        triage.save_queue(self.config, items)
        triage.answer(self.config, calendar, ["1"], "accept", log=lambda _: None)
        return items, calendar.answers

    def test_a_moved_week_of_an_answered_series_comes_back_on_its_own(self):
        events = [ev("w1", "Leads sync", "2026-11-17", "14:00", "14:30", type="exception", seriesMasterId="leads"),
                  ev("w2", "Leads sync", "2026-11-24", "09:00", "09:30", response="accepted", type="occurrence", seriesMasterId="leads")]
        items, answers = self.queue_and_accept(events, {"invite:leads": "2026-10-01T09:00:00"})
        self.assertEqual([(i["id"], i["series"], i["exceptions"]) for i in items], [("w1", None, [])])
        self.assertEqual(answers, [("w1", "accept", "", True)])            # that week only, not the series

    def test_a_new_series_with_a_changed_week_is_one_invite_and_accepting_it_answers_both(self):
        events = [ev("n1", "Design forum", "2026-11-17", "14:00", "15:00", type="occurrence", seriesMasterId="forum"),
                  ev("n2", "Design forum", "2026-11-24", "15:00", "16:00", type="exception", seriesMasterId="forum")]
        items, answers = self.queue_and_accept(events, {})
        self.assertEqual([(i["id"], i["exceptions"]) for i in items], [("forum", ["n2"])])
        self.assertEqual([a[0] for a in answers], ["forum", "n2"])


class ProposalCalendar(FakeCalendar):
    """Alex's own 1:1 with Yahya on Thu 12 Nov 12:00–12:50, and Friday's diary."""
    def between(self, start, end):
        s, e = (t.astimezone(dt.timezone.utc).replace(tzinfo=None) for t in (start, end))   # GMT in November: UTC = local
        return [x for x in self.items if invites._times(x)[0] < e and invites._times(x)[1] > s]

    def zone(self):
        return dt.timezone.utc


class ProposalMailbox:
    def __init__(self, proposed, attendees=1, organiser=True, fail=None):
        self.deleted, self.fail = [], fail
        self.message = {"@odata.type": "#microsoft.graph.eventMessageResponse", "id": "p1", "internetMessageId": "<p1>",
                        "subject": "New Time Proposed: GJ and YP catch-up", "receivedDateTime": "2026-11-11T20:13:00Z",
                        "from": {"emailAddress": {"name": "Yahya Patel", "address": "yahya@example.com"}}}
        utc = lambda t: {"dateTime": f"{t}:00.0000000", "timeZone": "UTC"}   # noqa: E731
        self.full = {"proposedNewTime": {"start": utc(proposed[0]), "end": utc(proposed[1])},
                     "startDateTime": utc("2026-11-12T12:00"), "endDateTime": utc("2026-11-12T12:50"),
                     "event": {"id": "yp", "isOrganizer": organiser}}
        self.graph = self

    def folder_id(self, name):
        return name

    def pages(self, url, scopes=None):
        return iter([self.message, {"@odata.type": "#microsoft.graph.message", "id": "m2", "subject": "Hello"}])

    def request(self, url, method="GET", body=None, scopes=None):
        if "proposedNewTime" in url:   # what Graph does: $select is read against the base message type
            raise urllib.error.HTTPError(url, 400, "Bad Request", {}, io.BytesIO(b""))
        if self.fail:
            raise urllib.error.HTTPError(url, self.fail, "x", {}, io.BytesIO(b""))
        return self.full

    def delete(self, message_id):
        self.deleted.append(message_id)


class Proposals(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        path = pathlib.Path(self.temp.name).resolve() / "config.toml"
        path.write_text(SETTINGS.replace("[invites]\n", "[invites]\nproposals_folders = ['Inbox']\naccept_proposals = true\n"),
                        encoding="utf-8")
        self.config = cfg.load(path)
        self.now = dt.datetime(2026, 11, 12, 9, 30)
        yahya = (("Alex Smith", ME, "required"), ("Yahya Patel", "yahya@example.com", "required"))
        self.diary = [ev("yp-12", "GJ and YP catch-up", "2026-11-12", "12:00", "12:50", organiser="Alex Smith", org_addr=ME,
                         attendees=yahya, type="occurrence", seriesMasterId="yp"),
                      ev("smt", "SMT", "2026-11-13", "11:00", "12:00", response="accepted"),
                      ev("aw", "GJ and AW catchup", "2026-11-13", "13:00", "13:50", **SELF),
                      ev("plan", "Work plan", "2026-11-13", "00:00", "23:59", isAllDay=True, showAs="free", **SELF)]

    def tearDown(self):
        self.temp.cleanup()

    def run_with(self, proposed, diary=None, **mailbox):
        box, calendar, raised = ProposalMailbox(proposed, **mailbox), ProposalCalendar(diary or self.diary), {}
        items, acted, seen = invites.proposals(self.config, box, calendar, raised, log=lambda _: None, now=self.now)
        return items, acted, calendar.moves, box.deleted

    def test_free_at_the_new_time_moves_that_occurrence_and_deletes_the_proposal(self):
        # The real case (12 Nov 2026): Yahya declined Thursday 12:00 and proposed Friday 12:00–12:30
        items, acted, moves, deleted = self.run_with(("2026-11-13T12:00", "2026-11-13T12:30"))
        self.assertEqual(items, [])
        self.assertEqual(moves, [("yp-12", dt.datetime(2026, 11, 13, 12, 0), dt.datetime(2026, 11, 13, 12, 30))])
        self.assertEqual(deleted, ["p1"])
        self.assertIn("proposes Fri 13 Nov 12:00–12:30 instead of Thu 12 Nov 12:00", acted[0]["why"])

    def test_anything_else_is_queued_with_the_reason(self):
        group = list(self.diary)
        group[0] = dict(group[0], attendees=group[0]["attendees"] + [{"type": "required", "emailAddress": {"address": "sam@example.com"}}])
        cases = {"clashes with SMT": (("2026-11-13T11:30", "2026-11-13T12:00"), {}, None),
                 "outside working hours": (("2026-11-13T18:30", "2026-11-13T19:00"), {}, None),
                 "has passed": (("2026-11-12T09:00", "2026-11-12T09:30"), {}, None),
                 "not your meeting": (("2026-11-13T12:00", "2026-11-13T12:30"), {"organiser": False}, None),
                 "1 other attendee(s) would move too": (("2026-11-13T12:00", "2026-11-13T12:30"), {}, group)}
        for reason, (proposed, mailbox, diary) in cases.items():
            with self.subTest(reason):
                items, acted, moves, deleted = self.run_with(proposed, diary, **mailbox)
                self.assertEqual((acted, moves, deleted), ([], [], []))
                self.assertIn(reason, items[0]["check"])
                self.assertEqual(items[0]["rule"], "New time proposed")

    def test_a_proposal_graph_wont_read_is_queued_not_fatal(self):
        items, acted, moves, deleted = self.run_with(("2026-11-13T12:00", "2026-11-13T12:30"), fail=400)
        self.assertEqual((acted, moves, deleted), ([], [], []))
        self.assertEqual(len(items), 1)
        self.assertIn("couldn't read the proposal", items[0]["check"])
        with self.assertRaises(urllib.error.HTTPError):
            self.run_with(("2026-11-13T12:00", "2026-11-13T12:30"), fail=502)

    def test_off_it_only_recommends(self):
        self.config.invites.accept_proposals = False
        items, acted, moves, _ = self.run_with(("2026-11-13T12:00", "2026-11-13T12:30"))
        self.assertEqual((acted, moves), ([], []))
        self.assertTrue(items[0]["check"].startswith("ACCEPT"))
        self.assertEqual(items[0]["move"]["start"], "2026-11-13T12:00")


class Requests(unittest.TestCase):
    def test_answered_or_removed_invitations_are_found_and_the_rest_left(self):
        found = invites.answered_requests(RequestGraph(), "folder")
        self.assertEqual([(m["id"], m["answer"]) for m in found], [("m1", "accepted"), ("m2", "gone")])


if __name__ == "__main__":
    unittest.main()
