using System.Net;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

/// <summary>1.28.0: `move` and `invite` preview without --yes, like `respond` and `cancel`: what would happen, who is told,
/// and "Nothing sent". With --yes they write once. A preview writes nothing even with --writes live, and `invite`'s
/// preview takes no lock. (`forward`'s preview is in ForwardTests.) "Now" is Mon 23 Nov 2026, 09:00.</summary>
public sealed class ByHandPreviewTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);
    private readonly Settings config;

    public ByHandPreviewTests() => config = dir.Settings(InviteSettings);

    public void Dispose() => dir.Dispose();

    private static readonly (string, string, string)[] WithSarah = [("Alex Smith", Me, "required"), ("Sarah Wood", "sarah@example.com", "required")];
    private static readonly (string, string, string)[] WithJordan = [("Alex Smith", Me, "required"), ("Jordan Lee", "jordan@example.com", "required")];

    /// <summary>A Graph holding some events: each by id, and all of them for any calendarView. Records every call; a
    /// write can be made to lose its answer after it was sent.</summary>
    private sealed class Graph(params JsonObject[] events) : IGraph
    {
        private readonly Dictionary<string, JsonObject> byId = events.ToDictionary(e => e.Req("id"));

        public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];

        public bool LoseWrites { get; init; }

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add(("GET", url, null));
            return url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? byId.Values.Select(e => (JsonObject)e.DeepClone()).ToList() : [];
        }

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add((method, url, body?.DeepClone()));
            if (method == "GET")
            {
                var id = url.StartsWith("/me/events/", StringComparison.Ordinal) ? url["/me/events/".Length..].Split('?')[0] : "";
                return byId.TryGetValue(id, out var e) ? (JsonObject)e.DeepClone()
                    : throw new M365Graph.GraphRequestException(HttpStatusCode.NotFound, method, url, "not found");
            }
            if (LoseWrites)
                throw new M365Graph.GraphDeliveryUnknownException(method, url, HttpStatusCode.BadGateway, "lost");
            return [];
        }

        public List<(string Method, string Url, JsonNode? Body)> Writes => Calls.Where(c => c.Method != "GET").ToList();
    }

    private (int Code, string Out) Run(Graph graph, params string[] args)
    {
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now)))
            code = Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), .. args],
                new CliHost { Out = output, Err = output, Echo = true, Graph = (_, _) => graph });
        return (code, output.ToString());
    }

    // ---- move ---------------------------------------------------------------------------------------------------

    private static JsonObject Meeting() =>
        Ev("mine-1", "GJ and SW catchup", "2026-11-25", "15:00", "15:30", organiser: "Alex Smith", orgAddr: Me, attendees: WithSarah,
            type: "occurrence", seriesMasterId: "series-9");

    [Fact]
    public void move_previews_the_move_who_is_told_and_a_clash_and_writes_nothing_even_with_writes_live()
    {
        var graph = new Graph(Meeting(), Ev("other", "Steering group", "2026-11-26", "10:15", "11:00", response: "accepted"));
        var (code, output) = Run(graph, "--writes", "live", "move", "mine-1", "2026-11-26T10:00");
        Assert.Equal(0, code);
        Assert.Contains("Would move your meeting \"GJ and SW catchup\" (this occurrence only): Wed 25 Nov 15:00–15:30 -> Thu 26 Nov 10:00–10:30.", output);
        Assert.Contains("This person gets an update:", output);
        Assert.Contains("    Sarah Wood <sarah@example.com>", output);
        Assert.Contains("Warning: it would clash with 1 thing in your calendar:", output);
        Assert.Contains("\"Steering group\", Thu 26 Nov 10:15–11:00 (busy, accepted)", output);
        Assert.Contains("Nothing sent. Run again with --yes to move it.", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void move_preview_of_a_block_tells_no_one_and_a_declined_or_free_entry_is_no_clash()
    {
        var graph = new Graph(Self("blk-1", "Focus time", "2026-11-25", "14:00", "15:00"),
            Ev("declined", "Webinar", "2026-11-25", "16:00", "17:00", response: "declined"),
            Ev("marker", "Pay day", "2026-11-25", "00:00", "23:59", isAllDay: true, showAs: "free", response: "accepted"));
        var (code, output) = Run(graph, "move", "blk-1", "2026-11-25T16:00", "17:00");
        Assert.Equal(0, code);
        Assert.Contains("Would move your block \"Focus time\" (this meeting): Wed 25 Nov 14:00–15:00 -> Wed 25 Nov 16:00–17:00.", output);
        Assert.Contains("No one else is invited, so no one is told.", output);
        Assert.Contains("Nothing else in your calendar then.", output);
        Assert.Contains("Nothing changed. Run again with --yes to move it.", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void move_preview_counts_your_leave_as_a_clash()
    {
        var graph = new Graph(Meeting(), Self("leave", "Holiday", "2026-11-27", "00:00", "00:00", endDay: "2026-11-28", isAllDay: true, showAs: "oof"));
        var (_, output) = Run(graph, "move", "mine-1", "2026-11-27T10:00");
        Assert.Contains("\"Holiday\", Fri 27 Nov, all day (oof, yours)", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void move_with_yes_moves_once()
    {
        var graph = new Graph(Meeting());
        var (code, output) = Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        Assert.Equal(0, code);
        var write = Assert.Single(graph.Writes);
        Assert.Equal(("PATCH", "/me/events/mine-1"), (write.Method, write.Url));
        Assert.Equal("2026-11-26T10:00:00", write.Body!["start"]!.Req("dateTime"));
        Assert.Equal("2026-11-26T10:30:00", write.Body!["end"]!.Req("dateTime"));
        Assert.Contains("moved  GJ and SW catchup", output);
        Assert.Contains("(attendees get an update)", output);
    }

    [Fact]
    public void move_with_yes_never_resends_a_lost_move()
    {
        var graph = new Graph(Meeting()) { LoseWrites = true };
        var (code, output) = Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        Assert.Equal(1, code);
        Assert.Single(graph.Writes);
        Assert.Contains("It wasn't sent twice.", output);
    }

    [Fact]
    public void move_with_yes_in_journal_mode_sends_nothing_and_says_so()
    {
        var graph = new Graph(Meeting());
        var (code, output) = Run(graph, "--writes", "journal", "--journal", Path.Combine(dir.Path, "journal.txt"), "move", "mine-1", "2026-11-26T10:00", "--yes");
        Assert.Equal(0, code);
        Assert.Empty(graph.Writes);
        Assert.Contains("Journalled, not sent: move \"GJ and SW catchup\"", output);
    }

    [Fact]
    public void move_refuses_someone_elses_meeting_an_unknown_id_and_a_bad_time_before_any_preview()
    {
        var graph = new Graph(Ev("theirs", "Leadership weekly", "2026-11-25", "11:00", "12:00", response: "accepted"), Meeting());
        Assert.Equal(2, Run(graph, "move", "theirs", "2026-11-26T10:00").Code);
        Assert.Equal(2, Run(graph, "move", "nope", "2026-11-26T10:00").Code);
        var (code, output) = Run(graph, "move", "mine-1", "Thursday");
        Assert.Equal(2, code);
        Assert.Contains("Start is YYYY-MM-DDTHH:MM", output);
        Assert.Empty(graph.Writes);
    }

    // ---- invite -------------------------------------------------------------------------------------------------

    private static JsonObject Invite(string subject, string? series, string occurrence, string[] decline, string recommendation,
        string note = "", JsonArray? clashes = null, bool responseRequested = true, string[]? exceptions = null) => new()
    {
        ["folder"] = "Calendar", ["type"] = "invite", ["id"] = series ?? occurrence, ["key"] = $"invite:{series ?? occurrence}",
        ["series"] = series, ["occurrence"] = occurrence, ["occurrences"] = J.Array([occurrence]), ["exceptions"] = J.Array(exceptions ?? []),
        ["responseRequested"] = responseRequested, ["received"] = "2026-11-24T09:00", ["from"] = "Sam Jones", ["subject"] = subject,
        ["rule"] = "invite", ["check"] = recommendation.ToUpperInvariant(), ["recommendation"] = recommendation,
        ["decline"] = J.Array(decline), ["note"] = note, ["clashes"] = clashes ?? new JsonArray(),
    };

    private static JsonObject Clash(string id, string subject, string start, string end, string to, string toEnd, string kind) => new()
    {
        ["id"] = id, ["subject"] = subject, ["start"] = start, ["end"] = end, ["kind"] = kind, ["to"] = to, ["to_end"] = toEnd,
    };

    private static JsonObject LeadsSync() =>
        Invite("Leads sync: Tue 24 Nov 09:00–09:30", "series-1", "s1", ["s1"], "accept, and decline the clashing ones");

    private static JsonObject DesignReview() =>
        Invite("Design review: Thu 26 Nov 14:30–15:30", null, "i-1", [], "accept and move", clashes: new JsonArray(
            Clash("blk-1", "Focus time", "2026-11-26T14:00", "2026-11-26T15:00", "2026-11-26T13:00", "2026-11-26T14:00", "your block"),
            Clash("mine-2", "Weekly 1-2-1 - Jordan", "2026-11-26T15:00", "2026-11-26T15:30", "2026-11-26T16:00", "2026-11-26T16:30", "your meeting")));

    private static Graph InviteGraph() => new(
        Ev("s1", "Leads sync", "2026-11-24", "09:00", "09:30", type: "occurrence", seriesMasterId: "series-1"),
        Ev("i-1", "Design review", "2026-11-26", "14:30", "15:30"),
        Self("blk-1", "Focus time", "2026-11-26", "14:00", "15:00"),
        Ev("mine-2", "Weekly 1-2-1 - Jordan", "2026-11-26", "15:00", "15:30", organiser: "Alex Smith", orgAddr: Me, attendees: WithJordan));

    private string QueueText() => File.ReadAllText(config.QueueFile);

    [Fact]
    public void invite_decline_previews_the_instances_the_note_and_the_response_and_writes_nothing()
    {
        Triage.SaveQueue(config, [LeadsSync()]);
        var before = QueueText();
        var graph = InviteGraph();
        var (code, output) = Run(graph, "--writes", "live", "invite", "decline", "1", "--note", "Clashes, sorry");
        Assert.Equal(0, code);
        Assert.Contains("1. \"Leads sync: Tue 24 Nov 09:00–09:30\", organised by Sam Jones", output);
        Assert.Contains("Would decline 1 occurrence, not the series: Tue 24 Nov 09:00.", output);
        Assert.Contains("Sam Jones gets a response, with the note \"Clashes, sorry\".", output);
        Assert.Contains("Nothing sent. Run again with --yes to send it.", output);
        Assert.DoesNotContain("WHOLE SERIES", output);
        Assert.Empty(graph.Writes);
        Assert.Equal(before, QueueText());                  // still queued, unchanged
    }

    [Fact]
    public void invite_decline_series_preview_makes_the_whole_series_stand_out()
    {
        Triage.SaveQueue(config, [LeadsSync()]);
        var graph = InviteGraph();
        var (code, output) = Run(graph, "invite", "decline", "1", "--series");
        Assert.Equal(0, code);
        Assert.Contains("Would decline THE WHOLE SERIES (--series): every occurrence, not only the clashing ones.", output);
        Assert.Contains("Sam Jones gets a response, with no note.", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void invite_decline_with_yes_declines_the_instance_once()
    {
        Triage.SaveQueue(config, [LeadsSync()]);
        var graph = InviteGraph();
        var (code, _) = Run(graph, "invite", "decline", "1", "--note", "Clashes, sorry", "--yes");
        Assert.Equal(0, code);
        var write = Assert.Single(graph.Writes);
        Assert.Equal(("POST", "/me/events/s1/decline"), (write.Method, write.Url));
        Assert.Equal("Clashes, sorry", write.Body!.Req("comment"));
        Assert.Empty(Triage.Queue(config));
    }

    [Fact]
    public void invite_decline_series_with_yes_declines_the_series_once()
    {
        Triage.SaveQueue(config, [LeadsSync()]);
        var graph = InviteGraph();
        Assert.Equal(0, Run(graph, "invite", "decline", "1", "--series", "--yes").Code);
        Assert.Equal(("POST", "/me/events/series-1/decline"), (Assert.Single(graph.Writes).Method, Assert.Single(graph.Writes).Url));
    }

    [Fact]
    public void invite_accept_move_previews_each_move_and_who_gets_an_update()
    {
        Triage.SaveQueue(config, [DesignReview()]);
        var graph = InviteGraph();
        var (code, output) = Run(graph, "invite", "accept", "1", "--move");
        Assert.Equal(0, code);
        Assert.Contains("Would accept it.", output);
        Assert.Contains("Sam Jones gets a response, with no note.", output);
        Assert.Contains("Would move 2 of your own:", output);
        Assert.Contains("\"Focus time\" Thu 26 Nov 14:00–15:00 -> Thu 26 Nov 13:00–14:00: no one else is invited, so no one is told.", output);
        Assert.Contains("\"Weekly 1-2-1 - Jordan\" Thu 26 Nov 15:00–15:30 -> Thu 26 Nov 16:00–16:30: 1 person gets an update: Jordan Lee <jordan@example.com>.", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void invite_accept_move_with_yes_accepts_and_moves_each_once()
    {
        Triage.SaveQueue(config, [DesignReview()]);
        var graph = InviteGraph();
        Assert.Equal(0, Run(graph, "invite", "accept", "1", "--move", "--yes").Code);
        Assert.Equal(["POST /me/events/i-1/accept", "PATCH /me/events/blk-1", "PATCH /me/events/mine-2"], graph.Writes.Select(w => $"{w.Method} {w.Url}"));
    }

    [Fact]
    public void invite_preview_says_when_the_organiser_is_not_told_and_when_move_does_nothing()
    {
        Triage.SaveQueue(config, [LeadsSync(), Invite("Retro: Wed 25 Nov 13:00–14:00", null, "i-2", [], "accept", responseRequested: false)]);
        var graph = InviteGraph();
        var (_, silent) = Run(graph, "invite", "decline", "1", "--silent", "--note", "Sorry", "--move");
        Assert.Contains("No response is sent (--silent): Sam Jones isn't told, and the note isn't sent.", silent);
        Assert.Contains("--move is only for accept: nothing would move.", silent);
        var (_, unasked) = Run(graph, "invite", "tentative", "2");
        Assert.Contains("Would accept it tentatively.", unasked);
        Assert.Contains("No response is sent (the organiser didn't ask for one): Sam Jones isn't told.", unasked);
        Assert.Contains("--move: nothing of yours to move.", Run(graph, "invite", "accept", "2", "--move").Out);
        var (_, series) = Run(graph, "invite", "accept", "1");
        Assert.Contains("Would accept the whole series.", series);
        Assert.Contains("It stays queued, to decline 1 occurrence(s) with `mail-triage invite decline 1`.", series);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public void invite_preview_refuses_what_yes_would_refuse()
    {
        Triage.SaveQueue(config, [DesignReview()]);
        var graph = InviteGraph();
        var (code, output) = Run(graph, "invite", "decline", "1", "--series");
        Assert.Equal(2, code);
        Assert.Contains("--series is for a recurring meeting", output);
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public async Task invite_preview_takes_no_lock_so_a_run_in_progress_does_not_hold_it_up()
    {
        var cancel = TestContext.Current.CancellationToken;
        Triage.SaveQueue(config, [LeadsSync()]);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (Locks.Exclusive(Watcher.RunLock(config), 5, "triage run"))
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        });
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10), cancel));
            // The lock is held for up to 30 seconds; a preview that waited for it would time out here (or fail after 600).
            var (code, output) = await Task.Run(() => Run(InviteGraph(), "invite", "decline", "1"), cancel).WaitAsync(TimeSpan.FromSeconds(10), cancel);
            Assert.Equal(0, code);
            Assert.Contains("Nothing sent.", output);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    // ---- the unattended paths are unchanged -------------------------------------------------------------------

    [Fact]
    public void a_run_still_answers_and_moves_by_rule_without_yes()
    {
        var path = dir.Write("auto.toml", InviteSettings.Replace("[invites]\n", "[invites]\nauto_accept = true\nauto_move_blocks = true\n"));
        var graph = new Graph(
            Self("blk-1", "Focus time", "2026-11-26", "14:00", "15:00"),
            Ev("i-free", "Project sync", "2026-11-25", "10:00", "11:00", attendees: WithSarah),
            Ev("i-block", "Design review", "2026-11-26", "14:30", "15:30", attendees: WithSarah));
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now)))
            code = Cli.Run(["--config", path, "run"], new CliHost { Out = output, Err = output, Echo = true, Graph = (_, _) => graph });
        Assert.True(code == 0, output.ToString());
        var writes = graph.Writes.Select(w => $"{w.Method} {w.Url}").ToList();
        Assert.Contains("POST /me/events/i-free/accept", writes);
        Assert.Contains("POST /me/events/i-block/accept", writes);
        Assert.Contains("PATCH /me/events/blk-1", writes);
    }
}
