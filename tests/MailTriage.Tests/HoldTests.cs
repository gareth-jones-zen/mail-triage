using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>`mail-triage hold` (1.18.0): a hold in your own calendar for the find-a-slot skill, never with attendees.</summary>
public sealed class HoldTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);   // Mon 23 Nov 2026

    public HoldTests() => dir.Settings(Make.InviteSettings);

    public void Dispose() => dir.Dispose();

    /// <summary>A Graph that answers the create with an id, and the read-back with a webLink.</summary>
    private sealed class Graph : IGraph
    {
        public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) => [];

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add((method, url, body?.DeepClone()));
            if (method == "POST" && url == "/me/events")
                return new JsonObject { ["id"] = "hold-1" };
            if (method == "GET" && url.StartsWith("/me/events/hold-1", StringComparison.Ordinal))
                return new JsonObject { ["id"] = "hold-1", ["webLink"] = "https://outlook.example/hold-1" };
            return [];
        }
    }

    private (int Code, string Out) Run(Graph graph, params string[] args)
    {
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now)))
            code = Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), .. args],
                new CliHost { Out = output, Err = output, Echo = false, Graph = (_, _) => graph });
        return (code, output.ToString());
    }

    [Fact]
    public void without_yes_it_shows_the_hold_and_writes_nothing()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS in FibreHub", "--body", "Paul, Jake, Dan");
        Assert.Equal(0, code);
        Assert.Contains("Would create in your calendar: \"Hold: ITS in FibreHub\", Wed 25 Nov 16:15-17:00, busy, no attendees.", output);
        Assert.Contains("Nothing written.", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void with_yes_it_creates_an_event_with_no_attendees_and_records_it()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS in FibreHub", "--online", "--location", "Teams", "--yes");
        Assert.Equal(0, code);
        var create = Assert.Single(graph.Calls, c => c.Method == "POST");
        Assert.Equal("/me/events", create.Url);
        var body = (JsonObject)create.Body!;
        Assert.Empty((JsonArray)body["attendees"]!);
        Assert.Equal("busy", body.Req("showAs"));
        Assert.Equal("normal", body.Req("sensitivity"));
        Assert.Equal("2026-11-25T16:15:00", body["start"]!.Req("dateTime"));
        Assert.Equal("2026-11-25T17:00:00", body["end"]!.Req("dateTime"));
        Assert.True(body["isOnlineMeeting"]!.GetValue<bool>());
        Assert.Contains("Held: \"Hold: ITS in FibreHub\"", output);
        Assert.Contains("https://outlook.example/hold-1", output);
        var settings = Settings.Load(Path.Combine(dir.Path, "config.toml"));
        var held = Assert.Single(Triage.Deleted(settings));
        Assert.Equal("Holds", held.Req("rule"));
        Assert.Contains("held Wed 25/11 16:15-17:00, no attendees", held.Req("did"));
        var (_, listed) = Run(graph, "deleted");
        Assert.Contains("Holds (1)", listed);
    }

    [Fact]
    public void a_hold_with_attendees_is_refused_by_the_writer()
    {
        var body = Holds.Body("x", now.AddDays(1), now.AddDays(1).AddHours(1), "GMT Standard Time");
        ((JsonArray)body["attendees"]!).Add((JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = "a@example.com" } });
        var graph = new Graph();
        Assert.Throws<InvalidOperationException>(() => WriteDoor.Live(graph).Calendar.Hold(body));
        Assert.Empty(graph.Calls);
    }

    [Fact]
    public void the_command_line_has_no_way_to_add_attendees() =>
        Assert.Throws<UsageException>(() => Cli.Parse(["hold", "2026-11-25T16:15", "17:00", "x", "--attendee", "a@example.com"]));

    [Theory]
    [InlineData("2026-11-20T10:00", "11:00", "is in the past")]
    [InlineData("2026-11-25T16:15", "16:00", "must be after the start")]
    public void past_starts_and_ends_before_starts_are_refused(string start, string end, string message)
    {
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", start, end, "x", "--yes");
        Assert.Equal(2, code);
        Assert.Contains(message, output);
        Assert.DoesNotContain(graph.Calls, c => c.Method == "POST");
    }

    [Fact]
    public void body_and_body_file_together_are_refused() =>
        Assert.Throws<UsageException>(() => Cli.Parse(["hold", "2026-11-25T16:15", "17:00", "x", "--body", "a", "--body-file", "b.txt"]));
}
