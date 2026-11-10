"""Run with: python -m unittest discover -s tests"""

import datetime as dt
import json
import os
import pathlib
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))

from mail_triage import checks, config as cfg, triage  # noqa: E402

SETTINGS = """
queue_file = "queue.json"
state_file = "raised.json"
deleted_file = "deleted.json"
log_file = "triage.log"

[[folder]]
name = "Triage"

[[rule]]
name = "News"
sender = '@news\\.example\\.com$'
action = "delete"

[[rule]]
name = "Old name"
sender = 'legacy@example\\.com'
action = "resolve"

[[rule]]
name = "Digest"
sender = 'digest@example\\.com'
match = 'flow\\(s\\) have failed'
action = "check"
check = "flow-failures"

[[rule]]
name = "Boss"
sender = 'boss@example\\.com'
action = "review"

[[flow_check]]
flow = "Weekly report"
output = 'out/report-*.html'
pending = 'requests/*.txt'
"""

DIGEST = """<table><tr><td>Weekly report flow</td><td>3</td></tr><tr><td>Name</td><td>Failures</td></tr></table>"""


def message(n, address, subject="Hello", preview=""):
    return {"id": f"graph-{n}", "internetMessageId": f"<{n}@example.com>", "subject": subject, "bodyPreview": preview,
            "from": {"emailAddress": {"address": address}}, "receivedDateTime": "2026-11-10T08:00:00Z", "webLink": "x"}


class FakeGraph:
    def __init__(self, messages, body=DIGEST):
        self.messages, self.body, self.calls = messages, body, []

    def pages(self, url, scopes=None):
        if url.startswith("/me/mailFolders?"):
            return iter([{"id": "top", "displayName": "Inbox", "childFolderCount": 1}])
        if "/childFolders" in url:
            return iter([{"id": "f1", "displayName": "Triage", "childFolderCount": 0}])
        return iter(list(self.messages))

    def request(self, url, method="GET", body=None, scopes=None):
        self.calls.append((method, url, body))
        if url.endswith("/move"):
            moved = url.split("/")[3]
            self.messages = [m for m in self.messages if m["id"] != moved]
        return {"body": {"content": self.body}} if "$select=body" in url else {}


class Base(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.temp.name).resolve()
        (self.dir / "config.toml").write_text(SETTINGS, encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")
        self.lines = []

    def tearDown(self):
        self.temp.cleanup()

    def run_triage(self, graph, dry_run=False):
        return triage.run(self.config, triage.Mailbox(graph, dry_run), dry_run, self.lines.append)


class Settings(Base):
    def test_loads_rules_in_order_with_the_old_action_name(self):
        self.assertEqual([r.name for r in self.config.rules], ["News", "Old name", "Digest", "Boss"])
        self.assertEqual(self.config.rules[1].action, "delete")
        self.assertEqual(self.config.queue_file, self.dir / "queue.json")

    def test_first_match_wins_and_match_needs_both(self):
        self.assertEqual(self.config.rule_for("a@news.example.com", "x").name, "News")
        self.assertIsNone(self.config.rule_for("digest@example.com", "Weekly summary"))
        self.assertEqual(self.config.rule_for("DIGEST@example.com", "2 flow(s) have failed").name, "Digest")

    def test_bad_settings_are_refused(self):
        for bad in ('[[folder]]\nname="x"\n[[rule]]\nname="r"\nsender="a"\naction="archive"\n',
                    '[[folder]]\nname="x"\n[[rule]]\nname="r"\nsender="("\naction="delete"\n',
                    '[[rule]]\nname="r"\nsender="a"\naction="delete"\n'):
            (self.dir / "bad.toml").write_text(bad, encoding="utf-8")
            with self.assertRaises(ValueError):
                cfg.load(self.dir / "bad.toml")


class Run(Base):
    def test_deletes_queues_and_never_deletes_an_unknown_sender(self):
        graph = FakeGraph([message(1, "a@news.example.com"), message(2, "boss@example.com"), message(3, "stranger@example.org")])
        report = self.run_triage(graph)
        self.assertEqual((report["deleted"], report["new"]), (1, 2))
        self.assertIn(("POST", "/me/messages/graph-1/move", {"destinationId": "deleteditems"}), graph.calls)
        queued = triage.queue(self.config)
        self.assertEqual([(i["n"], i["rule"]) for i in queued], [(1, "Boss"), (2, "no rule matched")])

    def test_each_email_is_raised_once(self):
        graph = FakeGraph([message(2, "boss@example.com")])
        self.run_triage(graph)
        triage.clear(self.config, self.lines.append)
        report = self.run_triage(graph)
        self.assertEqual((report["new"], report["kept"]), (0, 1))
        self.assertEqual(triage.queue(self.config), [])

    def test_waiting_items_carry_over_until_decided_and_drop_when_gone(self):
        graph = FakeGraph([message(2, "boss@example.com"), message(3, "x@example.org")])
        self.run_triage(graph)
        self.assertEqual(self.run_triage(graph)["waiting"], 2)
        graph.messages = graph.messages[:1]   # one deleted in Outlook
        self.run_triage(graph)
        self.assertEqual([i["id"] for i in triage.queue(self.config)], ["graph-2"])

    def test_dry_run_changes_nothing(self):
        graph = FakeGraph([message(1, "a@news.example.com"), message(2, "boss@example.com")])
        self.run_triage(graph, dry_run=True)
        self.assertFalse([c for c in graph.calls if c[0] != "GET"])
        self.assertFalse(self.config.queue_file.exists() or self.config.state_file.exists())

    def test_rule_deletions_are_recorded_until_the_queue_is_cleared(self):
        graph = FakeGraph([message(1, "a@news.example.com", "Daily news"), message(2, "boss@example.com")])
        self.run_triage(graph, dry_run=True)
        self.assertEqual(triage.deleted(self.config), [])   # a dry run deletes nothing, so records nothing
        self.run_triage(graph)
        gone = triage.deleted(self.config)
        self.assertEqual([(i["rule"], i["subject"]) for i in gone], [("News", "Daily news")])
        triage.clear(self.config, self.lines.append)
        self.assertEqual(triage.deleted(self.config), [])
        self.assertEqual(len(triage.deleted(self.config, everything=True)), 1)
        graph.messages.append(message(6, "b@news.example.com", "More news"))
        self.run_triage(graph)
        self.assertEqual([i["subject"] for i in triage.deleted(self.config)], ["More news"])

    def test_delete_and_keep_by_number(self):
        graph = FakeGraph([message(2, "boss@example.com"), message(3, "x@example.org"), message(4, "y@example.org")])
        self.run_triage(graph)
        triage.delete(self.config, triage.Mailbox(graph), ["1"], self.lines.append)
        self.assertIn(("POST", "/me/messages/graph-2/move", {"destinationId": "deleteditems"}), graph.calls)
        triage.keep(self.config, ["2"], self.lines.append)   # numbers were re-assigned after the delete
        self.assertEqual([i["id"] for i in triage.queue(self.config)], ["graph-3"])
        with self.assertRaises(LookupError):
            triage.keep(self.config, ["9"])


class FlowFailures(Base):
    def touch(self, name, when):
        path = self.dir / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("x")
        os.utime(path, (when, when))

    def digest(self):
        return message(5, "digest@example.com", "Some of your flow(s) have failed")

    def test_reads_the_digest(self):
        self.assertEqual(checks.flow_failures(DIGEST), [("Weekly report flow", 3)])

    def test_recovered_flow_is_deleted(self):
        now = time.time()
        self.touch("requests/old.txt", now - 3600)
        self.touch("out/report-1.html", now)
        graph = FakeGraph([self.digest()])
        self.assertEqual(self.run_triage(graph)["deleted"], 1)

    def test_still_failing_flow_is_raised_with_why(self):
        now = time.time()
        self.touch("out/report-1.html", now - 3600)
        self.touch("requests/new.txt", now)
        self.run_triage(FakeGraph([self.digest()]))
        self.assertIn("haven't been picked up", triage.queue(self.config)[0]["check"])

    def test_a_flow_with_no_check_is_raised(self):
        body = "<tr><td>Something else</td><td>1</td></tr>"
        self.run_triage(FakeGraph([self.digest()], body))
        self.assertIn("no [[flow_check]]", triage.queue(self.config)[0]["check"])

    def test_without_pending_output_must_be_newer_than_the_digest(self):
        check = cfg.FlowCheck("x", str(self.dir / "out" / "*.html"))
        self.touch("out/a.html", time.time())
        self.assertTrue(checks.flow_recovered(check, dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=1))[0])
        self.assertFalse(checks.flow_recovered(check, dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1))[0])


class Watching(Base):
    def test_working_hours_and_weekdays(self):
        import datetime as dt
        from mail_triage.watch import due
        monday = dt.datetime(2026, 11, 9)
        self.assertEqual(self.config.hours, ("07:00", "19:00"))
        self.assertTrue(due(self.config, monday.replace(hour=7)))
        self.assertFalse(due(self.config, monday.replace(hour=19)))
        self.assertFalse(due(self.config, monday.replace(hour=6, minute=59)))
        self.assertFalse(due(self.config, dt.datetime(2026, 11, 14, 10)))   # Saturday
        self.config.weekdays = False
        self.assertTrue(due(self.config, dt.datetime(2026, 11, 14, 10)))

    def test_bad_hours_are_refused(self):
        (self.dir / "bad.toml").write_text('hours = "7am-7pm"\n[[folder]]\nname = "x"\n', encoding="utf-8")
        with self.assertRaises(ValueError):
            cfg.load(self.dir / "bad.toml")


class Commands(Base):
    """The command line, end to end, on everything that doesn't need Graph."""

    def cli(self, *args):
        import contextlib, io
        from mail_triage import cli
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = cli.main(["--config", str(self.dir / "config.toml"), *args])
        return code, out.getvalue()

    def test_list_deleted_which_and_clear(self):
        self.run_triage(FakeGraph([message(1, "a@news.example.com", "Daily news"), message(2, "boss@example.com")]))
        code, out = self.cli("list")
        self.assertIn("1 deleted by the rules", out)
        code, out = self.cli("deleted")
        self.assertIn("News (1)", out)
        self.assertIn("Daily news", out)
        self.assertIn("Boss: review", self.cli("which", "boss@example.com")[1])
        self.assertEqual(self.cli("clear")[0], 0)
        self.assertIn("Nothing deleted", self.cli("deleted")[1])
        self.assertIn("Nothing waiting", self.cli("list")[1])


if __name__ == "__main__":
    unittest.main()
