using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>[sent] (1.19.0): tidying Sent Items by Graph's message type; rule forwards' copies filed; real mail untouched.</summary>
public sealed class SentTests : IDisposable
{
    private const string Base = """
        email = "alex@example.com"
        queue_file = "queue.json"
        state_file = "raised.json"
        deleted_file = "deleted.json"
        sent_file = "sent.json"
        log_file = "log.txt"

        [[folder]]
        name = "Triage"

        [[rule]]
        name = "Supplier notices"
        sender = 'maintenance@supplier\.example$'
        action = "forward"
        forward_to = ["team@example.com"]
        comment = "FYI - supplier notice below."
        to = 'Suppliers/Notices'
        file_sent = 'Suppliers/Notices'

        """;

    private const string AllNow = Base + """

        [sent]
        invites = "immediately"
        cancellations = "immediately"
        responses = "immediately"
        """;

    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);

    public void Dispose() => dir.Dispose();

    private static JsonObject Msg(string id, string subject, string sent, string? type = null, bool attachments = false,
        string to = "someone@example.com", string preview = "Hello")
    {
        var m = new JsonObject
        {
            ["id"] = id, ["subject"] = subject, ["sentDateTime"] = sent, ["hasAttachments"] = attachments, ["bodyPreview"] = preview,
            ["toRecipients"] = new JsonArray((JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = to } }),
        };
        if (type is not null)
            m["@odata.type"] = type;
        return m;
    }

    /// <summary>Sent Items, each meeting message's details, the folder tree, and every write.</summary>
    private sealed class Graph(List<JsonObject> sent, Dictionary<string, JsonObject> details) : IGraph
    {
        public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];
        public List<string> Reads { get; } = [];

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Reads.Add(url);
            if (url.StartsWith("/me/mailFolders/sentitems/messages", StringComparison.Ordinal))
                return sent.Select(m => (JsonObject)m.DeepClone()).ToList();
            if (url.StartsWith("/me/mailFolders?", StringComparison.Ordinal))
                return [new JsonObject { ["id"] = "f-sup", ["displayName"] = "Suppliers", ["childFolderCount"] = 1 }];
            if (url.Contains("/f-sup/childFolders"))
                return [new JsonObject { ["id"] = "f-notices", ["displayName"] = "Notices", ["childFolderCount"] = 0 }];
            return [];
        }

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            if (method == "GET" && url.StartsWith("/me/messages/", StringComparison.Ordinal) && details.TryGetValue(url["/me/messages/".Length..], out var d))
                return (JsonObject)d.DeepClone();
            if (method != "GET")
                Calls.Add((method, url, body?.DeepClone()));
            return [];
        }
    }

    private static JsonObject Meeting(string type, string end = "2026-11-20T11:00:00") => new()
    {
        ["meetingMessageType"] = type,
        ["startDateTime"] = new JsonObject { ["dateTime"] = "2026-11-20T10:00:00", ["timeZone"] = "UTC" },
        ["endDateTime"] = new JsonObject { ["dateTime"] = end, ["timeZone"] = "UTC" },
    };

    private (Graph Graph, Settings Settings) Setup(string text)
    {
        var settings = dir.Settings(text);
        var sent = new List<JsonObject>
        {
            Msg("req", "Project sync", "2026-11-19T09:00:00Z", "#microsoft.graph.eventMessageRequest"),
            Msg("upd", "Project sync (updated)", "2026-11-19T09:05:00Z", "#microsoft.graph.eventMessageRequest"),
            Msg("can", "Canceled: Old meeting", "2026-11-18T09:00:00Z", "#microsoft.graph.eventMessage"),
            Msg("acc", "Accepted: Design review", "2026-11-17T09:00:00Z", "#microsoft.graph.eventMessageResponse"),
            Msg("ten", "Tentative: Forum", "2026-11-16T09:00:00Z", "#microsoft.graph.eventMessageResponse"),
            Msg("dec", "Declined: Social", "2026-11-15T09:00:00Z", "#microsoft.graph.eventMessageResponse"),
            Msg("att", "Workshop with the pack", "2026-11-14T09:00:00Z", "#microsoft.graph.eventMessageRequest", attachments: true),
            Msg("real", "Re: the budget", "2026-11-13T09:00:00Z"),
            Msg("fwd", "FW: Emergency maintenance", "2026-11-12T09:00:00Z", to: "team@example.com", preview: "FYI - supplier notice below. From: ..."),
            Msg("fwd-other", "FW: Something else", "2026-11-11T09:00:00Z", to: "team@example.com", preview: "Have a look at this"),
        };
        var details = new Dictionary<string, JsonObject>
        {
            ["req"] = Meeting("meetingRequest", "2026-11-30T11:00:00"),
            ["upd"] = Meeting("meetingRequest", "2026-11-30T11:00:00"),
            ["can"] = Meeting("meetingCancelled"),
            ["acc"] = Meeting("meetingAccepted"),
            ["ten"] = Meeting("meetingTenativelyAccepted"),   // Graph's own spelling
            ["dec"] = Meeting("meetingDeclined"),
            ["att"] = Meeting("meetingRequest"),
        };
        return (new Graph(sent, details), settings);
    }

    private (List<SentFinding> Findings, bool First) Plan(Graph graph, Settings settings, JsonObject? state = null)
    {
        using (Clock.Use(new FixedTime(now)))
        {
            var (findings, first, _) = SentItems.Plan(settings, new Mailbox(graph, WriteDoor.Refusing().Mail), state ?? [], now);
            return (findings, first);
        }
    }

    private static Dictionary<string, string> Actions(List<SentFinding> findings) => findings.ToDictionary(f => f.Id, f => $"{f.Kind}:{f.Action}");

    [Fact]
    public void each_kind_is_told_apart_by_graph_type_and_real_mail_is_left_alone()
    {
        var (graph, settings) = Setup(AllNow);
        var (findings, first) = Plan(graph, settings);
        Assert.True(first);
        Assert.Equal(new Dictionary<string, string>
        {
            ["req"] = "invites:delete", ["upd"] = "invites:delete", ["can"] = "cancellations:delete", ["acc"] = "responses:delete",
            ["ten"] = "responses:delete", ["dec"] = "responses:delete", ["att"] = "invites:skip", ["real"] = "other:leave",
            ["fwd"] = "forwards:move", ["fwd-other"] = "other:leave",
        }, Actions(findings));
        Assert.Equal("Suppliers/Notices", findings.Single(f => f.Id == "fwd").To);
        Assert.Contains("attachments", findings.Single(f => f.Id == "att").Why);
        Assert.Empty(graph.Calls);   // a plan only reads
    }

    [Fact]
    public void kinds_left_out_are_off_and_untouched()
    {
        var (graph, settings) = Setup(Base + "\n[sent]\ncancellations = \"immediately\"\n");
        var actions = Actions(Plan(graph, settings).Findings);
        Assert.Equal("invites:leave", actions["req"]);
        Assert.Equal("responses:leave", actions["acc"]);
        Assert.Equal("cancellations:delete", actions["can"]);
        Assert.Equal("forwards:move", actions["fwd"]);   // a rule's file_sent applies whenever [sent] is there
    }

    [Fact]
    public void after_meeting_and_after_sending_wait_for_their_time()
    {
        var (graph, settings) = Setup(Base + "\n[sent]\ninvites = \"after_meeting\"\ninvites_days = 1\ncancellations = \"after_meeting\"\n"
                                           + "responses = \"after_sending\"\nresponses_days = 7\n");
        var findings = Plan(graph, settings).Findings;
        var actions = Actions(findings);
        Assert.Equal("invites:wait", actions["req"]);              // meeting ends 30 Nov, + 1 day
        Assert.Equal("cancellations:delete", actions["can"]);     // that meeting ended 20 Nov
        Assert.Equal("responses:wait", actions["acc"]);           // sent 17 Nov + 7 days = 24 Nov
        Assert.Equal("responses:delete", actions["dec"]);         // sent 15 Nov + 7 days = 22 Nov
        Assert.Equal(new DateTime(2026, 12, 1), findings.Single(f => f.Id == "req").Due!.Value.Date);
    }

    [Fact]
    public void doing_it_deletes_files_records_and_remembers_where_it_got_to()
    {
        var (graph, settings) = Setup(AllNow);
        var (findings, first) = Plan(graph, settings);
        var state = new JsonObject();
        using (Clock.Use(new FixedTime(now)))
            SentItems.Apply(settings, new Mailbox(graph, WriteDoor.Live(graph).Mail), state, findings, first, _ => { });
        var moves = graph.Calls.Where(c => c.Url.EndsWith("/move", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, moves.Count);   // six deleted, one filed
        Assert.Contains(moves, c => c.Url == "/me/messages/req/move" && c.Body!.Req("destinationId") == "deleteditems");
        Assert.Contains(moves, c => c.Url == "/me/messages/fwd/move" && c.Body!.Req("destinationId") == "f-notices");
        Assert.DoesNotContain(graph.Calls, c => c.Url.Contains("/real") || c.Url.Contains("/att") || c.Url.Contains("/fwd-other"));
        Assert.DoesNotContain(graph.Calls, c => c.Url == "/me/messages/fwd" && c.Method == "PATCH");   // a move keeps it as it was
        var gone = Triage.Deleted(settings);
        Assert.Equal(7, gone.Count);
        Assert.Contains(gone, g => g.Req("rule") == "Sent Items: invites" && g.Req("did") == "deleted from Sent Items");
        Assert.Contains(gone, g => g.Req("rule") == "Sent Items: forwards" && g.Req("did").Contains("filed to Suppliers/Notices"));
        var saved = StateFiles.LoadObject(settings.SentFile, () => []);
        Assert.Equal("2026-11-19T09:05:00Z", saved.Req("until"));

        // The next run reads only what's new (with an hour's overlap), and skips what it has seen.
        graph.Reads.Clear();
        var again = Plan(graph, settings, saved).Findings;
        Assert.Contains(graph.Reads, u => u.Contains("sentDateTime+ge+2026-11-19T08%3A05%3A00Z") || u.Contains("sentDateTime ge 2026-11-19T08:05:00Z"));
        Assert.DoesNotContain(again, f => f.Id is "req" or "upd");
    }

    [Fact]
    public void the_command_previews_then_acts_and_a_run_waits_for_the_first()
    {
        var (graph, _) = Setup(AllNow);
        string Cli(params string[] args)
        {
            var output = new StringWriter();
            using (Clock.Use(new FixedTime(now)))
                MailTriage.Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), .. args],
                    new CliHost { Out = output, Err = output, Echo = false, Graph = (_, _) => graph });
            return output.ToString();
        }
        var settings = Settings.Load(Path.Combine(dir.Path, "config.toml"));
        using (Clock.Use(new FixedTime(now)))
            SentItems.RunScheduled(settings, new Mailbox(graph, WriteDoor.Live(graph).Mail), false, _ => { });
        Assert.Empty(graph.Calls);   // nothing until the first `sent --yes`
        var preview = Cli("sent");
        Assert.Contains("10 message(s) read (all of Sent Items: the first run)", preview);
        Assert.Contains("invites (meeting requests and updates you sent): 3, 14/11/2026 to 19/11/2026: would delete 2; 1 skipped", preview);
        Assert.Contains("everything else: 2", preview);
        Assert.Contains("skipped: 14/11/2026  Workshop with the pack  (it has attachments: never deleted)", preview);
        Assert.Contains("Nothing changed.", preview);
        Assert.Empty(graph.Calls);
        var done = Cli("sent", "--yes");
        Assert.Contains("deleted 2", done);
        Assert.Equal(7, graph.Calls.Count(c => c.Url.EndsWith("/move", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("[sent]\ninvite = \"immediately\"\n", "'invite' isn't a setting")]
    [InlineData("[sent]\ninvites = \"now\"\n", "invites should be one of")]
    [InlineData("[sent]\ninvites = \"immediately\"\ninvites_days = 3\n", "only for after_meeting or after_sending")]
    [InlineData("[sent]\nresponses = \"after_sending\"\nresponses_days = -1\n", "0 or more")]
    public void sent_settings_are_read_strictly(string sent, string message)
    {
        var error = Assert.Throws<SettingsException>(() => dir.Settings(Base + "\n" + sent, "bad.toml"));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void file_sent_is_only_for_forward_rules()
    {
        var error = Assert.Throws<SettingsException>(() => dir.Settings(
            "email = \"a@example.com\"\n[[rule]]\nname = \"x\"\nsender = 'a'\naction = \"delete\"\nfile_sent = 'Inbox'\n", "fs.toml"));
        Assert.Contains("file_sent is only for action forward", error.Message);
    }
}
