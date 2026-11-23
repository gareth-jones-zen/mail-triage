using System.Net;
using System.Text.Json.Nodes;
using PyCompat;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

/// <summary>1.29.0: `leave --decline` previews without --yes, like `invite` and `move` (1.28.0): each meeting, its organiser,
/// that only the occurrence is declined, the note and whether the organiser is told, then "Nothing sent". With --decline
/// --yes it declines each once, as before. `leave` alone is still a listing; --yes without --decline is refused. A run's
/// own days-off declines are unchanged. "Now" is Mon 23 Nov 2026, 09:00; the leave is Wed 16 to Fri 18 Dec.</summary>
public sealed class LeaveDeclineTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);

    public LeaveDeclineTests() => dir.Settings(InviteSettings);

    public void Dispose() => dir.Dispose();

    /// <summary>A Graph holding some events: all of them for any calendarView. Records every call; a write can be made to
    /// lose its answer after it was sent.</summary>
    private sealed class Graph(params JsonObject[] events) : IGraph
    {
        public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];

        public bool LoseWrites { get; init; }

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add(("GET", url, null));
            return url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? events.Select(e => (JsonObject)e.DeepClone()).ToList() : [];
        }

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add((method, url, body?.DeepClone()));
            if (method == "GET")
                throw new M365Graph.GraphRequestException(HttpStatusCode.NotFound, method, url, "not found");
            if (LoseWrites)
                throw new M365Graph.GraphDeliveryUnknownException(method, url, HttpStatusCode.BadGateway, "lost");
            return [];
        }

        public List<(string Method, string Url, JsonNode? Body)> Writes => Calls.Where(c => c.Method != "GET").ToList();
    }

    private static Graph InLeave(bool loseWrites = false) => new(
        Self("leave", "Holiday", "2026-12-16", "00:00", "23:59", endDay: "2026-12-18", showAs: "oof"),
        Ev("occ-1", "Leads sync", "2026-12-16", "09:00", "09:30", response: "accepted", type: "occurrence", seriesMasterId: "series-1"),
        Ev("one-off", "Architecture forum", "2026-12-17", "10:00", "11:00", organiser: "Jordan Lee", orgAddr: "jordan@example.com"),
        Ev("unasked", "Town hall", "2026-12-18", "14:00", "15:00", response: "tentativelyAccepted", responseRequested: false),
        Ev("before", "Steering group", "2026-12-15", "10:00", "11:00", response: "accepted"))   // not in the leave
    {
        LoseWrites = loseWrites,
    };

    private (int Code, string Out) Run(Graph graph, params string[] args) => Run(Path.Combine(dir.Path, "config.toml"), graph, args);

    private (int Code, string Out) Run(string config, Graph graph, params string[] args)
    {
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now)))
            code = Cli.Run(["--config", config, .. args], new CliHost { Out = output, Err = output, Echo = true, Graph = (_, _) => graph });
        return (code, output.ToString());
    }

    [Fact]
    public void decline_previews_each_meeting_and_writes_nothing_even_with_writes_live()
    {
        var graph = InLeave();
        var (code, output) = Run(graph, "--writes", "live", "leave", "--decline");
        Assert.True(code == 0, output);
        Assert.Contains("Would decline 3 meetings in your leave over the next 60 days, each occurrence on its own, never the series:", output);
        Assert.Contains("  1. \"Leads sync\", organised by Sam Jones <sam@example.com>", output);
        Assert.Contains("     Wed 16 Dec 09:00–09:30; your answer so far: accepted", output);
        Assert.Contains("     Would decline this occurrence only, not the series.", output);
        Assert.Contains("     Sam Jones gets a response, with the note \"On annual leave 16/12 to 18/12\".", output);
        Assert.Contains("  2. \"Architecture forum\", organised by Jordan Lee <jordan@example.com>", output);
        Assert.Contains("     Thu 17 Dec 10:00–11:00; your answer so far: not answered yet", output);
        Assert.Contains("     Would decline it.", output);
        Assert.Contains("     Jordan Lee gets a response, with the note \"On annual leave 16/12 to 18/12\".", output);
        Assert.Contains("  3. \"Town hall\", organised by Sam Jones <sam@example.com>", output);
        Assert.Contains("     No response is sent (the organiser didn't ask for one): Sam Jones isn't told, and the note isn't sent.", output);
        Assert.Contains("Nothing sent. Run again with --decline --yes to decline them.", output);
        Assert.DoesNotContain("Steering group", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void decline_preview_shows_a_given_note_and_a_single_meeting_as_it()
    {
        var graph = new Graph(Self("leave", "Holiday", "2026-12-16", "00:00", "23:59", showAs: "oof"),
            Ev("one-off", "Architecture forum", "2026-12-16", "10:00", "11:00"));
        var (_, output) = Run(graph, "leave", "--decline", "--note", "Away that week, sorry");
        Assert.Contains("Would decline 1 meeting in your leave", output);
        Assert.Contains("Sam Jones gets a response, with the note \"Away that week, sorry\".", output);
        Assert.Contains("Nothing sent. Run again with --decline --yes to decline it.", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void decline_with_yes_declines_each_occurrence_once_with_its_note()
    {
        var graph = InLeave();
        var (code, output) = Run(graph, "leave", "--decline", "--yes");
        Assert.True(code == 0, output);
        Assert.Equal(["POST /me/events/occ-1/decline", "POST /me/events/one-off/decline", "POST /me/events/unasked/decline"],
            graph.Writes.Select(w => $"{w.Method} {w.Url}"));
        var first = graph.Writes[0].Body!;
        Assert.Equal("On annual leave 16/12 to 18/12", first.Req("comment"));
        Assert.True(first["sendResponse"]!.GetValue<bool>());
        Assert.False(graph.Writes[2].Body!["sendResponse"]!.GetValue<bool>());       // the organiser didn't ask for one
        Assert.Contains("3 meeting(s) in your leave: declined, each occurrence on its own.", output);
        Assert.DoesNotContain("Nothing sent", output);
    }

    [Fact]
    public void decline_with_yes_never_resends_a_lost_answer_and_carries_on()
    {
        var graph = InLeave(loseWrites: true);
        var (_, output) = Run(graph, "leave", "--decline", "--yes");
        Assert.Equal(["occ-1", "one-off", "unasked"], graph.Writes.Select(w => w.Url.Split('/')[3]));   // each once, none retried
        Assert.Contains("Microsoft didn't confirm this decline, so it may have gone: check Outlook before declining it again.", output);
        Assert.Contains("3 decline(s) may or may not have gone (above): never sent twice.", output);
    }

    [Fact]
    public void yes_without_decline_is_refused_and_sends_nothing()
    {
        var graph = InLeave();
        var (code, output) = Run(graph, "leave", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("--yes goes with --decline", output);
        Assert.Empty(graph.Calls);
    }

    [Fact]
    public void leave_alone_is_still_a_plain_listing()
    {
        var graph = InLeave();
        var (code, output) = Run(graph, "--writes", "live", "leave");
        Assert.Equal(0, code);
        Assert.Contains("  Wed 16 Dec 09:00–09:30  Leads sync  (Sam Jones; accepted)  note: \"On annual leave 16/12 to 18/12\"", output);
        Assert.Contains("  Fri 18 Dec 14:00–15:00  Town hall  (Sam Jones; tentativelyAccepted)  (no response requested)", output);
        Assert.Contains("3 meeting(s) in your leave. `--decline` shows what declining them would send", output);
        Assert.DoesNotContain("Would decline", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void nothing_in_your_leave_says_so_with_or_without_yes()
    {
        var graph = new Graph(Ev("before", "Steering group", "2026-12-15", "10:00", "11:00", response: "accepted"));
        Assert.Contains("Nothing in your leave over the next 60 days that you haven't declined.", Run(graph, "leave", "--decline").Out);
        Assert.Contains("Nothing in your leave over the next 60 days that you haven't declined.", Run(graph, "leave", "--decline", "--yes").Out);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void a_run_still_declines_meetings_on_your_days_off_without_yes()
    {
        var path = dir.Write("auto.toml", InviteSettings.Replace("[invites]\n", "[invites]\ndecline_days_off = true\n"));
        var graph = InLeave();
        var (code, output) = Run(path, graph, "run");
        Assert.True(code == 0, output);
        var writes = graph.Writes.Select(w => $"{w.Method} {w.Url}").ToList();
        Assert.Contains("POST /me/events/occ-1/decline", writes);                    // accepted, in the leave
        Assert.Contains("POST /me/events/unasked/decline", writes);                  // tentative, in the leave
        Assert.DoesNotContain("POST /me/events/before/decline", writes);
    }
}
