using System.Text.Json.Nodes;
using ActivityJournal;
using PyCompat;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

/// <summary>1.33.0: `requeue` puts a kept email back on the queue as it was, and each thing mail-triage does is a line in
/// the shared activity journal. Every email, sender and meeting here is made up.</summary>
public sealed class RequeueAndJournalTests : IDisposable
{
    private const string SettingsText = """
        queue_file = "queue.json"
        state_file = "raised.json"
        deleted_file = "deleted.json"
        kept_file = "kept.json"
        journal_file = "state/journal.jsonl"
        log_file = "triage.log"

        [[folder]]
        name = "Triage"

        [[rule]]
        name = "News"
        sender = '@news\.example\.com$'
        action = "delete"
        why = "Newsletters: read on the web if at all"

        [[rule]]
        name = "Boss"
        sender = 'boss@example\.com'
        action = "review"

        [[rule]]
        name = "Plans"
        sender = 'plans@example\.com'
        action = "file"
        to = 'Inbox/Triage'

        """;

    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly List<string> lines = [];

    public RequeueAndJournalTests() => config = dir.Settings(SettingsText);

    public void Dispose() => dir.Dispose();

    private static JsonObject Message(int n, string address, string subject) => TriageBase.Message(n, address, subject);

    private RunReport Run(FakeGraph graph, bool dryRun = false, WriteDoor? door = null) =>
        Triage.Run(config, new Mailbox(graph, (door ?? WriteDoor.Live(graph)).Mail, dryRun), null, dryRun, lines.Add);

    private IReadOnlyList<JournalEntry> Journal() => new Journal(config.JournalFile!).Read();

    private (int Code, string Output) Cli(params string[] args)
    {
        var output = new StringWriter();
        var code = MailTriage.Cli.Run(["--config", config.Path, .. args],
            new CliHost { Out = output, Err = output, Echo = false, Graph = (_, _) => throw new InvalidOperationException("no Graph in these tests") });
        return (code, output.ToString());
    }

    /// <summary>Three queued: the boss's, two no rule matched; one deleted by News, one filed by Plans.</summary>
    private FakeGraph Mailbox() => new([
        Message(1, "boss@example.com", "Budget for Q1"),
        Message(2, "a@news.example.com", "This week's digest"),
        Message(3, "stranger@example.org", "Quick question"),
        Message(4, "plans@example.com", "Sprint 04 Plan"),
        Message(5, "someone@example.org", "Lunch on Friday?"),
    ]);

    [Fact]
    public void Keep_then_requeue_puts_it_back_as_it_was_at_the_end()
    {
        Run(Mailbox());
        var before = Triage.Queue(config);
        Assert.Equal(["Budget for Q1", "Quick question", "Lunch on Friday?"], before.Select(i => i.Req("subject")));
        // The fields a queued invite carries, to show requeue keeps every one.
        var queue = Triage.Queue(config);
        queue[0]["recommendation"] = "accept";
        queue[0]["clashes"] = new JsonArray(new JsonObject { ["subject"] = "Focus time" });
        Triage.SaveQueue(config, queue);
        var original = Triage.Queue(config)[0];

        Assert.Equal(1, Triage.Keep(config, ["1"], lines.Add));
        Assert.Equal(["Quick question", "Lunch on Friday?"], Triage.Queue(config).Select(i => i.Req("subject")));
        var kept = Assert.Single(Kept.All(config));
        Assert.Equal(1, kept.N);
        Assert.False(kept.Item.ContainsKey("n"));

        var back = Kept.Requeue(config, ["1"], lines.Add);
        Assert.Single(back);
        var after = Triage.Queue(config);
        Assert.Equal(["Quick question", "Lunch on Friday?", "Budget for Q1"], after.Select(i => i.Req("subject")));
        var requeued = after[2];
        Assert.Equal(3, (int)requeued["n"]!);   // the next number, at the end
        original["n"] = 3;
        Assert.True(JsonNode.DeepEquals(original, requeued), requeued.ToJsonString());
        Assert.Empty(Kept.All(config));
        Assert.Contains(lines, l => l.StartsWith("requeued as 3  boss@example.com: Budget for Q1", StringComparison.Ordinal));
    }

    [Fact]
    public void Requeue_by_id_or_key_and_numbers_stay_put()
    {
        Run(Mailbox());
        Triage.Keep(config, ["1", "2"], lines.Add);   // the boss's (graph-1), then the stranger's (graph-3)
        Triage.Keep(config, ["1"], lines.Add);        // what's now first: Lunch (graph-5)
        Assert.Equal([1L, 2L, 3L], Kept.All(config).Select(r => r.N));
        Kept.Requeue(config, ["graph-3"], lines.Add);           // by its Graph id
        Kept.Requeue(config, ["<5@example.com>"], lines.Add);   // by its internet message ID
        Assert.Equal([1L], Kept.All(config).Select(r => r.N));
        Assert.Equal(["Quick question", "Lunch on Friday?"], Triage.Queue(config).Select(i => i.Req("subject")));
        Triage.Keep(config, ["2"], lines.Add);
        Assert.Equal([1L, 4L], Kept.All(config).Select(r => r.N));   // a new number, never one reused
    }

    [Fact]
    public void Requeue_refuses_what_isnt_kept_or_is_queued_already_and_changes_nothing()
    {
        Run(Mailbox());
        Triage.Keep(config, ["1"], lines.Add);
        var queueBefore = File.ReadAllText(config.QueueFile);
        Assert.Throws<LookupException>(() => Kept.Requeue(config, ["1", "9"], lines.Add));
        Assert.Equal(queueBefore, File.ReadAllText(config.QueueFile));
        Assert.Single(Kept.All(config));

        // Raised again meanwhile (cleared by hand from the kept record's point of view): already in the queue.
        var queue = Triage.Queue(config);
        queue.Add((JsonObject)Kept.All(config)[0].Item.DeepClone());
        Triage.SaveQueue(config, queue);
        var error = Assert.Throws<LookupException>(() => Kept.Requeue(config, ["1"], lines.Add));
        Assert.Contains("in the queue already, as 3", error.Message, StringComparison.Ordinal);
        Assert.Single(Kept.All(config));
    }

    [Fact]
    public void A_requeued_email_is_carried_by_the_next_run_while_its_still_in_the_folder()
    {
        var graph = Mailbox();
        Run(graph);
        Triage.Keep(config, ["2"], lines.Add);
        Kept.Requeue(config, ["1"], lines.Add);
        Run(graph);   // seen before (raised), still in Triage: carried, not queued twice
        Assert.Equal(["Budget for Q1", "Lunch on Friday?", "Quick question"], Triage.Queue(config).Select(i => i.Req("subject")));

        Triage.Keep(config, ["3"], lines.Add);
        Kept.Requeue(config, ["2"], lines.Add);
        graph.Messages = graph.Messages.Where(m => m.Req("id") != "graph-3").ToList();   // moved away in Outlook meanwhile
        Run(graph);
        Assert.DoesNotContain(Triage.Queue(config), i => i.Req("id") == "graph-3");
    }

    [Fact]
    public void Kept_records_go_after_60_days()
    {
        Run(Mailbox());
        using (Clock.Use(new FixedTime(new DateTime(2026, 9, 1, 9, 0, 0))))
            Triage.Keep(config, ["1"], lines.Add);
        Triage.Keep(config, ["1"], lines.Add);   // now: the September record is past 60 days, so it goes
        Assert.Equal(["Quick question"], Kept.All(config).Select(r => r.Item.Req("subject")));
    }

    [Fact]
    public void The_commands_kept_and_requeue()
    {
        Run(Mailbox());
        Assert.Equal(0, Cli("keep", "1").Code);
        var (code, text) = Cli("kept");
        Assert.Equal(0, code);
        Assert.Contains("  1. 2026-11-10  boss@example.com", text, StringComparison.Ordinal);
        Assert.Contains("`mail-triage requeue <n>`", text, StringComparison.Ordinal);
        var json = JsonNode.Parse(Cli("kept", "--json").Output)!.AsArray();
        Assert.Equal("graph-1", json.Single()!["id"]!.GetValue<string>());
        Assert.Equal("Boss", json.Single()!["rule"]!.GetValue<string>());

        Assert.Equal(1, Cli("requeue", "7").Code);   // nothing kept is 7
        Assert.Equal(2, Cli("requeue").Code);        // a reference is needed
        Assert.Equal(0, Cli("requeue", "1").Code);
        Assert.Equal("Budget for Q1", Triage.Queue(config)[^1].Req("subject"));
        Assert.Contains("Nothing kept", Cli("kept").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_journals_what_it_deleted_and_filed_and_nothing_it_only_queued()
    {
        var graph = Mailbox();
        Run(graph);
        var all = Journal();
        Assert.Equal(["delete", "file"], all.Select(e => e.What));
        Assert.All(all, e => Assert.Equal(("mail-triage", By.Auto, Sensitivity.Private), (e.Tool, e.By, e.Sensitivity)));

        var deleted = all[0];
        Assert.Equal("This week's digest", deleted.Subject);
        Assert.Equal("News", deleted.Rule);
        Assert.Equal("Newsletters: read on the web if at all", deleted.Why);   // the rule's why
        Assert.Equal("<2@example.com>", deleted.Item);
        var record = Restores.Listed(config, true).Single(x => x.Entry.Str("rule") == "News");
        Assert.Equal($"mail-triage restore {record.Id}", deleted.Undo);   // the id `deleted` and `restore` use

        var filed = all[1];
        Assert.Equal(("Sprint 04 Plan", "Plans", (string?)null), (filed.Subject, filed.Rule, filed.Undo));
        Assert.Null(filed.Outcome);

        // Nothing new the second time: what's left was queued already.
        Run(graph);
        Assert.Equal(2, Journal().Count);
    }

    [Fact]
    public void A_dry_run_a_journal_mode_run_and_no_journal_file_write_no_lines()
    {
        Run(Mailbox(), dryRun: true);
        Assert.False(File.Exists(config.JournalFile));
        Run(Mailbox(), door: WriteDoor.Journaling());
        Assert.False(File.Exists(config.JournalFile));

        using var other = new TempDir();
        var without = other.Settings(SettingsText.Replace("journal_file = \"state/journal.jsonl\"\n", ""));
        Assert.Null(without.JournalFile);
        var graph = Mailbox();
        Assert.Equal(1, Triage.Run(without, new Mailbox(graph, WriteDoor.Live(graph).Mail), null, false, lines.Add).Deleted);
        Assert.False(Directory.Exists(Path.Combine(other.Path, "state")));
        Assert.False(File.Exists(config.JournalFile));
    }

    [Fact]
    public void Keep_requeue_delete_and_clear_by_hand_are_journalled_with_their_undo()
    {
        var graph = Mailbox();
        Run(graph);
        Triage.Keep(config, ["1"], lines.Add);
        Kept.Requeue(config, ["1"], lines.Add);
        Triage.Delete(config, new Mailbox(graph, WriteDoor.Live(graph).Mail), ["1"], lines.Add);   // the stranger's
        Triage.Clear(config, lines.Add);

        var byHand = Journal().Where(e => e.By == By.Hand).ToList();
        Assert.Equal(["keep", "requeue", "delete", "clear"], byHand.Select(e => e.What));
        Assert.Equal(("Budget for Q1", "mail-triage requeue graph-1"), (byHand[0].Subject, byHand[0].Undo));
        Assert.Equal(("Budget for Q1", "mail-triage keep graph-1"), (byHand[1].Subject, byHand[1].Undo));
        Assert.Equal("Quick question", byHand[2].Subject);
        var record = Restores.Listed(config, true).Single(x => x.Entry.Truthy("by_hand"));
        Assert.Equal($"mail-triage restore {record.Id}", byHand[2].Undo);
        Assert.Equal(("by hand", Sensitivity.Private), (byHand[2].Rule, byHand[2].Sensitivity));
        Assert.Equal((null, Sensitivity.Normal, (string?)null), (byHand[3].Subject, byHand[3].Sensitivity, byHand[3].Undo));
        Assert.Equal("2 items off the queue, each left where it is; what the rules deleted marked as seen", byHand[3].Why);
    }

    [Fact]
    public void A_journal_that_cant_be_written_never_fails_the_run()
    {
        File.WriteAllText(Path.Combine(dir.Path, "state"), "a file where the journal's folder should be");
        var report = Run(Mailbox());
        Assert.Equal(1, report.Deleted);
        Assert.Contains(lines, l => l.StartsWith("journal: couldn't write to ", StringComparison.Ordinal));
        Assert.Equal(3, Triage.Queue(config).Count);
    }

    [Fact]
    public void A_recorded_calendar_change_is_journalled_from_its_tag_and_an_unconfirmed_one_says_so()
    {
        var entry = new JsonObject
        {
            ["when"] = "2026-11-25T09:00:00.000000", ["folder"] = "Calendar", ["received"] = "2026-12-02T10:00", ["from"] = "Sam Jones",
            ["subject"] = "Project sync: Wed 02 Dec 10:00–11:00", ["rule"] = "Declined: on leave", ["did"] = "On leave that day",
        };
        Activity.Mark(entry, "decline", "evt-1", Activity.Answer("evt-1", "decline"));
        var tag = Activity.Take(entry);
        Assert.False(entry.ContainsKey(Activity.Tag));   // never into the deleted file
        Activity.Recorded(config, lines.Add, entry, tag, By.Auto);
        var unconfirmed = (JsonObject)entry.DeepClone();
        Unconfirmed.Mark(unconfirmed, "timed out", "Declined");
        Activity.Recorded(config, lines.Add, unconfirmed, tag, By.Auto);
        Activity.Recorded(config, lines.Add, entry, null, By.Auto);   // untagged (a preview's "would"): no line

        var all = Journal();
        Assert.Equal(2, all.Count);
        Assert.Equal(("decline", "evt-1", "mail-triage respond evt-1 accept", "On leave that day"), (all[0].What, all[0].Item, all[0].Undo, all[0].Why));
        Assert.Null(all[0].Outcome);
        Assert.Equal(JournalEntry.Unconfirmed, all[1].Outcome);
    }

    [Fact]
    public void Undo_hints_for_answers_and_moves()
    {
        Assert.Equal("mail-triage respond e1 decline", Activity.Answer("e1", "accept"));
        Assert.Equal("mail-triage respond e1 decline", Activity.Answer("e1", "tentative"));
        Assert.Null(Activity.Answer("e1", "accept", series: true));
        Assert.Equal("mail-triage move e2 2026-12-01T09:30 10:00", Activity.MoveBack("e2", new DateTime(2026, 12, 1, 9, 30, 0), new DateTime(2026, 12, 1, 10, 0, 0)));
        Assert.Null(Activity.MoveBack("e2", new DateTime(2026, 12, 1, 23, 30, 0), new DateTime(2026, 12, 2, 0, 30, 0)));
    }

    [Fact]
    public void Invites_answered_without_asking_are_tagged_for_the_journal()
    {
        var auto = dir.Settings(InviteSettings, "auto.toml");
        auto.Invites!.AutoAccept = true;
        auto.Invites.MonthsAhead = 3;
        var calendar = new FakeCalendar([
            Ev("i-free", "Project sync", "2026-11-13", "10:00", "11:00", attendees: [("Alex Smith", Me, "required"), ("Sam Jones", "sam@example.com", "required")]),
        ]);
        var acted = new List<JsonObject>();
        InviteTriage.Triage(auto, calendar, [], Quiet, new DateTime(2026, 11, 11, 9, 0, 0), acted: acted);
        var tag = Assert.Single(acted)[Activity.Tag]!.AsObject();
        Assert.Equal(("accept", "i-free", "mail-triage respond i-free decline"), (tag.Str("what"), tag.Str("item"), tag.Str("undo")));
    }
}
