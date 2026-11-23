using System.Net;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>`mail-triage send-invite` (1.24.0): an invitation the owner has approved, sent from their calendar, never twice.</summary>
public sealed class SendInviteTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);   // Mon 23 Nov 2026
    private int sleeps;

    private const string Rooms = """

        [hold]
        rooms = [
            { name = "MTG Room 3.A.1", address = "MTGRoom3.A.1@example.com" },
            { name = "MTG Room 2.D.2", address = "MTGRoom2.D.2@example.com" },
        ]
        """;

    private static readonly string Base = "sent_invites_file = \"sent-invites.json\"\n" + Make.InviteSettings + Rooms;

    public SendInviteTests() => dir.Settings(Base);

    public void Dispose() => dir.Dispose();

    /// <summary>A Graph that answers the create with an id (or fails as told), reads the invitation back with its attendees
    /// as created (the room answering in turn) and a Teams link when online, serves one hold, and knows a few names.</summary>
    private sealed class Graph : IGraph
    {
        public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];
        public string[] RoomAnswers { get; init; } = ["accepted"];
        public Exception? CreateFails { get; set; }
        public JsonObject? Hold { get; init; }
        public Dictionary<string, string> Names { get; } = new() { ["dan@example.com"] = "Dan Clark", ["sam@example.com"] = "Sam Jones" };
        private int reads;

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) => [];

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add((method, url, body?.DeepClone()));
            if (method == "GET" && url.StartsWith("/users/", StringComparison.Ordinal))
            {
                var address = Uri.UnescapeDataString(url["/users/".Length..url.IndexOf('?')]);
                return Names.TryGetValue(address, out var name) ? new JsonObject { ["displayName"] = name }
                    : throw new GraphRequestException(HttpStatusCode.NotFound, method, url, "not found");
            }
            if (method == "POST" && url == "/me/events")
            {
                if (CreateFails is { } fail)
                    throw fail;
                return new JsonObject { ["id"] = "inv-1" };
            }
            if (method == "GET" && url.StartsWith("/me/events/inv-1", StringComparison.Ordinal))
            {
                var created = (JsonObject)Calls.Last(c => c.Method == "POST").Body!;
                var back = new JsonObject { ["id"] = "inv-1", ["webLink"] = "https://outlook.example/inv-1" };
                var attendees = new JsonArray();
                var answer = RoomAnswers[Math.Min(reads++, RoomAnswers.Length - 1)];
                foreach (var a in (JsonArray)created["attendees"]!)
                {
                    var stored = a!.DeepClone();
                    stored["status"] = new JsonObject { ["response"] = a.Req("type") == "resource" ? answer : "none" };
                    attendees.Add(stored);
                }
                back["attendees"] = attendees;
                if (created.Truthy("isOnlineMeeting"))
                    back["onlineMeeting"] = new JsonObject { ["joinUrl"] = "https://teams.example/join/1" };
                return back;
            }
            if (method == "GET" && url.StartsWith("/me/events/hold-1", StringComparison.Ordinal))
                return Hold is null ? throw new GraphRequestException(HttpStatusCode.NotFound, method, url, "") : (JsonObject)Hold.DeepClone();
            return [];
        }
    }

    private static JsonObject HoldEvent(string subject = "Hold: Planning", bool organiser = true, params (string Address, string Type)[] attendees) => new()
    {
        ["id"] = "hold-1", ["subject"] = subject, ["isOrganizer"] = organiser, ["type"] = "singleInstance",
        ["start"] = new JsonObject { ["dateTime"] = "2026-12-01T10:00:00.0000000" }, ["end"] = new JsonObject { ["dateTime"] = "2026-12-01T10:30:00.0000000" },
        ["attendees"] = new JsonArray(attendees.Select(a => (JsonNode)new JsonObject
        {
            ["emailAddress"] = new JsonObject { ["address"] = a.Address }, ["type"] = a.Type,
        }).ToArray()),
    };

    private (int Code, string Out) Run(Graph graph, params string[] args)
    {
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now)))
            code = Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), .. args],
                new CliHost { Out = output, Err = output, Echo = false, Graph = (_, _) => graph, Sleep = _ => sleeps++ });
        return (code, output.ToString());
    }

    private static readonly string[] Invite = ["send-invite", "2026-12-01T10:00", "10:30", "Planning", "--to", "dan@example.com,pat@partner.org"];

    private Settings Loaded => Settings.Load(Path.Combine(dir.Path, "config.toml"));

    [Fact]
    public void without_yes_it_shows_exactly_what_would_go_and_sends_nothing()
    {
        var graph = new Graph();
        var bodyFile = dir.Write("body.txt", "Hi both,\n\nAgenda:\n- the plan\n- <the risks> & costs\n\nThanks, Alex\n");
        var (code, output) = Run(graph, [.. Invite, "--optional", "sam@example.com", "--room", "mtg room 3.a.1", "--body-file", bodyFile]);
        Assert.Equal(0, code);
        Assert.Contains("Would send this invitation from your calendar", output);
        Assert.Contains("  Subject   Planning", output);
        Assert.Contains("  When      Tue 01 Dec 10:00-10:30", output);
        Assert.Contains("  To        Dan Clark <dan@example.com>", output);
        Assert.Contains("            pat@partner.org (external)", output);
        Assert.Contains("  Optional  Sam Jones <sam@example.com>", output);
        Assert.Contains("  Room      MTG Room 3.A.1 <MTGRoom3.A.1@example.com>", output);
        Assert.Contains("  Teams     yes", output);
        Assert.Contains("    | Hi both,\n    |\n    | Agenda:\n    | - the plan\n    | - <the risks> & costs\n    |\n    | Thanks, Alex", output.Replace("\r\n", "\n"));
        Assert.Contains("Nothing sent. Run again with --yes to send it.", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
        Assert.False(File.Exists(Loaded.SentInvitesFile));
    }

    [Fact]
    public void the_body_is_paragraphs_and_bullets_with_everything_escaped()
    {
        const string P = "<p style=\"margin:0\">";
        var html = InviteText.Html("\n\nHi <b>all</b>,\nsecond line\n\n\n- one & two\n- \"three\"\nafter\n\nBye\n\n");
        Assert.Equal("<div style=\"font-family: Aptos, Calibri, Arial, sans-serif; font-size: 11pt;\">"
                     + $"{P}Hi &lt;b&gt;all&lt;/b&gt;,</p>{P}second line</p>{P}&nbsp;</p>"
                     + "<ul style=\"margin-top:0;margin-bottom:0\"><li>one &amp; two</li><li>&quot;three&quot;</li></ul>"
                     + $"{P}after</p>{P}&nbsp;</p>{P}Bye</p></div>", html);
        Assert.Equal("", InviteText.Html("  \n\n"));
        Assert.Equal(["Hi", "", "Agenda:", "- a", "- b"], InviteText.Readable("Hi\r\n\r\n\r\nAgenda:\n- a\n  - b\n\n"));
        // No other markdown: a star or a heading stays as written.
        Assert.Contains($"{P}* not a bullet</p>{P}# not a heading</p>", InviteText.Html("* not a bullet\n# not a heading"));
    }

    [Fact]
    public void with_yes_it_creates_the_meeting_reads_it_back_and_records_it()
    {
        var graph = new Graph { RoomAnswers = ["none", "accepted"] };
        var (code, output) = Run(graph, [.. Invite, "--optional", "sam@example.com", "--room", "MTG Room 3.A.1", "--body", "Hello\n- a", "--yes"]);
        Assert.Equal(0, code);
        var create = Assert.Single(graph.Calls, c => c.Method == "POST");
        var body = (JsonObject)create.Body!;
        var attendees = ((JsonArray)body["attendees"]!).Select(a => (a!["emailAddress"]!.Req("address"), a.Req("type"))).ToList();
        Assert.Equal([("dan@example.com", "required"), ("pat@partner.org", "required"), ("sam@example.com", "optional"),
                      ("MTGRoom3.A.1@example.com", "resource")], attendees);
        Assert.Equal("Dan Clark", body["attendees"]![0]!["emailAddress"]!.Req("name"));
        Assert.Equal("html", body["body"]!.Req("contentType"));
        Assert.Contains("<p style=\"margin:0\">Hello</p><ul style=\"margin-top:0;margin-bottom:0\"><li>a</li></ul>", body["body"]!.Req("content"));
        Assert.True(body["isOnlineMeeting"]!.GetValue<bool>());
        Assert.Equal("teamsForBusiness", body.Req("onlineMeetingProvider"));
        Assert.True(body["responseRequested"]!.GetValue<bool>());
        Assert.Equal("busy", body.Req("showAs"));
        Assert.Equal("MTG Room 3.A.1", body["location"]!.Req("displayName"));
        Assert.Equal("2026-12-01T10:00:00", body["start"]!.Req("dateTime"));
        Assert.False(body.ContainsKey("reminderMinutesBeforeStart"));
        Assert.Contains("Sent: \"Planning\", Tue 01 Dec 10:00-10:30, to 3 people (1 optional), room MTG Room 3.A.1, Teams.", output);
        Assert.Contains("  id     inv-1", output);
        Assert.Contains("    Dan Clark <dan@example.com> (required)", output);
        Assert.Contains("    MTG Room 3.A.1 <MTGRoom3.A.1@example.com> (resource)", output);
        Assert.Contains("join link present: https://teams.example/join/1", output);
        Assert.Contains("room MTG Room 3.A.1 accepted: it's booked.", output);
        Assert.Equal(1, sleeps);
        var sent = Assert.Single(Triage.Deleted(Loaded));
        Assert.Equal("Invites sent", sent.Req("rule"));
        Assert.Contains("sent Tue 01/12 10:00-10:30 to 3 people (1 optional); room MTG Room 3.A.1 (accepted); Teams: yes (inv-1)", sent.Req("did"));
        var (_, listed) = Run(graph, "deleted");
        Assert.Contains("Invites sent (1)", listed);
    }

    [Fact]
    public void no_online_leaves_teams_off()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, [.. Invite, "--no-online", "--location", "The kitchen", "--yes"]);
        Assert.Equal(0, code);
        var body = (JsonObject)Assert.Single(graph.Calls, c => c.Method == "POST").Body!;
        Assert.False(body["isOnlineMeeting"]!.GetValue<bool>());
        Assert.False(body.ContainsKey("onlineMeetingProvider"));
        Assert.Equal("The kitchen", body["location"]!.Req("displayName"));
        Assert.Contains(", at The kitchen, no Teams.", output);
        Assert.Contains("Teams: no", Assert.Single(Triage.Deleted(Loaded)).Req("did"));
    }

    [Fact]
    public void the_same_invitation_is_never_sent_twice()
    {
        var graph = new Graph();
        Assert.Equal(0, Run(graph, [.. Invite, "--yes"]).Code);
        // People in another order and case are the same people.
        var (code, output) = Run(graph, "send-invite", "2026-12-01T10:00", "10:30", "Planning", "--to", "PAT@partner.org, dan@example.com", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("already sent", output);
        Assert.Contains("Not sent again.", output);
        var (previewCode, _) = Run(graph, Invite);
        Assert.Equal(2, previewCode);
        Assert.Single(graph.Calls, c => c.Method == "POST");
        // A different subject is a different invitation.
        Assert.Equal(0, Run(graph, "send-invite", "2026-12-01T10:00", "10:30", "Planning (2)", "--to", "dan@example.com", "--yes").Code);
        Assert.Equal(2, graph.Calls.Count(c => c.Method == "POST"));
    }

    public static TheoryData<Exception> LostAnswers => new()
    {
        new GraphRequestException(HttpStatusCode.ServiceUnavailable, "POST", "/me/events", "busy"),
        new TimeoutException("stalled"),
        new GraphDeliveryUnknownException("POST", "/me/events", new HttpRequestException("reset")),
    };

    [Theory]
    [MemberData(nameof(LostAnswers))]
    public void a_send_whose_answer_was_lost_is_recorded_as_may_have_gone_and_never_resent(Exception lost)
    {
        var graph = new Graph { CreateFails = lost, Hold = HoldEvent() };
        var (code, output) = Run(graph, [.. Invite, "--replace-hold", "hold-1", "--yes"]);
        Assert.Equal(1, code);
        Assert.Contains("may or may not have gone", output);
        Assert.Contains("The hold wasn't deleted.", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method == "DELETE");
        var state = StateFiles.LoadObject(Loaded.SentInvitesFile, () => []);
        Assert.Equal("sending", Assert.Single(state).Value.Str("state"));
        Assert.Contains("MAY HAVE GONE", Assert.Single(Triage.Deleted(Loaded)).Req("did"));

        graph.CreateFails = null;
        var (again, said) = Run(graph, [.. Invite, "--yes"]);
        Assert.Equal(2, again);
        Assert.Contains("may already have gone", said);
        Assert.Single(graph.Calls, c => c.Method == "POST");
    }

    [Fact]
    public void a_refusal_from_microsoft_sent_nothing_so_it_can_be_sent_again()
    {
        var graph = new Graph { CreateFails = new GraphRequestException(HttpStatusCode.BadRequest, "POST", "/me/events", "bad") };
        var (code, output) = Run(graph, [.. Invite, "--yes"]);
        Assert.Equal(1, code);
        Assert.Contains("nothing was sent", output);
        Assert.Empty(StateFiles.LoadObject(Loaded.SentInvitesFile, () => []));
        graph.CreateFails = null;
        Assert.Equal(0, Run(graph, [.. Invite, "--yes"]).Code);
        Assert.Equal(2, graph.Calls.Count(c => c.Method == "POST"));
    }

    [Fact]
    public void a_journal_run_records_nothing()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, ["--writes", "journal", "--journal", Path.Combine(dir.Path, "journal.txt"), .. Invite, "--yes"]);
        Assert.Equal(0, code);
        Assert.Contains("Journalled, not sent", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
        Assert.False(File.Exists(Loaded.SentInvitesFile));
    }

    [Theory]
    [InlineData("2026-12-01T10:00", "09:30", new string[0], "must be after the start")]
    [InlineData("2026-12-01T10:00", "2026-12-02T10:30", new string[0], "same day")]
    [InlineData("2026-11-20T10:00", "10:30", new string[0], "is in the past")]
    [InlineData("2026-12-01T10:00", "10:30", new[] { "--room", "dan@example.com" }, "isn't one of [hold] rooms")]
    [InlineData("2026-12-01T10:00", "10:30", new[] { "--optional", "dan@example.com" }, "in both --to and --optional")]
    [InlineData("2026-12-01T10:00", "10:30", new[] { "--optional", "alex@example.com" }, "is you")]
    [InlineData("2026-12-01T10:00", "10:30", new[] { "--optional", "MTGRoom3.A.1@example.com" }, "give it with --room")]
    [InlineData("2026-12-01T10:00", "10:30", new[] { "--optional", "not-an-address" }, "isn't an email address")]
    public void bad_invitations_are_refused_before_anything_is_sent(string start, string end, string[] extra, string message)
    {
        var graph = new Graph();
        var (code, output) = Run(graph, ["send-invite", start, end, "Planning", "--to", "dan@example.com", .. extra, "--yes"]);
        Assert.Equal(2, code);
        Assert.Contains(message, output);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Theory]
    [InlineData("")]
    [InlineData("--to| ")]
    [InlineData("--to|a@example.com|--room|r|--location|y")]
    [InlineData("--to|a@example.com|--body|a|--body-file|b")]
    [InlineData("--to|a@example.com|--online")]
    public void usage_mistakes_are_refused(string extra) =>
        Assert.Throws<UsageException>(() => Cli.Parse(["send-invite", "2026-12-01T10:00", "10:30", "x", .. extra.Split('|', StringSplitOptions.RemoveEmptyEntries)]));

    [Fact]
    public void the_writer_refuses_anyone_not_approved()
    {
        var people = new[] { new InvitePerson("dan@example.com", null, false, false) };
        var draft = new InviteDraft("x", now.AddDays(1), now.AddDays(1).AddHours(1), people, "", null, null, true);
        var body = SendInvites.Body(draft, "GMT Standard Time");
        var graph = new Graph();
        Assert.Throws<InvalidOperationException>(() => WriteDoor.Live(graph).Calendar.Invite(body, ["someone@example.com"], []));
        ((JsonArray)body["attendees"]!).Add((JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = "r@example.com" }, ["type"] = "resource" });
        Assert.Throws<InvalidOperationException>(() => WriteDoor.Live(graph).Calendar.Invite(body, ["dan@example.com"], []));
        var nobody = SendInvites.Body(draft with { People = [] }, "GMT Standard Time");
        Assert.Throws<InvalidOperationException>(() => WriteDoor.Live(graph).Calendar.Invite(nobody, [], []));
        Assert.Empty(graph.Calls);
    }

    /// <summary>A Graph whose ordinary Request (which M365Graph retries after a 5xx or a dropped connection) refuses
    /// writes, so only RequestOnce can create.</summary>
    private sealed class OnceGraph : IGraph
    {
        public List<string> Once { get; } = [];

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) => [];

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null) =>
            method == "GET" ? [] : throw new InvalidProgramException($"{method} {url} went through the retrying Request");

        public JsonObject RequestOnce(string url, string method, JsonNode? body, IReadOnlyList<string>? scopes = null)
        {
            Once.Add($"{method} {url}");
            return new JsonObject { ["id"] = "inv-1" };
        }
    }

    [Fact]
    public void the_invitation_goes_through_the_write_that_is_never_retried()
    {
        var people = new[] { new InvitePerson("dan@example.com", null, false, false) };
        var draft = new InviteDraft("x", now.AddDays(1), now.AddDays(1).AddHours(1), people, "", null, null, true);
        var graph = new OnceGraph();
        var created = new Calendar(graph, WriteDoor.Live(graph).Calendar).Invite(draft, []);
        Assert.Equal("inv-1", created!.Req("id"));
        Assert.Equal(["POST /me/events"], graph.Once);
        Assert.Throws<WriteRefusedException>(() => new ReadOnlyGraph(graph).RequestOnce("/me/events", "POST", new JsonObject()));
    }

    // --replace-hold: validated before anything is sent; deleted only after the invitation is confirmed created.

    public static TheoryData<JsonObject, string> BadHolds => new()
    {
        { HoldEvent(organiser: false), "it isn't yours" },
        { HoldEvent(subject: "Planning"), "doesn't start \"Hold:\"" },
        { HoldEvent("Hold: Planning", true, ("kim@example.com", "required")), "it has people on it (kim@example.com)" },
    };

    [Theory]
    [MemberData(nameof(BadHolds))]
    public void a_hold_that_isnt_a_plain_hold_of_yours_is_refused_before_sending(JsonObject hold, string message)
    {
        var graph = new Graph { Hold = hold };
        var (code, output) = Run(graph, [.. Invite, "--replace-hold", "hold-1", "--yes"]);
        Assert.Equal(2, code);
        Assert.Contains(message, output);
        Assert.Contains("Nothing sent.", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void an_unknown_hold_is_refused_before_sending()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, [.. Invite, "--replace-hold", "hold-1", "--yes"]);
        Assert.Equal(2, code);
        Assert.Contains("no event with that id", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void a_valid_hold_is_deleted_after_the_invitation_is_created()
    {
        var graph = new Graph { Hold = HoldEvent("Hold: Planning", true, ("MTGRoom2.D.2@example.com", "resource")) };
        var (preview, shown) = Run(graph, [.. Invite, "--replace-hold", "hold-1"]);
        Assert.Equal(0, preview);
        Assert.Contains("Replaces  your hold \"Hold: Planning\", Tue 01 Dec 10:00-10:30: deleted once the invitation is created", shown);
        Assert.Contains("The hold's room (MTG Room 2.D.2) is freed", shown);

        var (code, output) = Run(graph, [.. Invite, "--replace-hold", "hold-1", "--yes"]);
        Assert.Equal(0, code);
        var writes = graph.Calls.Where(c => c.Method != "GET").Select(c => $"{c.Method} {c.Url}").ToList();
        Assert.Equal(["POST /me/events", "DELETE /me/events/hold-1"], writes);
        Assert.Contains("hold \"Hold: Planning\" deleted, which frees the room it had booked.", output);
        Assert.Contains("replaced hold \"Hold: Planning\"", Assert.Single(Triage.Deleted(Loaded)).Req("did"));
    }

    [Fact]
    public void a_hold_on_the_same_room_is_warned_of_and_a_decline_is_handed_back_to_outlook()
    {
        var graph = new Graph { Hold = HoldEvent("Hold: Planning", true, ("MTGRoom3.A.1@example.com", "resource")), RoomAnswers = ["declined"] };
        var (_, shown) = Run(graph, [.. Invite, "--room", "MTG Room 3.A.1", "--replace-hold", "hold-1"]);
        Assert.Contains("The hold has MTG Room 3.A.1 booked, so the room may decline this invitation as double-booked", shown);

        var (code, output) = Run(graph, [.. Invite, "--room", "MTG Room 3.A.1", "--replace-hold", "hold-1", "--yes"]);
        Assert.Equal(0, code);
        Assert.Contains("room MTG Room 3.A.1 DECLINED: your hold still had it booked.", output);
        Assert.Contains("open the invitation in Outlook and add MTG Room 3.A.1 again", output);
        Assert.Single(graph.Calls, c => c.Method == "POST");
        Assert.Single(graph.Calls, c => c.Method == "DELETE");
    }
}
