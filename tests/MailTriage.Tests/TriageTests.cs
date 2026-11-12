using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage.Tests;

// test_triage.py, test for test, under the same names.

public abstract class TriageBase : IDisposable
{
    public const string SettingsText = """
        queue_file = "queue.json"
        state_file = "raised.json"
        deleted_file = "deleted.json"
        log_file = "triage.log"

        [[folder]]
        name = "Triage"

        [[rule]]
        name = "News"
        sender = '@news\.example\.com$'
        action = "delete"

        [[rule]]
        name = "Old name"
        sender = 'legacy@example\.com'
        action = "resolve"

        [[rule]]
        name = "Digest"
        sender = 'digest@example\.com'
        match = 'flow\(s\) have failed'
        action = "check"
        check = "flow-failures"

        [[rule]]
        name = "Boss"
        sender = 'boss@example\.com'
        action = "review"

        [[rule]]
        name = "Plans"
        sender = 'plans@example\.com'
        action = "file"
        to = 'Inbox/Triage'

        [[flow_check]]
        flow = "Weekly report"
        output = 'out/report-*.html'
        pending = 'requests/*.txt'

        """;

    protected readonly TempDir Dir = new();
    protected readonly Settings Config;
    protected readonly List<string> Lines = [];

    protected TriageBase() => Config = Dir.Settings(SettingsText);

    public void Dispose() => Dir.Dispose();

    public static JsonObject Message(int n, string address, string subject = "Hello", string preview = "") => new()
    {
        ["id"] = $"graph-{n}", ["internetMessageId"] = $"<{n}@example.com>", ["subject"] = subject, ["bodyPreview"] = preview,
        ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = address } },
        ["receivedDateTime"] = "2026-11-10T08:00:00Z", ["webLink"] = "x",
    };

    protected RunReport RunTriage(FakeGraph graph, bool dryRun = false) =>
        Triage.Run(Config, new Mailbox(graph, WriteDoor.Live(graph).Mail, dryRun), null, dryRun, Lines.Add);

    protected static JsonObject Obj(string key, string value) => new() { [key] = value };
}

public sealed class SettingsTests : TriageBase
{
    [Fact]
    public void loads_rules_in_order_with_the_old_action_name()
    {
        Assert.Equal(["News", "Old name", "Digest", "Boss", "Plans"], Config.Rules.Select(r => r.Name));
        Assert.Equal("delete", Config.Rules[1].Action);
        Assert.Equal(Path.Combine(Dir.Path, "queue.json"), Config.QueueFile);
    }

    [Fact]
    public void first_match_wins_and_match_needs_both()
    {
        Assert.Equal("News", Config.RuleFor("a@news.example.com", "x")!.Name);
        Assert.Null(Config.RuleFor("digest@example.com", "Weekly summary"));
        Assert.Equal("Digest", Config.RuleFor("DIGEST@example.com", "2 flow(s) have failed")!.Name);
    }

    [Fact]
    public void bad_settings_are_refused()
    {
        foreach (var bad in new[]
                 {
                     "[[folder]]\nname=\"x\"\n[[rule]]\nname=\"r\"\nsender=\"a\"\naction=\"archive\"\n",
                     "[[folder]]\nname=\"x\"\n[[rule]]\nname=\"r\"\nsender=\"(\"\naction=\"delete\"\n",
                     "[[rule]]\nname=\"r\"\nsender=\"a\"\naction=\"delete\"\n",
                 })
            Assert.Throws<SettingsException>(() => Dir.Settings(bad, "bad.toml"));
    }
}

public sealed class Run : TriageBase
{
    [Fact]
    public void deletes_queues_and_never_deletes_an_unknown_sender()
    {
        var graph = new FakeGraph([Message(1, "a@news.example.com"), Message(2, "boss@example.com"), Message(3, "stranger@example.org")]);
        var report = RunTriage(graph);
        Assert.Equal((1, 2), (report.Deleted, report.New));
        Assert.True(graph.Has("POST", "/me/messages/graph-1/move", Obj("destinationId", "deleteditems")));
        var queued = Triage.Queue(Config);
        Assert.Equal([("1", "Boss"), ("2", "no rule matched")], queued.Select(i => (Py.Str(i["n"]), i.Req("rule"))));
    }

    [Fact]
    public void an_inbox_folder_gets_only_the_rules_that_name_it()
    {
        Config.Folders[0].Unmatched = "leave";
        Config.Rules.Insert(0, new Rule { Name = "Accepts", Sender = @"@example\.org$", Action = "delete", Match = "^Accepted: ", Folders = ["triage"] });
        var graph = new FakeGraph([Message(1, "a@news.example.com"), Message(3, "stranger@example.org"),
                                   Message(5, "colleague@example.org", "Accepted: Weekly 1-2-1")]);
        var report = RunTriage(graph);
        Assert.Equal((1, 0), (report.Deleted, report.New));                       // only the accept
        Assert.Empty(Triage.Queue(Config));
        Assert.Equal(["graph-1", "graph-3"], graph.Messages.Select(m => m.Req("id")).Order());   // the rest left alone
        var normal = new Folder("Other");
        Assert.Null(Config.RuleFor("colleague@example.org", "Accepted: x", normal));   // named elsewhere
        Assert.Equal("News", Config.RuleFor("a@news.example.com", "x", normal)!.Name);
    }

    [Fact]
    public void file_moves_it_to_a_folder_by_path_left_unread()
    {
        var graph = new FakeGraph([Message(4, "plans@example.com", "Sprint 04 Plan")]);
        var report = RunTriage(graph);
        Assert.Equal((1, 0, 0), (report.Filed, report.New, report.Deleted));
        Assert.True(graph.Has("POST", "/me/messages/graph-4/move", Obj("destinationId", "f1")));
        Assert.DoesNotContain(graph.Calls, c => c.Method == "PATCH");   // not marked read
        Assert.Equal("Inbox/Triage", Triage.Deleted(Config)[0].Req("to"));
        Assert.Throws<SettingsException>(() => Dir.Settings("[[folder]]\nname=\"x\"\n[[rule]]\nname=\"r\"\nsender=\"a\"\naction=\"file\"\n", "bad.toml"));
    }

    [Fact]
    public void each_email_is_raised_once()
    {
        var graph = new FakeGraph([Message(2, "boss@example.com")]);
        RunTriage(graph);
        Triage.Clear(Config, Lines.Add);
        var report = RunTriage(graph);
        Assert.Equal((0, 1), (report.New, report.Kept));
        Assert.Empty(Triage.Queue(Config));
    }

    [Fact]
    public void waiting_items_carry_over_until_decided_and_drop_when_gone()
    {
        var graph = new FakeGraph([Message(2, "boss@example.com"), Message(3, "x@example.org")]);
        RunTriage(graph);
        Assert.Equal(2, RunTriage(graph).Waiting);
        graph.Messages = graph.Messages.Take(1).ToList();   // one deleted in Outlook
        RunTriage(graph);
        Assert.Equal(["graph-2"], Triage.Queue(Config).Select(i => i.Req("id")));
    }

    [Fact]
    public void dry_run_changes_nothing()
    {
        var graph = new FakeGraph([Message(1, "a@news.example.com"), Message(2, "boss@example.com")]);
        RunTriage(graph, dryRun: true);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
        Assert.False(File.Exists(Config.QueueFile) || File.Exists(Config.StateFile));
    }

    [Fact]
    public void rule_deletions_are_recorded_until_the_queue_is_cleared()
    {
        var graph = new FakeGraph([Message(1, "a@news.example.com", "Daily news"), Message(2, "boss@example.com")]);
        RunTriage(graph, dryRun: true);
        Assert.Empty(Triage.Deleted(Config));   // a dry run deletes nothing, so records nothing
        RunTriage(graph);
        Assert.Equal([("News", "Daily news")], Triage.Deleted(Config).Select(i => (i.Req("rule"), i.Req("subject"))));
        Triage.Clear(Config, Lines.Add);
        Assert.Empty(Triage.Deleted(Config));
        Assert.Single(Triage.Deleted(Config, everything: true));
        graph.Messages.Add(Message(6, "b@news.example.com", "More news"));
        RunTriage(graph);
        Assert.Equal(["More news"], Triage.Deleted(Config).Select(i => i.Req("subject")));
    }

    [Fact]
    public void delete_and_keep_by_number()
    {
        var graph = new FakeGraph([Message(2, "boss@example.com"), Message(3, "x@example.org"), Message(4, "y@example.org")]);
        RunTriage(graph);
        Triage.Delete(Config, new Mailbox(graph, WriteDoor.Live(graph).Mail), ["1"], Lines.Add);
        Assert.True(graph.Has("POST", "/me/messages/graph-2/move", Obj("destinationId", "deleteditems")));
        Triage.Keep(Config, ["2"], Lines.Add);   // numbers were re-assigned after the delete
        Assert.Equal(["graph-3"], Triage.Queue(Config).Select(i => i.Req("id")));
        Assert.Throws<LookupException>(() => Triage.Keep(Config, ["9"], Make.Quiet));
    }
}

public sealed class FlowFailures : TriageBase
{
    private void Touch(string name, DateTimeOffset when)
    {
        var path = Dir.Write(name, "x");
        File.SetLastWriteTimeUtc(path, when.UtcDateTime);
    }

    private static JsonObject DigestMessage() => Message(5, "digest@example.com", "Some of your flow(s) have failed");

    [Fact]
    public void reads_the_digest() => Assert.Equal([("Weekly report flow", 3L)], Checks.FlowFailures(FakeGraph.Digest));

    [Fact]
    public void recovered_flow_is_deleted()
    {
        var now = DateTimeOffset.UtcNow;
        Touch("requests/old.txt", now.AddHours(-1));
        Touch("out/report-1.html", now);
        Assert.Equal(1, RunTriage(new FakeGraph([DigestMessage()])).Deleted);
    }

    [Fact]
    public void still_failing_flow_is_raised_with_why()
    {
        var now = DateTimeOffset.UtcNow;
        Touch("out/report-1.html", now.AddHours(-1));
        Touch("requests/new.txt", now);
        RunTriage(new FakeGraph([DigestMessage()]));
        Assert.Contains("haven't been picked up", Triage.Queue(Config)[0].Req("check"));
    }

    [Fact]
    public void a_flow_with_no_check_is_raised()
    {
        RunTriage(new FakeGraph([DigestMessage()], "<tr><td>Something else</td><td>1</td></tr>"));
        Assert.Contains("no [[flow_check]]", Triage.Queue(Config)[0].Req("check"));
    }

    [Fact]
    public void without_pending_output_must_be_newer_than_the_digest()
    {
        var check = new FlowCheck("x", Path.Combine(Dir.Path, "out", "*.html"));
        Touch("out/a.html", DateTimeOffset.UtcNow);
        Assert.True(Checks.FlowRecovered(check, DateTimeOffset.UtcNow.AddHours(-1)).Item1);
        Assert.False(Checks.FlowRecovered(check, DateTimeOffset.UtcNow.AddHours(1)).Item1);
    }
}

public sealed class Watching : TriageBase
{
    [Fact]
    public void working_hours_and_weekdays()
    {
        var monday = new DateTime(2026, 11, 9);
        Assert.Equal(("07:00", "19:00"), Config.Hours);
        Assert.True(Watcher.Due(Config, monday.AddHours(7)));
        Assert.False(Watcher.Due(Config, monday.AddHours(19)));
        Assert.False(Watcher.Due(Config, monday.AddHours(6).AddMinutes(59)));
        Assert.False(Watcher.Due(Config, new DateTime(2026, 11, 14, 10, 0, 0)));   // Saturday
        Config.Weekdays = false;
        Assert.True(Watcher.Due(Config, new DateTime(2026, 11, 14, 10, 0, 0)));
    }

    [Fact]
    public void bad_hours_are_refused() =>
        Assert.Throws<SettingsException>(() => Dir.Settings("hours = \"7am-7pm\"\n[[folder]]\nname = \"x\"\n", "bad.toml"));
}

public sealed class Commands : TriageBase
{
    private (int Code, string Out) Cli(params string[] args)
    {
        var output = new StringWriter();
        var code = MailTriage.Cli.Run(["--config", Path.Combine(Dir.Path, "config.toml"), .. args],
            new CliHost { Out = output, Err = output, Graph = (_, _) => throw new InvalidOperationException("no Graph in tests") });
        return (code, output.ToString());
    }

    [Fact]
    public void list_deleted_which_and_clear()
    {
        RunTriage(new FakeGraph([Message(1, "a@news.example.com", "Daily news"), Message(2, "boss@example.com")]));
        Assert.Contains("1 deleted by the rules", Cli("list").Out);
        var (_, deleted) = Cli("deleted");
        Assert.Contains("News (1)", deleted);
        Assert.Contains("Daily news", deleted);
        Assert.Contains("Boss: review", Cli("which", "boss@example.com").Out);
        Assert.Equal(0, Cli("clear").Code);
        Assert.Contains("Nothing deleted", Cli("deleted").Out);
        Assert.Contains("Nothing waiting", Cli("list").Out);
    }
}

public sealed class WatcherLoop : TriageBase
{
    [Fact]
    public void runs_at_once_then_only_when_due_and_notifies_after_three_failures_in_a_row()
    {
        var clock = new DateTime(2026, 11, 14, 22, 0, 0);   // a Saturday night: only the first run is due
        var runs = 0;
        var notes = new List<string>();
        var loops = 0;
        var code = Watcher.Watch(Config, (_, _) =>
        {
            runs++;
            throw new InvalidOperationException("Graph is down");
        }, (title, _) => notes.Add(title), stop: () => loops++ >= 200, sleep: _ => clock = clock.AddMinutes(1), now: () => clock);
        Assert.Equal(0, code);
        Assert.Equal(1, runs);                                   // nothing more is due before Monday 07:00
        Assert.Empty(notes);                                     // one failure isn't worth a notification
        var log = File.ReadAllText(Config.LogFile);
        Assert.Contains("run failed: InvalidOperationException: Graph is down", log);
        Assert.Contains("  alive", log);
        Assert.Matches(@"(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}  watching 1 folder\(s\) every 15 min, 07:00-19:00 Mon-Fri, and once now", log);

        clock = new DateTime(2026, 11, 16, 9, 0, 0);            // Monday: every 15 minutes
        loops = 0;
        Watcher.Watch(Config, (_, _) => { runs++; throw new InvalidOperationException("still down"); },
            (title, _) => notes.Add(title), stop: () => loops++ >= 60, sleep: _ => clock = clock.AddMinutes(1), now: () => clock);
        Assert.Equal(["mail-triage couldn't run"], notes);       // the third failure in a row, once
    }

    [Fact]
    public void a_second_watcher_for_the_same_settings_exits_with_3()
    {
        using var held = MailTriage.Interim.Exclusive.SingleInstance(Watcher.WatchLock(Config));
        Assert.Equal(Watcher.AlreadyRunning, Watcher.Watch(Config, (_, _) => throw new InvalidOperationException(), (_, _) => { }, stop: () => true));
    }

    [Fact]
    public void the_lock_names_are_the_python_ones() =>
        Assert.Equal($"mail-triage-run-{WinBackground.Locks.Key(Config.Path)}", Watcher.RunLock(Config));
}
