using System.Text.Json.Nodes;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

// test_invites.py, test for test, under the same names. "Now" is Wed 11 Nov 2026, 09:00.

public sealed class Invites : IDisposable
{
    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly DateTime now = new(2026, 11, 11, 9, 0, 0);
    private readonly FakeCalendar calendar;

    public Invites()
    {
        config = dir.Settings(InviteSettings);
        calendar = new FakeCalendar([
            // Alex's own diary
            Self("leave", "Holiday", "2026-12-16", "00:00", "23:59", endDay: "2026-12-18", showAs: "oof"),
            Ev("john", "Weekly 1-2-1 - Alex", "2026-11-13", "10:30", "11:00", organiser: "Pat Boss", orgAddr: "pat@example.com", response: "accepted"),
            Ev("mine", "Weekly 1-2-1 - Jordan", "2026-11-11", "15:30", "16:20", organiser: "Alex Smith", orgAddr: Me,
               attendees: [("Alex Smith", Me, "required"), ("Jordan", "jordan@example.com", "required")]),
            Self("travel-am", "Travel", "2026-11-12", "07:00", "08:00", showAs: "oof"),
            Self("block", "Focus time", "2026-11-12", "14:00", "15:00"),
            Self("travel-pm", "Travel", "2026-11-12", "17:00", "18:00", showAs: "oof"),
            Self("update", "Weekly update for Pat", "2026-11-13", "16:00", "16:30"),
            Self("prep", "Prep for leadership", "2026-11-16", "15:30", "16:00"),
            Ev("tue", "Tuesday workshop", "2026-11-17", "08:00", "11:00", response: "accepted"),
            Ev("lead", "Leadership weekly", "2026-11-17", "11:00", "12:00", response: "accepted"),
            Ev("talk", "Lunch and learn", "2026-11-18", "10:00", "11:00", response: "accepted"),
            Self("travel-mid", "Travel to site", "2026-11-18", "12:00", "13:00", showAs: "oof"),
            Ev("review", "Quarterly review", "2026-11-18", "15:00", "16:00", response: "accepted"),
            // Invites waiting for an answer
            Ev("i-leave", "Planning day", "2026-12-16", "10:00", "12:00"),
            Ev("i-john", "Vendor demo", "2026-11-13", "10:00", "11:00", organiser: "Rudi", orgAddr: "rudi@vendor.com"),
            Ev("i-block", "Design review", "2026-11-12", "14:30", "15:30"),
            Ev("i-mine", "Architecture catch-up", "2026-11-11", "15:00", "15:50", organiser: "Robin", orgAddr: "robin@example.com"),
            Ev("i-early", "Breakfast briefing", "2026-11-12", "07:30", "08:30"),
            Ev("i-evening", "Social", "2026-11-12", "17:00", "19:00", attendees: [("Team", "team@example.com", "required")]),
            Ev("i-mid", "Site meeting", "2026-11-18", "12:30", "13:30"),
            Ev("i-update", "All hands", "2026-11-13", "15:30", "17:00"),
            Ev("i-prep", "Away day", "2026-11-16", "08:00", "18:00"),
            Ev("i-free", "Retro", "2026-11-18", "13:00", "14:00", attendees: [("Alex Smith", Me, "optional")], responseRequested: false),
            Ev("i-away", "Kurt Holiday - All Day", "2026-11-20", "00:00", "23:59", isAllDay: true, showAs: "free"),
            Ev("i-marker", "Salary changes due", "2026-11-12", "00:00", "23:59", isAllDay: true, showAs: "free",
               attendees: [("Managers", "managers@example.com", "required")]),
            Ev("s1", "Leads sync", "2026-11-17", "09:00", "09:30", type: "occurrence", seriesMasterId: "series-1"),
            Ev("s2", "Leads sync", "2026-11-24", "09:00", "09:30", type: "occurrence", seriesMasterId: "series-1"),
            Ev("past", "Yesterday's thing", "2026-11-10", "10:00", "11:00"),
        ]);
    }

    public void Dispose() => dir.Dispose();

    private (Dictionary<string, JsonObject> Items, HashSet<string> Seen, int Skipped, JsonObject Raised) RunTriage(JsonObject? raised = null)
    {
        raised ??= [];
        var (items, seen, skipped) = InviteTriage.Triage(config, calendar, raised, Quiet, now);
        return (items.ToDictionary(i => i.Req("subject").Split(':')[0]), seen, skipped, raised);
    }

    // Not in test_invites.py. What v0.9.1 on Python 3.14.5 does, checked against it: 13:30-14:30 for 08:00-18:00
    // and 08:00-23:59, None for 08:00-24:00 (time.fromisoformat("24:00") is 00:00, so the day closes as it opens),
    // and ValueError for 08:00-24:30.
    [Fact]
    public void a_working_day_ending_at_24_00_has_no_free_slot_as_in_python_3_14()
    {
        var block = new Held { Id = "block", Subject = "Focus time", Start = At("2026-11-12", "14:00"), End = At("2026-11-12", "15:00"), Kind = "your block" };
        var avoid = (At("2026-11-12", "14:30"), At("2026-11-12", "15:30"));
        (DateTime, DateTime)? Slot(string end)
        {
            config.Invites!.WorkingHours = ("08:00", end);
            return InviteTriage.FreeSlot(config, block, [block], avoid, [], now);
        }
        Assert.Equal((At("2026-11-12", "13:30"), At("2026-11-12", "14:30")), Slot("18:00"));
        Assert.Equal((At("2026-11-12", "13:30"), At("2026-11-12", "14:30")), Slot("23:59"));
        Assert.Null(Slot("24:00"));
        Assert.Throws<FormatException>(() => Slot("24:30"));
    }

    [Fact]
    public void recommendations_follow_the_evidence()
    {
        var (items, _, skipped, _) = RunTriage();
        string Rec(string name) => items[name].Req("recommendation");
        Assert.Equal("decline", Rec("Planning day"));                     // leave
        Assert.Equal("On annual leave 16/12 to 18/12", items["Planning day"].Req("note"));
        Assert.Equal("decline", Rec("Vendor demo"));                      // protected 1:1
        Assert.Contains("an outside organiser", items["Vendor demo"].Req("check"));
        Assert.Equal("decline", Rec("Site meeting"));                     // travel in the middle of the day
        Assert.Contains("travel mid-day", items["Site meeting"].Req("check"));
        Assert.Equal("decline", Rec("Away day"));                         // prep has nowhere to go before Tuesday's meeting
        Assert.Contains("no free slot", items["Away day"].Req("check"));
        Assert.Equal("accept", Rec("Retro"));
        Assert.Contains("optional", items["Retro"].Req("check"));
        Assert.Equal("accept", Rec("Salary changes due"));                // holds no time
        Assert.Empty((JsonArray)items["Salary changes due"]["clashes"]!);
        Assert.DoesNotContain("Kurt Holiday - All Day", items.Keys);      // skipped by rule
        Assert.DoesNotContain("Yesterday's thing", items.Keys);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void what_can_move_comes_with_where_to()
    {
        var (items, _, _, _) = RunTriage();
        List<(string, string, string)> Moved(string name)
        {
            Assert.True(items[name].Req("recommendation") == "accept and move", items[name].Req("check"));
            return items[name]["clashes"].Objects().Select(c => (c.Req("id"), c.Req("to"), c.Req("to_end"))).ToList();
        }
        Assert.Equal([("block", "2026-11-12T13:30", "2026-11-12T14:30")], Moved("Design review"));   // nearest free
        Assert.Equal([("mine", "2026-11-11T16:00", "2026-11-11T16:50")], Moved("Architecture catch-up"));
        Assert.Equal([("travel-am", "2026-11-12T06:30", "2026-11-12T07:30")], Moved("Breakfast briefing"));  // in earlier
        Assert.Equal([("travel-pm", "2026-11-12T19:00", "2026-11-12T20:00")], Moved("Social"));  // home later
        Assert.Equal([("update", "2026-11-13T15:00", "2026-11-13T15:30")], Moved("All hands"));  // stays on Friday
    }

    [Fact]
    public void a_series_is_raised_once_and_only_clashing_instances_are_declined()
    {
        var (items, seen, _, raised) = RunTriage();
        var series = items["Leads sync"];
        Assert.Equal(("series-1", "series-1"), (series.Req("id"), series.Req("series")));
        Assert.Equal("accept, and decline the clashing ones", series.Req("recommendation"));
        Assert.Equal(["s1"], series["decline"].Strings());                         // Tuesday's workshop
        var (again, _, _, _) = RunTriage(raised);
        Assert.Empty(again);
        Assert.Contains("invite:series-1", seen);
    }

    private void Answer(string name, string action, bool series = false, bool move = false)
    {
        var number = Triage.Queue(config).First(i => i.Req("subject").StartsWith(name, StringComparison.Ordinal))["n"]!.ToJsonString();
        Triage.Answer(config, calendar, [number], action, log: Quiet, series: series, move: move);
    }

    [Fact]
    public void answers_decline_instances_never_the_series_unless_asked()
    {
        var (items, _, _, _) = RunTriage();
        Triage.SaveQueue(config, items.Values.ToList());
        Answer("Leads sync", "accept");
        Assert.Equal(("series-1", "accept", "", true), calendar.Answers[^1]);       // a series is accepted whole
        Assert.Contains("series-1", Triage.Queue(config).Select(i => i.Req("id")));  // still to decline s1
        Answer("Leads sync", "decline");
        Assert.Equal(("s1", "decline", "", true), calendar.Answers[^1]);             // the instance only
        Assert.DoesNotContain("series-1", Triage.Queue(config).Select(i => i.Req("id")));
        Triage.SaveQueue(config, items.Values.ToList());
        Answer("Leads sync", "decline", series: true);
        Assert.Equal(("series-1", "decline", "", true), calendar.Answers[^1]);       // only when asked
    }

    [Fact]
    public void leave_note_no_response_where_none_asked_and_moves()
    {
        var (items, _, _, _) = RunTriage();
        Triage.SaveQueue(config, items.Values.ToList());
        Answer("Planning day", "decline");
        Assert.Equal(("i-leave", "decline", "On annual leave 16/12 to 18/12", true), calendar.Answers[^1]);
        Answer("Retro", "decline");
        Assert.Equal(("i-free", "decline", "", false), calendar.Answers[^1]);
        Answer("Architecture catch-up", "accept", move: true);
        Assert.Equal([("mine", new DateTime(2026, 11, 11, 16, 0, 0), new DateTime(2026, 11, 11, 16, 50, 0))], calendar.Moves);
        Assert.Throws<LookupException>(() => Triage.Delete(config, null, ["1"], Quiet));   // an invite isn't deleted like an email
    }

    [Fact]
    public void leave_sweep_finds_what_you_havent_declined_with_its_note()
    {
        var found = InviteTriage.LeaveSweep(config, calendar, DateOnly.FromDateTime(now), 60);
        Assert.Equal([("i-leave", "On annual leave 16/12 to 18/12")], found.Select(e => (e.Req("id"), e.Req("note"))));
    }

    [Fact]
    public void a_single_day_of_leave_has_the_plain_note()
    {
        Assert.Equal("On annual leave", InviteTriage.LeaveNote(config, new DateOnly(2026, 11, 20), [new DateOnly(2026, 11, 20)]));
        // Friday and Monday: the weekend doesn't break it
        Assert.Equal("On annual leave 20/11 to 23/11",
            InviteTriage.LeaveNote(config, new DateOnly(2026, 11, 20), [new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23)]));
    }
}

/// <summary>Invitation emails in one folder, each linked to its meeting (or to none: declined, so removed).</summary>
internal sealed class RequestGraph : IGraph
{
    private const string Req = "#microsoft.graph.eventMessageRequest";

    private readonly List<JsonObject> messages =
    [
        new() { ["@odata.type"] = Req, ["id"] = "m1", ["subject"] = "Accepted one" },
        new() { ["@odata.type"] = Req, ["id"] = "m2", ["subject"] = "Declined one" },
        new() { ["@odata.type"] = Req, ["id"] = "m3", ["subject"] = "Not answered yet" },
        new() { ["@odata.type"] = "#microsoft.graph.message", ["id"] = "m4", ["subject"] = "An ordinary email" },
    ];

    private readonly Dictionary<string, JsonObject> events = new()
    {
        ["m1"] = new() { ["id"] = "e1", ["responseStatus"] = new JsonObject { ["response"] = "accepted" } },
        ["m2"] = [],
        ["m3"] = new() { ["id"] = "e3", ["responseStatus"] = new JsonObject { ["response"] = "notResponded" } },
    };

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) =>
        messages.Select(m => (JsonObject)m.DeepClone());

    public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var id = url.Split('/')[3].Split('?')[0];
        return new JsonObject { ["id"] = id, ["event"] = events[id].DeepClone() };
    }
}

public sealed class Requests
{
    [Fact]
    public void answered_or_removed_invitations_are_found_and_the_rest_left()
    {
        var found = Proposals.AnsweredRequests(new RequestGraph(), "folder");
        Assert.Equal([("m1", "accepted"), ("m2", "gone")], found.Select(m => (m.Req("id"), m.Req("answer"))));
    }
}

/// <summary>accept_shortened: a meeting you'd accepted, whose organiser only shortened it, is accepted unasked.</summary>
public sealed class Shortened : IDisposable
{
    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly DateTime now = new(2026, 11, 11, 9, 0, 0);

    public Shortened() => config = dir.Settings(InviteSettings.Replace("[invites]", "[invites]\naccept_shortened = true"));

    public void Dispose() => dir.Dispose();

    /// <summary>Remember before (accepted), then triage after (the organiser's change).</summary>
    private (List<JsonObject> Items, HashSet<string> Seen, List<JsonObject> Acted, FakeCalendar Calendar, JsonObject Accepted) RunWith(
        List<JsonObject> before, List<JsonObject> after, bool dryRun = false)
    {
        var accepted = new JsonObject();
        var acted = new List<JsonObject>();
        InviteTriage.Triage(config, new FakeCalendar(before), [], Quiet, now, accepted);
        var calendar = new FakeCalendar(after);
        var (items, seen, _) = InviteTriage.Triage(config, calendar, [], Quiet, now, accepted, acted, dryRun);
        return (items, seen, acted, calendar, accepted);
    }

    private static List<JsonObject> Series(string response, (string Day, string Start, string End)[] slots, string ids = "abc") =>
        slots.Select((s, i) => Ev($"pcb-{ids[i]}", "Alt Net PCB", s.Day, s.Start, s.End, response: response, type: "occurrence", seriesMasterId: "pcb")).ToList();

    [Fact]
    public void a_shortened_series_is_accepted_as_a_whole()
    {
        var before = Series("accepted", [("2026-11-12", "10:00", "10:45"), ("2026-11-26", "10:00", "10:45")]);
        // Rich shortens it, and extends the series by one: the new occurrence comes after the remembered ones
        var after = Series("notResponded", [("2026-11-12", "10:00", "10:30"), ("2026-11-26", "10:00", "10:30"), ("2026-12-10", "10:00", "10:30")]);
        var (items, seen, acted, calendar, accepted) = RunWith(before, after);
        Assert.Empty(items);
        Assert.Empty(seen);                      // off the queue, if it was ever on it
        Assert.Equal([("pcb", "accept", "", true)], calendar.Answers);
        Assert.Contains("10:00–10:45 to 10:00–10:30 (2 occurrences)", acted[0].Req("why"));
        Assert.Equal(["2026-12-10T10:00", "2026-12-10T10:30"], accepted["pcb@2026-12-10"].Strings());   // remembered for next time
    }

    [Fact]
    public void a_shortened_one_off_is_accepted_quietly_if_no_response_was_asked_for()
    {
        var (_, _, acted, calendar, _) = RunWith([Ev("one", "Options", "2026-11-13", "14:00", "15:00", response: "accepted")],
                                                 [Ev("one", "Options", "2026-11-13", "14:15", "15:00", responseRequested: false)]);
        Assert.Equal([("one", "accept", "", false)], calendar.Answers);
        Assert.Single(acted);
    }

    [Theory]
    [InlineData("moved later", "14:00", "15:00", "14:30", "15:30")]
    [InlineData("lengthened", "14:00", "15:00", "14:00", "15:30")]
    [InlineData("unchanged", "14:00", "15:00", "14:00", "15:00")]
    public void anything_more_than_a_shortening_is_raised(string name, string s0, string e0, string s1, string e1)
    {
        _ = name;
        var (items, _, acted, calendar, _) = RunWith([Ev("one", "Options", "2026-11-13", s0, e0, response: "accepted")],
                                                     [Ev("one", "Options", "2026-11-13", s1, e1)]);
        Assert.Equal((1, 0, 0), (items.Count, acted.Count, calendar.Answers.Count));
    }

    [Theory]
    [InlineData("tentativelyAccepted")]
    [InlineData("notResponded")]
    [InlineData("declined")]
    public void only_meetings_you_had_accepted_count(string response)
    {
        var (items, _, acted, _, _) = RunWith([Ev("one", "Options", "2026-11-13", "14:00", "15:00", response: response)],
                                              [Ev("one", "Options", "2026-11-13", "14:00", "14:30")]);
        Assert.Equal((1, 0), (items.Count, acted.Count));
    }

    [Fact]
    public void a_series_with_a_gap_in_what_was_accepted_is_raised()
    {
        var before = Series("accepted", [("2026-11-12", "10:00", "10:45"), ("2026-11-26", "10:00", "10:45")], ids: "ac");
        var after = Series("notResponded", [("2026-11-12", "10:00", "10:30"), ("2026-11-19", "10:00", "10:30"),
                                            ("2026-11-26", "10:00", "10:30")]);   // a new week in the middle: the pattern changed
        var (items, _, acted, _, _) = RunWith(before, after);
        Assert.Equal((1, 0), (items.Count, acted.Count));
    }

    [Fact]
    public void a_dry_run_answers_nothing_and_remembers_nothing()
    {
        List<JsonObject> before = [Ev("one", "Options", "2026-11-13", "14:00", "15:00", response: "accepted")];
        var (_, _, acted, calendar, accepted) = RunWith(before, [Ev("one", "Options", "2026-11-13", "14:00", "14:30")], dryRun: true);
        Assert.Equal((1, 0), (acted.Count, calendar.Answers.Count));
        Assert.Equal("""{"one":["2026-11-13T14:00","2026-11-13T15:00"]}""", accepted.ToJsonString());
    }

    [Fact]
    public void one_week_shortened_is_accepted_on_its_own_even_though_the_series_was_raised_long_ago()
    {
        // The real case (12 Nov 2026): Rich cut one week's Alt Net PCB to 30 minutes. The rest stays accepted.
        var before = Series("accepted", [("2026-11-12", "10:00", "10:45"), ("2026-11-26", "10:00", "10:45")]);
        List<JsonObject> after =
        [
            Ev("pcb-x", "Alt Net PCB", "2026-11-12", "10:00", "10:30", type: "exception", seriesMasterId: "pcb"),
            Ev("pcb-b", "Alt Net PCB", "2026-11-26", "10:00", "10:45", response: "accepted", type: "occurrence", seriesMasterId: "pcb"),
        ];
        var accepted = new JsonObject();
        var acted = new List<JsonObject>();
        InviteTriage.Triage(config, new FakeCalendar(before), [], Quiet, now, accepted);
        var calendar = new FakeCalendar(after);
        var (items, _, _) = InviteTriage.Triage(config, calendar, new JsonObject { ["invite:pcb"] = "2026-11-01T09:00:00" }, Quiet, now, accepted, acted);
        Assert.Empty(items);
        Assert.Equal([("pcb-x", "accept", "", true)], calendar.Answers);   // the occurrence: the series' answer misses it
    }

    [Fact]
    public void off_unless_switched_on()
    {
        config.Invites!.AcceptShortened = false;
        var (items, _, acted, calendar, _) = RunWith([Ev("one", "Options", "2026-11-13", "14:00", "15:00", response: "accepted")],
                                                     [Ev("one", "Options", "2026-11-13", "14:00", "14:30")]);
        Assert.Equal((1, 0, 0), (items.Count, acted.Count, calendar.Answers.Count));
    }
}

/// <summary>An occurrence the organiser changed on its own isn't answered by answering its series.</summary>
public sealed class Exceptions : IDisposable
{
    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly DateTime now = new(2026, 11, 11, 9, 0, 0);

    public Exceptions() => config = dir.Settings(InviteSettings);

    public void Dispose() => dir.Dispose();

    private (List<JsonObject> Items, List<(string, string, string, bool)> Answers) QueueAndAccept(List<JsonObject> events, JsonObject raised)
    {
        var calendar = new FakeCalendar(events);
        var (items, _, _) = InviteTriage.Triage(config, calendar, raised, Quiet, now);
        Triage.SaveQueue(config, items);
        Triage.Answer(config, calendar, ["1"], "accept", log: Quiet);
        return (items, calendar.Answers);
    }

    [Fact]
    public void a_moved_week_of_an_answered_series_comes_back_on_its_own()
    {
        List<JsonObject> events =
        [
            Ev("w1", "Leads sync", "2026-11-17", "14:00", "14:30", type: "exception", seriesMasterId: "leads"),
            Ev("w2", "Leads sync", "2026-11-24", "09:00", "09:30", response: "accepted", type: "occurrence", seriesMasterId: "leads"),
        ];
        var (items, answers) = QueueAndAccept(events, new JsonObject { ["invite:leads"] = "2026-10-01T09:00:00" });
        Assert.Equal([("w1", (string?)null, 0)], items.Select(i => (i.Req("id"), i.Str("series"), ((JsonArray)i["exceptions"]!).Count)));
        Assert.Equal([("w1", "accept", "", true)], answers);            // that week only, not the series
    }

    [Fact]
    public void a_new_series_with_a_changed_week_is_one_invite_and_accepting_it_answers_both()
    {
        List<JsonObject> events =
        [
            Ev("n1", "Design forum", "2026-11-17", "14:00", "15:00", type: "occurrence", seriesMasterId: "forum"),
            Ev("n2", "Design forum", "2026-11-24", "15:00", "16:00", type: "exception", seriesMasterId: "forum"),
        ];
        var (items, answers) = QueueAndAccept(events, []);
        Assert.Equal([("forum", "n2")], items.Select(i => (i.Req("id"), string.Join(",", i["exceptions"].Strings()))));
        Assert.Equal(["forum", "n2"], answers.Select(a => a.Item1));
    }
}

/// <summary>The proposal's mailbox: one "New Time Proposed" reply in the Inbox, and an ordinary email.</summary>
internal sealed class ProposalGraph : IGraph, IMailWriter
{
    public readonly List<string> Deleted = [];
    private readonly JsonObject message;
    private readonly JsonObject full;

    private readonly int? fail;

    public ProposalGraph((string Start, string End) proposed, bool organiser = true, int? fail = null)
    {
        this.fail = fail;
        message = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.eventMessageResponse", ["id"] = "p1", ["internetMessageId"] = "<p1>",
            ["subject"] = "New Time Proposed: GJ and YP catch-up", ["receivedDateTime"] = "2026-11-11T20:13:00Z",
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Yahya Patel", ["address"] = "yahya@example.com" } },
        };
        JsonObject Utc(string t) => new() { ["dateTime"] = $"{t}:00.0000000", ["timeZone"] = "UTC" };
        full = new JsonObject
        {
            ["proposedNewTime"] = new JsonObject { ["start"] = Utc(proposed.Start), ["end"] = Utc(proposed.End) },
            ["startDateTime"] = Utc("2026-11-12T12:00"), ["endDateTime"] = Utc("2026-11-12T12:50"),
            ["event"] = new JsonObject { ["id"] = "yp", ["isOrganizer"] = organiser },
        };
    }

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (url.StartsWith("/me/mailFolders?", StringComparison.Ordinal))
            return [new JsonObject { ["id"] = "Inbox", ["displayName"] = "Inbox", ["childFolderCount"] = 0 }];
        return [(JsonObject)message.DeepClone(), new JsonObject { ["@odata.type"] = "#microsoft.graph.message", ["id"] = "m2", ["subject"] = "Hello" }];
    }

    public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        // What Graph does (13 Nov 2026): $select is read against the base message type, so proposedNewTime is refused.
        if (url.Contains("proposedNewTime", StringComparison.Ordinal))
            throw new M365Graph.GraphRequestException(System.Net.HttpStatusCode.BadRequest, method, url, "");
        if (fail is { } code)
            throw new M365Graph.GraphRequestException((System.Net.HttpStatusCode)code, method, url, "");
        return (JsonObject)full.DeepClone();
    }

    public void Delete(string messageId) => Deleted.Add(messageId);

    public void File(string messageId, string folderId) => throw new InvalidOperationException("not in these tests");
}

public sealed class Proposals_ : IDisposable
{
    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly DateTime now = new(2026, 11, 12, 9, 30, 0);
    private readonly List<JsonObject> diary;

    public Proposals_()
    {
        config = dir.Settings(InviteSettings.Replace("[invites]", "[invites]\nproposals_folders = ['Inbox']\naccept_proposals = true"));
        (string, string, string)[] yahya = [("Alex Smith", Me, "required"), ("Yahya Patel", "yahya@example.com", "required")];
        diary =
        [
            Ev("yp-12", "GJ and YP catch-up", "2026-11-12", "12:00", "12:50", organiser: "Alex Smith", orgAddr: Me, attendees: yahya,
               type: "occurrence", seriesMasterId: "yp"),
            Ev("smt", "SMT", "2026-11-13", "11:00", "12:00", response: "accepted"),
            Self("aw", "GJ and AW catchup", "2026-11-13", "13:00", "13:50"),
            Self("plan", "Work plan", "2026-11-13", "00:00", "23:59", isAllDay: true, showAs: "free"),
        ];
    }

    public void Dispose() => dir.Dispose();

    private (List<JsonObject> Items, List<JsonObject> Acted, List<(string, DateTime, DateTime)> Moves, List<string> Deleted) RunWith(
        (string, string) proposed, List<JsonObject>? diaryOverride = null, bool organiser = true, int? fail = null)
    {
        var box = new ProposalGraph(proposed, organiser, fail);
        var calendar = new FakeCalendar(diaryOverride ?? diary);
        var (items, acted, _) = Proposals.Run(config, new Mailbox(box, box), calendar, [], Quiet, now: now);
        return (items, acted, calendar.Moves, box.Deleted);
    }

    [Fact]
    public void a_proposal_graph_wont_read_is_queued_not_fatal()
    {
        var (items, acted, moves, deleted) = RunWith(("2026-11-13T12:00", "2026-11-13T12:30"), fail: 400);
        Assert.Empty(acted);
        Assert.Empty(moves);
        Assert.Empty(deleted);
        Assert.Single(items);
        Assert.Contains("couldn't read the proposal (Graph said 400)", items[0].Req("check"));
        Assert.Throws<M365Graph.GraphRequestException>(() => RunWith(("2026-11-13T12:00", "2026-11-13T12:30"), fail: 502));
    }

    [Fact]
    public void free_at_the_new_time_moves_that_occurrence_and_deletes_the_proposal()
    {
        // The real case (12 Nov 2026): Yahya declined Thursday 12:00 and proposed Friday 12:00-12:30
        var (items, acted, moves, deleted) = RunWith(("2026-11-13T12:00", "2026-11-13T12:30"));
        Assert.Empty(items);
        Assert.Equal([("yp-12", new DateTime(2026, 11, 13, 12, 0, 0), new DateTime(2026, 11, 13, 12, 30, 0))], moves);
        Assert.Equal(["p1"], deleted);
        Assert.Contains("proposes Fri 13 Nov 12:00–12:30 instead of Thu 12 Nov 12:00", acted[0].Req("why"));
    }

    [Theory]
    [InlineData("clashes with SMT", "2026-11-13T11:30", "2026-11-13T12:00", true, false)]
    [InlineData("outside working hours", "2026-11-13T18:30", "2026-11-13T19:00", true, false)]
    [InlineData("has passed", "2026-11-12T09:00", "2026-11-12T09:30", true, false)]
    [InlineData("not your meeting", "2026-11-13T12:00", "2026-11-13T12:30", false, false)]
    [InlineData("1 other attendee(s) would move too", "2026-11-13T12:00", "2026-11-13T12:30", true, true)]
    public void anything_else_is_queued_with_the_reason(string reason, string start, string end, bool organiser, bool someoneElse)
    {
        List<JsonObject>? group = null;
        if (someoneElse)
        {
            group = diary.Select(e => (JsonObject)e.DeepClone()).ToList();
            ((JsonArray)group[0]["attendees"]!).Add((JsonNode)new JsonObject
            {
                ["type"] = "required", ["emailAddress"] = new JsonObject { ["address"] = "sam@example.com" },
            });
        }
        var (items, acted, moves, deleted) = RunWith((start, end), group, organiser);
        Assert.Equal((0, 0, 0), (acted.Count, moves.Count, deleted.Count));
        Assert.Contains(reason, items[0].Req("check"));
        Assert.Equal("New time proposed", items[0].Req("rule"));
    }

    [Fact]
    public void off_it_only_recommends()
    {
        config.Invites!.AcceptProposals = false;
        var (items, acted, moves, _) = RunWith(("2026-11-13T12:00", "2026-11-13T12:30"));
        Assert.Equal((0, 0), (acted.Count, moves.Count));
        Assert.StartsWith("ACCEPT", items[0].Req("check"));
        Assert.Equal("2026-11-13T12:00", items[0]["move"].Req("start"));
    }
}
