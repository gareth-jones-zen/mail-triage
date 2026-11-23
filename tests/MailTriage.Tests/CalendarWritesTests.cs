using System.Net;
using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>`mail-triage respond` and `cancel` (1.22.0): the calendar writes a session needs, so the connector's
/// calendar tools can be denied. Preview without --yes; one write with --yes; one occurrence unless --series.</summary>
public sealed class CalendarWritesTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);   // Mon 23 Nov 2026

    public CalendarWritesTests() => dir.Settings(Make.InviteSettings);

    public void Dispose() => dir.Dispose();

    private static JsonObject Attendee(string name, string address) =>
        new() { ["type"] = "required", ["emailAddress"] = new JsonObject { ["name"] = name, ["address"] = address } };

    private static JsonObject Event(string id, string subject, bool organiser, string response, string type = "occurrence",
        string? master = "series-1", params JsonObject[] attendees) => new()
    {
        ["id"] = id, ["subject"] = subject, ["isOrganizer"] = organiser, ["type"] = type, ["seriesMasterId"] = master,
        ["responseStatus"] = new JsonObject { ["response"] = response },
        ["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = organiser ? "Alex" : "Sam Smith", ["address"] = organiser ? "alex@example.com" : "sam@example.com" } },
        ["start"] = new JsonObject { ["dateTime"] = "2026-11-25T15:00:00.0000000" },
        ["end"] = new JsonObject { ["dateTime"] = "2026-11-25T15:30:00.0000000" },
        ["attendees"] = new JsonArray(attendees.Select(a => (JsonNode)a).ToArray()),
    };

    /// <summary>A Graph holding some events; records every call; can fail a write after sending it.</summary>
    private sealed class Graph(params JsonObject[] events) : IGraph
    {
        private readonly Dictionary<string, JsonObject> byId = events.ToDictionary(e => e.Req("id"));

        public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];

        public bool FailWrites { get; init; }

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) => [];

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add((method, url, body?.DeepClone()));
            var id = url.Split('/')[3].Split('?')[0];
            if (method == "GET")
                return byId.TryGetValue(id, out var e) ? (JsonObject)e.DeepClone()
                    : throw new M365Graph.GraphRequestException(HttpStatusCode.NotFound, method, url, "not found");
            if (FailWrites)
                throw new HttpRequestException("connection reset after sending");
            if (method == "DELETE")
                byId.Remove(id);
            return [];
        }

        public IEnumerable<(string Method, string Url, JsonNode? Body)> Writes => Calls.Where(c => c.Method != "GET");
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

    private List<JsonObject> Recorded() => Triage.Deleted(Settings.Load(Path.Combine(dir.Path, "config.toml")));

    // ---- respond ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("accepted", "said accepted")]
    [InlineData("tentativelyAccepted", "said tentative")]
    [InlineData("notResponded", "not answered yet")]
    public void respond_previews_whatever_was_said_before_and_writes_nothing(string response, string said)
    {
        var graph = new Graph(Event("occ-1", "Architecture Catch-Up", false, response));
        var (code, output) = Run(graph, "respond", "occ-1", "tentative", "--note", "I'll join after 14:00");
        Assert.Equal(0, code);
        Assert.Contains("Would answer \"Architecture Catch-Up\", Wed 25 Nov 15:00-15:30 (this occurrence only), organised by Sam Smith", output);
        Assert.Contains(said, output);
        Assert.Contains("Would send Sam Smith a response: tentative, with the note \"I'll join after 14:00\".", output);
        Assert.Contains("Nothing sent.", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void respond_with_yes_answers_the_occurrence_once_and_records_it()
    {
        var graph = new Graph(Event("occ-1", "Architecture Catch-Up", false, "accepted"));
        var (code, output) = Run(graph, "respond", "occ-1", "decline", "--note", "On leave", "--yes");
        Assert.Equal(0, code);
        var write = Assert.Single(graph.Writes);
        Assert.Equal("POST", write.Method);
        Assert.Equal("/me/events/occ-1/decline", write.Url);
        Assert.True(write.Body!["sendResponse"]!.GetValue<bool>());
        Assert.Equal("On leave", write.Body!.Req("comment"));
        Assert.Contains("Answered: decline", output);
        var entry = Assert.Single(Recorded());
        Assert.Equal("Answered by hand", entry.Req("rule"));
        Assert.Contains("decline Wed 25/11 15:00 (this occurrence only): \"On leave\"", entry.Req("did"));
    }

    [Fact]
    public void respond_with_series_answers_the_series_master()
    {
        var graph = new Graph(Event("occ-1", "Weekly sync", false, "accepted"));
        var (code, _) = Run(graph, "respond", "occ-1", "tentative", "--series", "--yes");
        Assert.Equal(0, code);
        Assert.Equal("/me/events/series-1/tentativelyAccept", Assert.Single(graph.Writes).Url);
    }

    [Fact]
    public void a_series_id_without_series_is_refused()
    {
        var graph = new Graph(Event("series-1", "Weekly sync", false, "accepted", type: "seriesMaster", master: null));
        var (code, output) = Run(graph, "respond", "series-1", "decline", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("That id is a whole series", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void respond_refuses_your_own_meeting()
    {
        var graph = new Graph(Event("mine-1", "GJ and SW catchup", true, "organizer", attendees: Attendee("Sarah", "sarah@example.com")));
        var (code, output) = Run(graph, "respond", "mine-1", "decline", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("is your own meeting", output);
        Assert.Contains("mail-triage cancel", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void an_invalid_action_is_a_usage_error() =>
        Assert.Throws<UsageException>(() => Cli.Parse(["respond", "occ-1", "maybe"]));

    // ---- cancel -------------------------------------------------------------------------------------------------

    [Fact]
    public void cancel_previews_a_meeting_with_its_attendees_and_writes_nothing()
    {
        var graph = new Graph(Event("mine-1", "GJ and SW catchup", true, "organizer", "singleInstance", null,
            Attendee("Sarah Wood", "sarah@example.com"), Attendee("Dan Clark", "dan@example.com")));
        var (code, output) = Run(graph, "cancel", "mine-1", "--note", "Moved to Thursday");
        Assert.Equal(0, code);
        Assert.Contains("Would cancel your meeting \"GJ and SW catchup\", Wed 25 Nov 15:00-15:30 (this meeting).", output);
        Assert.Contains("Each of these 2 people gets a cancellation, with the note \"Moved to Thursday\":", output);
        Assert.Contains("Sarah Wood <sarah@example.com>", output);
        Assert.Contains("Dan Clark <dan@example.com>", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void cancel_with_yes_cancels_a_meeting_with_the_note_and_records_it()
    {
        var graph = new Graph(Event("occ-2", "Team meeting", true, "organizer", attendees: Attendee("Sarah Wood", "sarah@example.com")));
        var (code, output) = Run(graph, "cancel", "occ-2", "--note", "Bank holiday", "--yes");
        Assert.Equal(0, code);
        var write = Assert.Single(graph.Writes);
        Assert.Equal(("POST", "/me/events/occ-2/cancel"), (write.Method, write.Url));
        Assert.Equal("Bank holiday", write.Body!.Req("comment"));
        Assert.Contains("Cancelled: \"Team meeting\"", output);
        Assert.Contains("1 attendee gets a cancellation", output);
        var entry = Assert.Single(Recorded());
        Assert.Equal("Cancelled by hand", entry.Req("rule"));
        Assert.Contains("1 told: \"Bank holiday\"", entry.Req("did"));
    }

    [Fact]
    public void cancel_of_a_block_deletes_it_and_tells_no_one()
    {
        var graph = new Graph(Event("blk-1", "JL pre-read", true, "organizer", "singleInstance", null));
        var (preview, shown) = Run(graph, "cancel", "blk-1");
        Assert.Equal(0, preview);
        Assert.Contains("Would delete your block \"JL pre-read\"", shown);
        Assert.Contains("no one is told", shown);
        var (code, output) = Run(graph, "cancel", "blk-1", "--yes");
        Assert.Equal(0, code);
        Assert.Equal(("DELETE", "/me/events/blk-1"), (Assert.Single(graph.Writes).Method, Assert.Single(graph.Writes).Url));
        Assert.Contains("Deleted: \"JL pre-read\"", output);
        Assert.Equal("Deleted by hand", Assert.Single(Recorded()).Req("rule"));
    }

    [Fact]
    public void a_block_whose_only_attendee_is_you_is_still_a_block()
    {
        var graph = new Graph(Event("blk-2", "Focus", true, "organizer", "singleInstance", null, Attendee("Alex", "alex@example.com")));
        var (_, shown) = Run(graph, "cancel", "blk-2");
        Assert.Contains("Would delete your block", shown);
    }

    [Fact]
    public void cancel_with_series_cancels_the_series()
    {
        var graph = new Graph(Event("occ-3", "Weekly 1-2-1 - Ben", true, "organizer", attendees: Attendee("Ben", "ben@example.com")));
        var (code, _) = Run(graph, "cancel", "occ-3", "--series", "--yes");
        Assert.Equal(0, code);
        Assert.Equal("/me/events/series-1/cancel", Assert.Single(graph.Writes).Url);
    }

    [Fact]
    public void cancel_refuses_someone_elses_meeting()
    {
        var graph = new Graph(Event("occ-4", "Architecture Catch-Up", false, "accepted"));
        var (code, output) = Run(graph, "cancel", "occ-4", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("is someone else's meeting", output);
        Assert.Contains("respond <event id> decline", output);
        Assert.Empty(graph.Writes);
    }

    // ---- shared -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("respond", "occ-1", "accept")]
    [InlineData("cancel", "mine-1", null)]
    public void a_write_that_may_have_reached_microsoft_is_not_retried(string command, string id, string? action)
    {
        var graph = new Graph(Event("occ-1", "Their meeting", false, "accepted"),
            Event("mine-1", "My meeting", true, "organizer", "singleInstance", null, Attendee("Sam", "sam@example.com")))
        { FailWrites = true };
        var args = action is null ? new[] { command, id, "--yes" } : [command, id, action, "--yes"];
        var (code, output) = Run(graph, args);
        Assert.Equal(1, code);
        Assert.Contains("may or may not have gone through", output);
        Assert.Contains("it isn't retried", output);
        Assert.Single(graph.Writes);
        Assert.Empty(Recorded());
    }

    [Fact]
    public void an_unknown_event_id_is_refused()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, "cancel", "nope", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("No event with that id", output);
        Assert.Empty(graph.Writes);
    }
}
