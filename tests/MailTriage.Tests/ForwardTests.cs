using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>The forward action (1.9.0): forward with a covering comment, mark read, file; never forwarded twice.</summary>
public sealed class ForwardTests : IDisposable
{
    private const string Forward = """

        [[rule]]
        name = "Supplier notices"
        sender = 'maintenance@supplier\.example$'
        action = "forward"
        forward_to = ["team@example.com"]
        comment = "FYI - for the team."
        to = 'Inbox/Triage'

        """;

    /// <summary>The rule goes with the other [[rule]]s: a kind of table split by another is refused.</summary>
    private static string WithRule(string rule) => "forwarded_file = \"forwarded.json\"\n" + TriageBase.SettingsText.Replace("[[flow_check]]", rule + "\n[[flow_check]]");

    private static readonly string Text = WithRule(Forward);

    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly List<string> lines = [];

    public ForwardTests() => config = dir.Settings(Text);

    public void Dispose() => dir.Dispose();

    private static JsonObject Notice(int n) => TriageBase.Message(n, "maintenance@supplier.example", "Emergency maintenance");

    /// <summary>The FakeGraph, plus a message GET by id, and a POST that fails (once) the way a lost answer does.</summary>
    private sealed class Graph(FakeGraph inner, string? failOn = null) : IGraph
    {
        private string? failOn = failOn;

        public FakeGraph Inner => inner;

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            if (failOn is not null && url.EndsWith(failOn, StringComparison.Ordinal))
            {
                failOn = null;
                inner.Calls.Add((method, url, body?.DeepClone()));
                throw new M365Graph.GraphRequestException(System.Net.HttpStatusCode.GatewayTimeout, method, url, "");
            }
            if (method == "GET" && url.StartsWith("/me/messages/", StringComparison.Ordinal) && url.Contains("?$select=id,"))
            {
                var id = url["/me/messages/".Length..url.IndexOf('?')];
                return (JsonObject)inner.Messages.First(m => m.Str("id") == id).DeepClone();
            }
            return inner.Request(url, method, body, scopes, headers);
        }

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) =>
            inner.Pages(url, scopes, headers);
    }

    private RunReport Run(IGraph graph, bool dryRun = false, IMailWriter? writer = null) =>
        Triage.Run(config, new Mailbox(graph, writer ?? WriteDoor.Live(graph).Mail, dryRun), null, dryRun, lines.Add);

    private static List<string> Writes(FakeGraph graph) =>
        graph.Calls.Where(c => c.Method != "GET").Select(c => $"{c.Method} {c.Url} {PyJson.Dump(c.Body, null, false)}").ToList();

    private JsonObject State() => JsonNode.Parse(File.ReadAllText(config.ForwardedFile))!.AsObject();

    [Fact]
    public void the_rule_loads_with_mark_read_on_by_default()
    {
        var rule = config.Rules.Single(r => r.Action == "forward");
        Assert.Equal(["team@example.com"], rule.ForwardTo!);
        Assert.Equal(("FYI - for the team.", "Inbox/Triage", true), (rule.Comment, rule.To, rule.MarkRead));
        Assert.Equal(Path.Combine(dir.Path, "forwarded.json"), config.ForwardedFile);
        var off = dir.Settings(WithRule("[[rule]]\nname = \"n\"\nsender = 'x'\naction = \"forward\"\n"
                                + "forward_to = [\"team@example.com\"]\ncomment = \"x\"\nmark_read = false\n"), "off.toml").Rules.Single(r => r.Action == "forward");
        Assert.Equal((false, null), (off.MarkRead, off.To));
        Assert.False(config.Rules.Single(r => r.Name == "Plans").MarkRead);   // file still leaves mail unread
    }

    [Theory]
    [InlineData("forward_to = [\"team@example.com\"]\n", "")]                          // no comment
    [InlineData("comment = \"x\"\n", "")]                                              // no forward_to
    [InlineData("forward_to = \"team@example.com\"\ncomment = \"x\"\n", "")]           // not a list
    [InlineData("forward_to = []\ncomment = \"x\"\n", "")]                              // empty
    [InlineData("forward_to = [\"not an address\"]\ncomment = \"x\"\n", "")]
    [InlineData("forward_to = [\"team@example.com\"]\ncomment = \"  \"\n", "")]         // blank comment
    [InlineData("forward_to = [\"team@example.com\"]\ncomment = \"x\"\n", "mark_read = \"yes\"\n")]
    public void a_bad_forward_rule_is_refused(string keys, string extra) =>
        Assert.Throws<SettingsException>(() => dir.Settings(
            "[[folder]]\nname = \"x\"\n[[rule]]\nname = \"r\"\nsender = \"a\"\naction = \"forward\"\n" + keys + extra, "bad.toml"));

    [Fact]
    public void forwards_then_marks_read_then_files_and_records_it()
    {
        var fake = new FakeGraph([Notice(1), TriageBase.Message(2, "boss@example.com")]);
        var report = Run(new Graph(fake));
        Assert.Equal((1, 1, 0), (report.Forwarded, report.New, report.Deleted));
        Assert.Equal(
        [
            """POST /me/messages/graph-1/forward {"comment": "FYI - for the team.", "toRecipients": [{"emailAddress": {"address": "team@example.com"}}]}""",
            """PATCH /me/messages/graph-1 {"isRead": true}""",
            """POST /me/messages/graph-1/move {"destinationId": "f1"}""",
        ], Writes(fake));
        Assert.Contains(lines, l => l.StartsWith("forwarded to team@example.com and filed to Inbox/Triage (marked read)  [Supplier notices]", StringComparison.Ordinal));
        var entry = Triage.Deleted(config).Single();
        Assert.Equal(("Supplier notices", "Inbox/Triage", "forwarded to team@example.com, marked read"), (entry.Req("rule"), entry.Req("to"), entry.Req("did")));
        Assert.Equal(["team@example.com"], entry.Get("forward_to").Strings());
        Assert.Equal("sent", State().Get("<1@example.com>").Req("state"));
        Assert.Equal(["Boss"], Triage.Queue(config).Select(i => i.Req("rule")));
    }

    [Fact]
    public void a_dry_run_says_what_it_would_do_and_writes_nothing()
    {
        var fake = new FakeGraph([Notice(1)]);
        var report = Run(new Graph(fake), dryRun: true);
        Assert.Equal(1, report.Forwarded);
        Assert.Contains(lines, l => l.StartsWith("would forward to team@example.com and file to Inbox/Triage (marked read)  [Supplier notices]", StringComparison.Ordinal));
        Assert.Empty(Writes(fake));
        Assert.False(File.Exists(config.ForwardedFile));
    }

    [Fact]
    public void a_forward_whose_answer_was_lost_is_never_sent_again()
    {
        var fake = new FakeGraph([Notice(1)]);
        Assert.ThrowsAny<Exception>(() => Run(new Graph(fake, failOn: "/forward")));
        Assert.Equal("sending", State().Get("<1@example.com>").Req("state"));   // recorded before the POST

        fake.Calls.Clear();
        var report = Run(new Graph(fake));
        Assert.Empty(Writes(fake));                                             // not resent, not filed
        Assert.Equal((0, 1), (report.Forwarded, report.New));
        var queued = Triage.Queue(config).Single();
        Assert.Contains("may already have gone", queued.Req("check"));
        Assert.Contains("check Sent Items", queued.Req("check"));

        fake.Calls.Clear();
        Run(new Graph(fake));                                                   // raised once, then just waits
        Assert.Empty(Writes(fake));
        Assert.Single(Triage.Queue(config));
    }

    [Fact]
    public void forwarded_but_not_filed_is_filed_without_forwarding_again()
    {
        var fake = new FakeGraph([Notice(1)]);
        Assert.ThrowsAny<Exception>(() => Run(new Graph(fake, failOn: "/move")));
        Assert.Equal("sent", State().Get("<1@example.com>").Req("state"));
        fake.Calls.Clear();
        Assert.Equal(1, Run(new Graph(fake)).Forwarded);
        Assert.Equal(["""PATCH /me/messages/graph-1 {"isRead": true}""", """POST /me/messages/graph-1/move {"destinationId": "f1"}"""], Writes(fake));
    }

    [Fact]
    public void a_journal_run_sends_nothing_and_records_no_forward()
    {
        var fake = new FakeGraph([Notice(1)]);
        var door = WriteDoor.Journaling();
        Run(new Graph(fake), writer: door.Mail);
        Assert.Empty(Writes(fake));
        Assert.StartsWith("POST /me/messages/graph-1/forward ", door.Journal[0].Text);
        Assert.False(File.Exists(config.ForwardedFile));   // a live run will still forward it
    }

    [Fact]
    public void refuse_mode_refuses_a_forward()
    {
        var door = WriteDoor.Refusing();
        Assert.Throws<WriteRefusedException>(() => door.Mail.Forward("m1", ["a@example.com"], "x"));
        Assert.Throws<WriteRefusedException>(() => door.Mail.MarkRead("m1"));
    }

    private (int Code, string Out) Cli(IGraph graph, params string[] args)
    {
        var output = new StringWriter();
        var code = MailTriage.Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), .. args],
            new CliHost { Out = output, Err = output, Graph = (_, _) => graph });
        return (code, output.ToString());
    }

    /// <summary>An email queued before its rule existed, as the Trooli notices were.</summary>
    private void QueueBeforeTheRule(FakeGraph fake)
    {
        var before = dir.Settings("forwarded_file = \"forwarded.json\"\n" + TriageBase.SettingsText, "before.toml");
        Triage.Run(before, new Mailbox(fake, WriteDoor.Live(fake).Mail), null, false, _ => { });
        Assert.Equal(["no rule matched", "Boss"], Triage.Queue(config).Select(i => i.Req("rule")));
        fake.Calls.Clear();
    }

    [Fact]
    public void forward_applies_the_rule_to_queued_emails_from_the_command_line()
    {
        var fake = new FakeGraph([Notice(1), TriageBase.Message(2, "boss@example.com")]);
        QueueBeforeTheRule(fake);
        var graph = new Graph(fake);

        var (code, output) = Cli(graph, "forward", "1", "--rule", "Boss");
        Assert.Equal(2, code);                                                  // Boss doesn't forward
        Assert.Contains("doesn't forward", output);
        Assert.Equal(2, Cli(graph, "forward", "2").Code);                       // no forward rule matches the boss
        Assert.Equal(2, Cli(graph, "forward", "2", "--yes").Code);              // refused the same way with --yes
        Assert.Empty(Writes(fake));

        (code, output) = Cli(graph, "forward", "1", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("forwarded to team@example.com and filed to Inbox/Triage (marked read) by hand", output);
        Assert.Equal(3, Writes(fake).Count);
        Assert.Single(Writes(fake), w => w.StartsWith("POST /me/messages/graph-1/forward ", StringComparison.Ordinal));   // forwarded once
        Assert.Equal(["Boss"], Triage.Queue(config).Select(i => i.Req("rule")));
        Assert.Contains("Supplier notices (1)", Cli(graph, "deleted").Out);
        Assert.Contains("-> filed to Inbox/Triage  -> forwarded to team@example.com, marked read", Cli(graph, "deleted").Out);

        fake.Calls.Clear();
        fake.Messages = [Notice(1), TriageBase.Message(2, "boss@example.com")];   // as if the move hadn't happened
        Run(graph);                                                              // raised: a run leaves it alone
        Assert.Empty(Writes(fake));
    }

    [Fact]
    public void forward_by_hand_never_resends_a_lost_forward()
    {
        var fake = new FakeGraph([Notice(1)]);
        QueueBeforeTheRuleOne(fake);
        Assert.Equal(1, Cli(new Graph(fake, failOn: "/forward"), "forward", "1", "--yes").Code);   // the failure is logged, exit 1
        fake.Calls.Clear();
        var (preview, shown) = Cli(new Graph(fake), "forward", "1");             // the preview says so too
        Assert.Equal(0, preview);
        Assert.Contains("Wouldn't be forwarded: a forward to team@example.com may already have gone", shown);
        Assert.Contains("Nothing sent.", shown);
        Assert.DoesNotContain("--yes", shown);
        var (code, output) = Cli(new Graph(fake), "forward", "1", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("may already have gone", output);
        Assert.Empty(Writes(fake));
        Assert.Contains("may already have gone", Triage.Queue(config).Single().Req("check"));
    }

    private void QueueBeforeTheRuleOne(FakeGraph fake)
    {
        var before = dir.Settings("forwarded_file = \"forwarded.json\"\n" + TriageBase.SettingsText, "before.toml");
        Triage.Run(before, new Mailbox(fake, WriteDoor.Live(fake).Mail), null, false, _ => { });
        fake.Calls.Clear();
    }

    // ---- 1.28.0: forward previews without --yes -----------------------------------------------------------------

    [Fact]
    public void forward_without_yes_previews_each_email_and_writes_nothing_even_with_writes_live()
    {
        var fake = new FakeGraph([Notice(1), TriageBase.Message(2, "boss@example.com")]);
        QueueBeforeTheRule(fake);
        var queued = File.ReadAllText(config.QueueFile);
        var deleted = File.ReadAllText(config.DeletedFile);
        var raised = File.ReadAllText(config.StateFile);
        var (code, output) = Cli(new Graph(fake), "--writes", "live", "forward", "1");
        Assert.Equal(0, code);
        Assert.Contains("Would forward 1 email:", output);
        Assert.Contains("1. 2026-11-10  maintenance@supplier.example: Emergency maintenance", output);
        Assert.Contains("rule \"Supplier notices\": forward to team@example.com and file to Inbox/Triage (marked read), with the comment \"FYI - for the team.\"", output);
        Assert.Contains("No email in the settings", output);                   // can't tell who's outside
        Assert.Contains("Nothing sent. Run again with --yes to forward it.", output);
        Assert.Empty(Writes(fake));
        Assert.Equal((queued, deleted, raised), (File.ReadAllText(config.QueueFile), File.ReadAllText(config.DeletedFile), File.ReadAllText(config.StateFile)));   // unchanged
        Assert.False(File.Exists(config.ForwardedFile));
    }

    [Fact]
    public void the_forward_preview_flags_recipients_outside_your_domain()
    {
        const string rule = """

            [[rule]]
            name = "Supplier notices"
            sender = 'maintenance@supplier\.example$'
            action = "forward"
            forward_to = ["team@example.com", "partner@vendor.example", "ops@sub.example.com"]
            comment = "FYI"

            """;
        dir.Settings("email = \"alex@example.com\"\n" + WithRule(rule));            // config.toml, now with your address
        var fake = new FakeGraph([Notice(1)]);
        QueueBeforeTheRuleOne(fake);
        var (code, output) = Cli(new Graph(fake), "forward", "1");
        Assert.Equal(0, code);
        Assert.Contains("forward to team@example.com, partner@vendor.example, ops@sub.example.com", output);
        Assert.Contains("OUTSIDE example.com: partner@vendor.example, ops@sub.example.com", output);
        Assert.DoesNotContain("No email in the settings", output);
        Assert.Empty(Writes(fake));
    }

    [Fact]
    public void a_preview_with_everyone_inside_the_domain_flags_no_one()
    {
        dir.Settings("email = \"alex@example.com\"\n" + Text);
        var fake = new FakeGraph([Notice(1)]);
        QueueBeforeTheRuleOne(fake);
        var (_, output) = Cli(new Graph(fake), "forward", "1");
        Assert.DoesNotContain("OUTSIDE", output);
    }

    [Fact]
    public void a_run_still_forwards_by_rule_without_yes()
    {
        var fake = new FakeGraph([Notice(1)]);
        var (code, _) = Cli(new Graph(fake), "run");                              // the watcher's path: unchanged
        Assert.Equal(0, code);
        Assert.Single(Writes(fake), w => w.StartsWith("POST /me/messages/graph-1/forward ", StringComparison.Ordinal));
        Assert.Equal("sent", State().Get("<1@example.com>").Req("state"));
    }
}
