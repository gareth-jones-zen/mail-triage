using System.Net;
using System.Text.Json.Nodes;
using PyCompat;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

/// <summary>1.30.0: a calendar write made by hand whose answer was lost is recorded in unconfirmed_file, so a later command
/// never resends it silently. The next preview or --yes for the same event reads it again: the change visibly there clears
/// the record ("went"); otherwise it's skipped ("may not have landed: check Outlook", exit 1 with --yes) unless --again,
/// which sends it once and records it again if that answer is lost too. Runs (the watcher) are unchanged. "Now" is Mon 23
/// Nov 2026, 09:00.</summary>
public sealed class UnconfirmedWritesTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);

    public UnconfirmedWritesTests() => dir.Settings(InviteSettings);

    public void Dispose() => dir.Dispose();

    private string Config => Path.Combine(dir.Path, "config.toml");

    private Settings Loaded => Settings.Load(Config);

    private JsonObject Records => UnconfirmedWrites.Load(Loaded);

    private static readonly (string, string, string)[] WithSarah = [("Alex Smith", Me, "required"), ("Sarah Wood", "sarah@example.com", "required")];

    /// <summary>A calendar that changes as Microsoft would: a move moves, an answer answers, a cancel cancels, a delete
    /// deletes, a create creates. A write to an id in Lose loses its answer after it was sent; with Lands, the change was
    /// made all the same (it reached Microsoft), otherwise it wasn't.</summary>
    private sealed class Graph(params JsonObject[] events) : IGraph
    {
        private readonly Dictionary<string, JsonObject> byId = events.ToDictionary(e => e.Req("id"));
        private int created;

        public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];

        public HashSet<string> Lose { get; } = [];

        public bool Lands { get; set; }

        public JsonObject this[string id] => byId[id];

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add(("GET", url, null));
            return url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? byId.Values.Select(e => (JsonObject)e.DeepClone()).ToList() : [];
        }

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add((method, url, body?.DeepClone()));
            var parts = url.Split('?')[0].Split('/');
            var id = parts.Length > 3 ? parts[3] : "";
            if (method == "GET")
                return byId.TryGetValue(id, out var e) ? (JsonObject)e.DeepClone()
                    : throw new M365Graph.GraphRequestException(HttpStatusCode.NotFound, method, url, "not found");
            var lose = Lose.Contains(id) || (id == "" && Lose.Contains("new"));
            JsonObject answer = [];
            if (!lose || Lands)
                answer = Apply(method, id, parts.Length > 4 ? parts[4] : "", body);
            if (lose)
                throw new M365Graph.GraphDeliveryUnknownException(method, url, HttpStatusCode.BadGateway, "lost");
            return answer;
        }

        private JsonObject Apply(string method, string id, string verb, JsonNode? body)
        {
            if (method == "DELETE")
                byId.Remove(id);
            else if (method == "PATCH")
            {
                byId[id]["start"] = new JsonObject { ["dateTime"] = body!["start"]!.Req("dateTime") + ".0000000" };
                byId[id]["end"] = new JsonObject { ["dateTime"] = body!["end"]!.Req("dateTime") + ".0000000" };
            }
            else if (verb is "accept" or "tentativelyAccept" or "decline")
                byId[id]["responseStatus"] = new JsonObject { ["response"] = verb switch { "accept" => "accepted", "decline" => "declined", _ => "tentativelyAccepted" } };
            else if (verb == "cancel")
                byId[id]["isCancelled"] = true;
            else if (id == "")
            {
                var e = Self($"new-{++created}", body!.Req("subject"), body!["start"]!.Req("dateTime")[..10], body!["start"]!.Req("dateTime")[11..16],
                    body!["end"]!.Req("dateTime")[11..16]);
                byId[e.Req("id")] = e;
                return (JsonObject)e.DeepClone();
            }
            return [];
        }

        public List<string> Writes => Calls.Where(c => c.Method != "GET").Select(c => $"{c.Method} {c.Url}").ToList();
    }

    private (int Code, string Out) Run(Graph graph, params string[] args) => Run(Config, graph, args);

    private (int Code, string Out) Run(string config, Graph graph, params string[] args)
    {
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now)))
            code = Cli.Run(["--config", config, .. args], new CliHost { Out = output, Err = output, Echo = true, Graph = (_, _) => graph, Sleep = _ => { } });
        return (code, output.ToString());
    }

    // ---- move ---------------------------------------------------------------------------------------------------

    private static JsonObject Meeting() =>
        Ev("mine-1", "GJ and SW catchup", "2026-11-25", "15:00", "15:30", organiser: "Alex Smith", orgAddr: Me, attendees: WithSarah,
            type: "occurrence", seriesMasterId: "series-9");

    [Fact]
    public void a_lost_move_is_recorded_with_what_which_event_the_change_and_when()
    {
        var graph = new Graph(Meeting()) { Lose = { "mine-1" } };
        var (code, output) = Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        Assert.Equal(1, code);
        Assert.Single(graph.Writes);
        var entry = Assert.IsType<JsonObject>(Records["move:mine-1"]);
        Assert.Equal("move", entry.Req("kind"));
        Assert.Equal("mine-1", entry.Req("event"));
        Assert.Equal("GJ and SW catchup", entry.Req("subject"));
        Assert.Equal("2026-11-26T10:00:00", entry.Req("start"));
        Assert.Equal("2026-11-26T10:30:00", entry.Req("end"));
        Assert.Equal("2026-11-23T09:00:00", entry.Req("when"));
        Assert.Contains("lost", entry.Req("why"));
        Assert.Contains("unconfirmed: recorded move of \"GJ and SW catchup\" (to Thu 26 Nov 10:00–10:30), may have gone, not resent", output);
        Assert.Contains("unconfirmed: recorded move", File.ReadAllText(Path.Combine(dir.Path, "log.txt")));
    }

    [Fact]
    public void the_next_move_that_finds_it_not_there_skips_it_and_says_to_check_outlook()
    {
        var graph = new Graph(Meeting()) { Lose = { "mine-1" } };
        Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        graph.Lose.Clear();

        var (preview, shown) = Run(graph, "move", "mine-1", "2026-11-26T10:00");
        Assert.Equal(0, preview);
        Assert.Contains("An earlier move of \"GJ and SW catchup\" (to Thu 26 Nov 10:00–10:30) on Mon 23 Nov 09:00 may not have landed: check Outlook.", shown);
        Assert.Contains("--yes would skip it, as the earlier move may have landed; once you've checked Outlook, --again --yes moves it.", shown);

        var (code, output) = Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("may not have landed: check Outlook. Not sent again: once you've checked, `--again --yes` moves it.", output);
        Assert.Contains("unconfirmed: skipped move", output);
        Assert.Single(graph.Writes);                                        // only the first, lost one: never resent
        Assert.NotNull(Records["move:mine-1"]);
    }

    [Fact]
    public void again_sends_it_once_and_a_confirmed_send_clears_the_record()
    {
        var graph = new Graph(Meeting()) { Lose = { "mine-1" } };
        Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        graph.Lose.Clear();
        var (code, output) = Run(graph, "move", "mine-1", "2026-11-26T10:00", "--again", "--yes");
        Assert.True(code == 0, output);
        Assert.Equal(["PATCH /me/events/mine-1", "PATCH /me/events/mine-1"], graph.Writes);
        Assert.Contains("unconfirmed: sending move of \"GJ and SW catchup\" (to Thu 26 Nov 10:00–10:30) again (--again)", output);
        Assert.Contains("unconfirmed: cleared move", output);
        Assert.Null(Records["move:mine-1"]);
    }

    [Fact]
    public void again_whose_answer_is_lost_too_is_recorded_again_and_sent_only_once()
    {
        var graph = new Graph(Meeting()) { Lose = { "mine-1" } };
        Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        var (code, output) = Run(graph, "move", "mine-1", "2026-11-26T10:00", "--again", "--yes");
        Assert.Equal(1, code);
        Assert.Equal(2, graph.Writes.Count);                                // one each run
        Assert.Contains("unconfirmed: recorded move", output);
        Assert.NotNull(Records["move:mine-1"]);
        Assert.Equal(1, Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes").Code);   // and skipped again after
        Assert.Equal(2, graph.Writes.Count);
    }

    [Fact]
    public void a_later_run_that_sees_the_move_landed_clears_it_and_says_it_went()
    {
        var graph = new Graph(Meeting()) { Lose = { "mine-1" }, Lands = true };
        Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        graph.Lose.Clear();
        var (code, output) = Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("An earlier move of \"GJ and SW catchup\" (to Thu 26 Nov 10:00–10:30) on Mon 23 Nov 09:00 went: it's there now, so it isn't sent again.", output);
        Assert.Contains("Nothing to do: it's already at that time.", output);
        Assert.Contains("unconfirmed: cleared move of \"GJ and SW catchup\" (to Thu 26 Nov 10:00–10:30): it went", output);
        Assert.Single(graph.Writes);
        Assert.Null(Records["move:mine-1"]);
    }

    [Fact]
    public void a_preview_that_sees_it_landed_clears_it_too_and_a_new_time_is_a_new_move()
    {
        var graph = new Graph(Meeting()) { Lose = { "mine-1" }, Lands = true };
        Run(graph, "move", "mine-1", "2026-11-26T10:00", "--yes");
        graph.Lose.Clear();
        var (code, output) = Run(graph, "move", "mine-1", "2026-11-27T11:00");
        Assert.Equal(0, code);
        Assert.Contains("went: it's there now", output);
        Assert.Contains("Would move your meeting", output);
        Assert.Contains("Run again with --yes to move it.", output);
        Assert.Null(Records["move:mine-1"]);
    }

    [Fact]
    public void journal_mode_records_nothing()
    {
        var graph = new Graph(Meeting());
        Run(graph, "--writes", "journal", "--journal", Path.Combine(dir.Path, "journal.txt"), "move", "mine-1", "2026-11-26T10:00", "--yes");
        Assert.False(File.Exists(Path.Combine(dir.Path, "unconfirmed.json")));
    }

    // ---- leave --decline ----------------------------------------------------------------------------------------

    private static Graph InLeave() => new(
        Self("leave", "Holiday", "2026-12-16", "00:00", "23:59", endDay: "2026-12-18", showAs: "oof"),
        Ev("occ-1", "Leads sync", "2026-12-16", "09:00", "09:30", response: "accepted", type: "occurrence", seriesMasterId: "series-1"),
        Ev("one-off", "Architecture forum", "2026-12-17", "10:00", "11:00", organiser: "Jordan Lee", orgAddr: "jordan@example.com"));

    [Fact]
    public void a_lost_leave_decline_exits_1_and_is_recorded()
    {
        var graph = InLeave();
        graph.Lose.Add("occ-1");
        var (code, output) = Run(graph, "leave", "--decline", "--yes");
        Assert.Equal(1, code);
        Assert.Equal(["POST /me/events/occ-1/decline", "POST /me/events/one-off/decline"], graph.Writes);
        var entry = Assert.IsType<JsonObject>(Records["respond:occ-1"]);
        Assert.Equal(("leave", "decline", "Wed 16 Dec 09:00, this occurrence only"), (entry.Req("command"), entry.Req("action"), entry.Req("change")));
        Assert.Null(Records["respond:one-off"]);
        Assert.Contains("1 decline(s) may or may not have gone (above): never sent twice.", output);
        Assert.Contains("2 meeting(s) in your leave: 1 declined, each occurrence on its own.", output);
    }

    [Fact]
    public void the_next_leave_decline_skips_one_that_may_not_have_landed_preview_and_yes()
    {
        var graph = InLeave();
        graph.Lose.Add("occ-1");
        Run(graph, "leave", "--decline", "--yes");
        graph.Lose.Clear();
        var (_, preview) = Run(graph, "leave", "--decline");
        Assert.Contains("An earlier decline of \"Leads sync\" (Wed 16 Dec 09:00, this occurrence only) on Mon 23 Nov 09:00 may not have landed: check Outlook. "
                        + "--yes would skip it; once you've checked, --again --yes declines it.", preview);
        var (code, output) = Run(graph, "leave", "--decline", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("may not have landed: check Outlook. Skipped, never resent: once you've checked, `leave --decline --again --yes` declines it.", output);
        Assert.Contains("1 decline(s) skipped: an earlier one may not have landed (above).", output);
        Assert.Equal(2, graph.Writes.Count);                                // nothing new
        var (again, sent) = Run(graph, "leave", "--decline", "--again", "--yes");
        Assert.True(again == 0, sent);
        Assert.Equal(["POST /me/events/occ-1/decline", "POST /me/events/one-off/decline", "POST /me/events/occ-1/decline"], graph.Writes);
        Assert.Null(Records["respond:occ-1"]);
    }

    [Fact]
    public void a_leave_decline_that_landed_is_cleared_by_the_next_leave()
    {
        var graph = InLeave();
        graph.Lose.Add("occ-1");
        graph.Lands = true;
        Run(graph, "leave", "--decline", "--yes");
        graph.Lose.Clear();
        var (code, output) = Run(graph, "leave", "--decline");
        Assert.Equal(0, code);
        Assert.Contains("An earlier decline of \"Leads sync\" (Wed 16 Dec 09:00, this occurrence only) on Mon 23 Nov 09:00 went: it's there now, so it isn't sent again.", output);
        Assert.Contains("Nothing in your leave over the next 60 days that you haven't declined.", output);
        Assert.Null(Records["respond:occ-1"]);
    }

    // ---- respond, cancel ------------------------------------------------------------------------------------------

    private static JsonObject Invited() =>
        Ev("occ-1", "Leads sync", "2026-11-25", "09:00", "09:30", response: "accepted", type: "occurrence", seriesMasterId: "series-1");

    [Fact]
    public void respond_records_a_lost_answer_skips_it_next_time_and_clears_it_once_it_shows()
    {
        var graph = new Graph(Invited()) { Lose = { "occ-1" } };
        Assert.Equal(1, Run(graph, "respond", "occ-1", "decline", "--yes").Code);
        Assert.Equal("decline", Records["respond:occ-1"]!.Req("action"));
        graph.Lose.Clear();
        var (code, output) = Run(graph, "respond", "occ-1", "decline", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("An earlier decline of \"Leads sync\" (Wed 25 Nov 09:00, this occurrence only) on Mon 23 Nov 09:00 may not have landed: check Outlook. Not sent again", output);
        Assert.Single(graph.Writes);
        graph["occ-1"]["responseStatus"] = new JsonObject { ["response"] = "declined" };   // it shows up after all
        var (later, shown) = Run(graph, "respond", "occ-1", "decline", "--yes");
        Assert.Equal(0, later);
        Assert.Contains("went: it's there now, so it isn't sent again.", shown);
        Assert.Contains("Nothing to do: it's already that way.", shown);
        Assert.Single(graph.Writes);
        Assert.Null(Records["respond:occ-1"]);
    }

    [Fact]
    public void cancel_of_a_block_that_went_is_cleared_when_the_event_is_gone()
    {
        var graph = new Graph(Self("blk", "Focus", "2026-11-25", "14:00", "15:00")) { Lose = { "blk" }, Lands = true };
        Assert.Equal(1, Run(graph, "cancel", "blk", "--yes").Code);
        Assert.Equal("delete", Records["cancel:blk"]!.Req("verb"));
        graph.Lose.Clear();
        var (code, output) = Run(graph, "cancel", "blk", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("An earlier delete of \"Focus\" (Wed 25 Nov 14:00, this meeting) on Mon 23 Nov 09:00 went", output);
        Assert.Contains("Nothing to do: it's no longer in your calendar.", output);
        Assert.Single(graph.Writes);
        Assert.Null(Records["cancel:blk"]);
    }

    // ---- hold -----------------------------------------------------------------------------------------------------

    [Fact]
    public void a_hold_whose_answer_was_lost_is_never_made_twice_unless_again()
    {
        var graph = new Graph() { Lose = { "new" } };
        Assert.Equal(1, Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--yes").Code);
        Assert.NotNull(Records["hold:2026-11-25T16:15|2026-11-25T17:00|Hold: ITS"]);
        graph.Lose.Clear();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("An earlier hold of \"Hold: ITS\" (Wed 25 Nov 16:15-17:00) on Mon 23 Nov 09:00 may not have landed: check Outlook. Not created again", output);
        Assert.Single(graph.Writes);
        Assert.Equal(0, Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--again", "--yes").Code);
        Assert.Equal(2, graph.Writes.Count);
        Assert.Null(Records["hold:2026-11-25T16:15|2026-11-25T17:00|Hold: ITS"]);
    }

    [Fact]
    public void a_hold_that_was_made_after_all_is_found_and_not_made_again()
    {
        var graph = new Graph() { Lose = { "new" }, Lands = true };
        Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--yes");
        graph.Lose.Clear();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("An earlier hold of \"Hold: ITS\" (Wed 25 Nov 16:15-17:00) on Mon 23 Nov 09:00 went", output);
        Assert.Contains("Nothing to do: the hold is in your calendar.", output);
        Assert.Single(graph.Writes);
    }

    // ---- invite ---------------------------------------------------------------------------------------------------

    private Graph Series()
    {
        var graph = new Graph(
            Ev("series-1", "Leads sync", "2026-11-24", "09:00", "09:30", type: "seriesMaster"),
            Ev("exc-1", "Leads sync", "2026-11-26", "10:00", "10:30", type: "exception", seriesMasterId: "series-1"));
        Triage.SaveQueue(Loaded, [new JsonObject
        {
            ["folder"] = "Calendar", ["type"] = "invite", ["id"] = "series-1", ["series"] = "series-1", ["occurrence"] = "occ-a",
            ["exceptions"] = new JsonArray("exc-1"), ["responseRequested"] = true, ["from"] = "Sam Jones",
            ["subject"] = "Leads sync: Tue 24 Nov 09:00–09:30", ["decline"] = new JsonArray(), ["clashes"] = new JsonArray(),
            ["recommendation"] = "accept",
        }]);
        return graph;
    }

    [Fact]
    public void invite_stops_at_a_lost_answer_keeps_the_invite_queued_and_never_resends_what_went()
    {
        var graph = Series();
        graph.Lose.Add("exc-1");
        var (code, output) = Run(graph, "invite", "accept", "1", "--yes");
        Assert.Equal(1, code);
        Assert.Equal(["POST /me/events/series-1/accept", "POST /me/events/exc-1/accept"], graph.Writes);
        var entry = Records["respond:exc-1"]!;
        Assert.Equal(["respond:series-1:accept"], entry["sent"].Strings());
        Assert.Contains("Microsoft didn't confirm the accept of \"Leads sync: Tue 24 Nov 09:00–09:30\" (one occurrence)", output);
        Assert.Contains("may have gone", Triage.Queue(Loaded).Single().Req("check"));

        graph.Lose.Clear();
        var (skip, skipped) = Run(graph, "invite", "accept", "1", "--yes");
        Assert.Equal(1, skip);
        Assert.Contains("may not have landed: check Outlook. Skipped, with the rest of this invite", skipped);
        Assert.Equal(2, graph.Writes.Count);                                // the series isn't answered twice, the lost one isn't resent

        graph["exc-1"]["responseStatus"] = new JsonObject { ["response"] = "accepted" };   // it landed after all
        var (code3, cleared) = Run(graph, "invite", "accept", "1", "--yes");
        Assert.True(code3 == 0, cleared);
        Assert.Contains("went: it's there now, so it isn't sent again.", cleared);
        Assert.Equal(2, graph.Writes.Count);
        Assert.Empty(Triage.Queue(Loaded));
        Assert.Null(Records["respond:exc-1"]);
    }

    [Fact]
    public void invite_again_sends_only_the_lost_one_after_a_preview_that_shows_it()
    {
        var graph = Series();
        graph.Lose.Add("exc-1");
        Run(graph, "invite", "accept", "1", "--yes");
        graph.Lose.Clear();
        var (_, preview) = Run(graph, "invite", "accept", "1");
        Assert.Contains("Already sent by an earlier `invite` (before an answer was lost), so not sent again: accept of", preview);
        Assert.Contains("may not have landed: check Outlook. --yes would skip it and the rest of this invite; once you've checked, --again --yes sends it.", preview);
        var (code, output) = Run(graph, "invite", "accept", "1", "--again", "--yes");
        Assert.True(code == 0, output);
        Assert.Equal(["POST /me/events/series-1/accept", "POST /me/events/exc-1/accept", "POST /me/events/exc-1/accept"], graph.Writes);
        Assert.Empty(Triage.Queue(Loaded));
        Assert.Null(Records["respond:exc-1"]);
    }

    // ---- runs are unchanged -----------------------------------------------------------------------------------------

    [Fact]
    public void a_run_neither_reads_nor_writes_the_record()
    {
        var path = dir.Write("auto.toml", InviteSettings.Replace("[invites]\n", "[invites]\ndecline_days_off = true\n"));
        var graph = InLeave();
        graph.Lose.Add("occ-1");
        Run(graph, "leave", "--decline", "--yes");                         // a by-hand decline of occ-1, lost: recorded (and lost again in the run)
        var before = File.ReadAllText(Path.Combine(dir.Path, "unconfirmed.json"));
        var (code, output) = Run(path, graph, "run");
        Assert.True(code == 0, output);
        Assert.Contains("POST /me/events/occ-1/decline", graph.Writes.Skip(2));   // the run declines it as it always did
        Assert.Equal(before, File.ReadAllText(Path.Combine(dir.Path, "unconfirmed.json")));   // and its own lost answer isn't recorded there
        Assert.DoesNotContain("unconfirmed:", output);
    }
}
