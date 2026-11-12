using System.Text.Json.Nodes;
using MailTriage.Interim;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

// test_one_to_ones.py, test for test, under the same names. "Now" is Thu 12 Nov 2026, 09:00.

public abstract class OneToOneBase : IDisposable
{
    public const string OneToOnesText = """

        [one_to_ones]
        act = true
        days_ahead = 42

        [[one_to_one]]
        name = "Jordan Lee"
        email = "jordan@example.com"

        [[one_to_one]]
        name = "Robin Hall"
        email = "Robin@Example.com"

        [[one_to_one]]
        name = "Pat Boss"                       # a mistake in the settings: the protected rule still wins
        email = "pat@example.com"
        subject = "Weekly 1-2-1 - Alex"

        """;

    public static readonly string[] Wednesdays = ["2026-11-18", "2026-11-25", "2026-12-02", "2026-12-09", "2026-12-16", "2026-12-23"];

    protected readonly TempDir Dir = new();
    protected readonly Settings Config;
    protected DateTime Now = new(2026, 11, 12, 9, 0, 0);
    protected FakeCalendar Calendar = new([]);

    protected OneToOneBase() => Config = Dir.Settings(InviteSettings + OneToOnesText);

    public void Dispose() => Dir.Dispose();

    public static JsonObject OneToOne(string who, string email, string day, string start, string end) =>
        Ev($"{who.ToLowerInvariant()}-{day[5..]}", $"Weekly 1-2-1 - {who}", day, start, end, organiser: "Alex Smith", orgAddr: Me,
           attendees: [("Alex Smith", Me, "required"), (who, email, "required")], type: "occurrence", seriesMasterId: $"{who.ToLowerInvariant()}-series");

    /// <summary>An all-day entry in Alex's diary, first to last inclusive.</summary>
    public static JsonObject Away(string id, string subject, string first, string last, string[]? categories = null) =>
        Self(id, subject, first, "00:00", "00:00", endDay: PyTime.Iso(DateOnly.Parse(last).AddDays(1)), isAllDay: true, showAs: "free", categories: categories);

    public static List<JsonObject> Diary() =>
    [
        .. Wednesdays.Select(d => OneToOne("Jordan", "jordan@example.com", d, "15:30", "16:20")),
        .. Wednesdays.Select(d => OneToOne("Robin", "robin@example.com", d, "11:00", "11:50")),
        .. Wednesdays.Select(d => Ev($"boss-{d[5..]}", "Weekly 1-2-1 - Alex", d, "10:00", "10:30", organiser: "Alex Smith", orgAddr: Me,
                                     attendees: [("Alex Smith", Me, "required"), ("Pat Boss", "pat@example.com", "required")],
                                     type: "occurrence", seriesMasterId: "boss-series")),
    ];

    protected Dictionary<string, Decision> Plan(List<JsonObject> extra, Dictionary<string, List<(DateTime, DateTime, string)>>? busy = null)
    {
        Calendar = new FakeCalendar([.. Diary(), .. extra], busy);
        return OneToOnePlanner.Plan(Config, Calendar, Now).ToDictionary(d => d.Meeting.Req("id"));
    }

    protected static List<JsonObject> Workshops(params int[] days) =>
        days.Select(d => Ev($"w{d}", "Workshop", $"2026-11-{d}", "08:00", "18:00", response: "accepted")).ToList();
}

public sealed class OneToOnes_ : OneToOneBase
{
    [Fact]
    public void settings()
    {
        var reports = Config.OneToOnes!.Reports;
        Assert.Equal([("Jordan Lee", "jordan@example.com", "Weekly 1-2-1 - Jordan"), ("Robin Hall", "robin@example.com", "Weekly 1-2-1 - Robin")],
                     reports.Take(2).Select(r => (r.Name, r.Email, r.Subject)));
        Assert.Equal((3.0, 20L), (Config.OneToOnes.CancelDays, Config.OneToOnes.MaxActions));
        Assert.Throws<SettingsException>(() => Dir.Settings($"email = \"{Me}\"\n" + OneToOnesText, "bad.toml"));   // it needs [invites]
    }

    [Fact]
    public void nobody_off_nothing_happens() => Assert.Empty(Plan([]));

    [Fact]
    public void off_three_days_of_the_week_cancels_with_the_report_note()
    {
        var found = Plan([Away("l", "Jordan Lee annual leave", "2026-11-18", "2026-11-20")]);
        Assert.Equal(["jordan-11-18"], found.Keys);                       // Robin's that day is untouched
        Assert.Equal("cancel", found["jordan-11-18"].Action);
        Assert.Equal("Cancelling as you're on leave - enjoy the time off", found["jordan-11-18"].Note);
        Assert.Contains("Jordan off 3 of 5 days that week", found["jordan-11-18"].Why);
    }

    [Fact]
    public void off_less_of_the_week_moves_to_the_nearest_free_slot()
    {
        var found = Plan([Away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25")]);
        // The same time on the nearest day: Tue and Thu 15:30 are both a day away, so the later, Thursday
        Assert.Equal("move", found["jordan-11-25"].Action);
        Assert.Equal((At("2026-11-26", "15:30"), At("2026-11-26", "16:20")), found["jordan-11-25"].To);
    }

    [Fact]
    public void same_time_taken_on_the_nearest_days_falls_back_to_nearest_in_time()
    {
        var busy = new[] { 23, 24, 26, 27 }.Select(d => Ev($"w{d}", "Workshop", $"2026-11-{d}", "15:00", "17:00", response: "accepted"));
        var found = Plan([Away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25"), .. busy]);
        // 15:30 isn't free on any day, so the nearest slot in time
        Assert.NotEqual(new TimeSpan(15, 30, 0), found["jordan-11-25"].To!.Value.Start.TimeOfDay);
    }

    [Fact]
    public void on_a_tie_the_later_slot()
    {
        var found = Plan([Away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25"), Ev("am", "Workshop", "2026-11-26", "08:00", "14:00", response: "accepted")]);
        // Thursday morning's workshop doesn't touch 15:30, so still Thursday 15:30 (the later of Tue and Thu)
        Assert.Equal((At("2026-11-26", "15:30"), At("2026-11-26", "16:20")), found["jordan-11-25"].To);
    }

    [Fact]
    public void a_move_needs_both_calendars_free()
    {
        // Alex: Monday and Friday taken, Thursday out of the office (oof) in the morning and tentative after
        List<JsonObject> mine =
        [
            .. Workshops(23, 27),
            Self("site", "Site visit", "2026-11-26", "08:00", "13:00", showAs: "oof"),
            Ev("tent", "Maybe this", "2026-11-26", "13:00", "18:00", response: "notResponded", showAs: "tentative"),
        ];
        // Jordan on Tuesday: busy 13:00-18:00, tentative 12:00-13:00, free (doesn't count) 11:00-12:00
        var theirs = new Dictionary<string, List<(DateTime, DateTime, string)>>
        {
            ["jordan@example.com"] =
            [
                (At("2026-11-24", "13:00"), At("2026-11-24", "18:00"), "busy"),
                (At("2026-11-24", "12:00"), At("2026-11-24", "13:00"), "tentative"),
                (At("2026-11-24", "11:00"), At("2026-11-24", "12:00"), "free"),
            ],
        };
        var found = Plan([Away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25"), .. mine], theirs);
        Assert.Equal((At("2026-11-24", "11:00"), At("2026-11-24", "11:50")), found["jordan-11-25"].To);
        Assert.Equal(["jordan@example.com"], Calendar.Asked);             // their free/busy, once for the week
    }

    [Fact]
    public void a_half_day_counts_only_if_it_overlaps_the_meeting()
    {
        var found = Plan([Away("r", "Robin Hall on leave PM", "2026-12-02", "2026-12-02"),          // Robin's is at 11:00
                          Away("j", "Jordan Lee annual leave (PM)", "2026-12-02", "2026-12-02")]);  // Jordan's at 15:30
        Assert.Equal(["jordan-12-02"], found.Keys);
        Assert.Equal("move", found["jordan-12-02"].Action);                 // half a day isn't three
        Assert.Contains("Jordan off 0.5 of 5 days", found["jordan-12-02"].Why);
        Assert.Equal(new DateTime(2026, 12, 3), found["jordan-12-02"].To!.Value.Start.Date);
    }

    [Fact]
    public void from_and_to_halves_of_longer_leave()
    {
        List<JsonObject> events =
        [
            Away("a", "Jordan Lee annual leave (from PM)", "2026-12-07", "2026-12-09"),
            Away("b", "Jordan Lee annual leave (to AM)", "2026-12-14", "2026-12-16"),
            Away("c", "Jordan Lee annual leave half day", "2026-12-21", "2026-12-21"),
            Away("d", "Jordan Lee annual leave AM", "2026-12-22", "2026-12-22"),
            Away("e", "Jordan Lee on leave PM", "2026-12-22", "2026-12-22"),
        ];
        var days = OneToOnePlanner.ReportLeave(Config, Config.OneToOnes!.Reports[0], events);
        var expected = new Dictionary<DateOnly, string>
        {
            [new(2026, 12, 7)] = "PM", [new(2026, 12, 8)] = "full", [new(2026, 12, 9)] = "full",
            [new(2026, 12, 14)] = "full", [new(2026, 12, 15)] = "full", [new(2026, 12, 16)] = "AM",
            [new(2026, 12, 21)] = "half", [new(2026, 12, 22)] = "full",     // morning + afternoon
        };
        Assert.Equal(expected.OrderBy(k => k.Key), days.OrderBy(k => k.Key));
        Assert.Empty(OneToOnePlanner.ReportLeave(Config, Config.OneToOnes.Reports[1], events));
    }

    [Fact]
    public void a_half_day_of_unknown_half_is_raised_not_acted_on()
    {
        var found = Plan([Self("h", "Holiday (half day) (Principal Architect)", "2026-12-09", "00:00", "23:59", showAs: "busy")]);
        Assert.Equal(new Dictionary<string, string> { ["jordan-12-09"] = "raise", ["robin-12-09"] = "raise" }, found.ToDictionary(k => k.Key, k => k.Value.Action));
        Assert.Contains("you have a half day Wed 09 Dec and which half isn't known", found["jordan-12-09"].Why);
        Assert.StartsWith("cancel it, or move it to", found["jordan-12-09"].Suggest);
    }

    [Fact]
    public void your_own_leave_from_people_first_cancels_with_your_note()
    {
        // People First: "Holiday (<job title>)", 00:00 to 23:59, busy and private, not all-day
        var found = Plan([Self("h", "Holiday (Principal Architect)", "2026-12-16", "00:00", "23:59", endDay: "2026-12-31", showAs: "busy", sensitivity: "private"),
                          Away("x", "Christmas Day", "2026-12-25", "2026-12-25", ["Holiday"])]);
        var expected = new[] { "jordan-12-16", "robin-12-16", "jordan-12-23", "robin-12-23" }.ToDictionary(k => k, _ => ("cancel", (string?)"Cancelling as I'm on leave"));
        Assert.Equal(expected.OrderBy(k => k.Key), found.ToDictionary(k => k.Key, k => (k.Value.Action, k.Value.Note)).OrderBy(k => k.Key));
        Assert.Contains("you off 3 of 5 days that week", found["jordan-12-16"].Why);        // Wed to Fri
        Assert.Contains("you off 5 of 5 days that week", found["jordan-12-23"].Why);        // leave + Christmas Day
    }

    [Fact]
    public void a_bank_holiday_moves_or_cancels_for_both()
    {
        var holiday = Away("b", "Founders' Day", "2026-11-25", "2026-11-25", ["Holiday"]);
        var found = Plan([holiday]);
        Assert.Equal(new Dictionary<string, string> { ["jordan-11-25"] = "move", ["robin-11-25"] = "move" }.OrderBy(k => k.Key),
                     found.ToDictionary(k => k.Key, k => k.Value.Action).OrderBy(k => k.Key));
        Assert.Contains("the bank holiday Wed 25 Nov", found["robin-11-25"].Why);
        // With two more days of Jordan's leave that week, Jordan is off three: cancelled, for the bank holiday
        found = Plan([holiday, Away("l", "Jordan Lee on leave", "2026-11-23", "2026-11-24")]);
        Assert.Equal(("cancel", "Cancelling for the bank holiday"), (found["jordan-11-25"].Action, found["jordan-11-25"].Note));
        Assert.Equal("move", found["robin-11-25"].Action);
        // Not in the Holiday category, and not all-day: not a bank holiday
        Assert.Empty(Plan([Away("n", "Founders' Day", "2026-11-25", "2026-11-25")]));
    }

    [Fact]
    public void no_free_slot_is_raised_with_a_suggestion()
    {
        var found = Plan([Away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25"), .. Workshops(23, 24, 26, 27)]);
        var d = found["jordan-11-25"];
        Assert.Equal("raise", d.Action);
        Assert.Contains("no time that week when you're both free", d.Why);
        Assert.Contains("Mon 30 Nov 15:30–16:20 (outside its week)", d.Suggest);     // the same time, nearest day either side
    }

    [Fact]
    public void unreadable_free_busy_is_raised()
    {
        Config.OneToOnes!.Reports[0].Email = "unreadable@example.com";
        var events = Diary().Select(e =>
        {
            if (!e.Req("id").StartsWith("jordan", StringComparison.Ordinal))
                return e;
            e["attendees"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "required", ["emailAddress"] = new JsonObject { ["address"] = "unreadable@example.com" } });
            return e;
        }).ToList();
        Calendar = new FakeCalendar([.. events, Away("l", "Jordan Lee on leave", "2026-11-25", "2026-11-25")]);
        var found = OneToOnePlanner.Plan(Config, Calendar, Now).ToDictionary(d => d.Meeting.Req("id"));
        Assert.Contains("couldn't read Jordan Lee's calendar", found["jordan-11-25"].Why);
    }

    [Fact]
    public void never_the_protected_1_1_nor_someone_elses_meeting()
    {
        var found = Plan([Away("p", "Pat Boss annual leave", "2026-11-16", "2026-11-20"),
                          Ev("theirs", "Weekly 1-2-1 - Jordan", "2026-11-18", "09:00", "09:30", organiser: "Jordan Lee", orgAddr: "jordan@example.com", response: "accepted"),
                          Away("l", "Jordan Lee annual leave", "2026-11-18", "2026-11-20")]);
        Assert.Equal(["jordan-11-18"], found.Keys);                        // not boss-*, not Jordan's own meeting
    }

    [Fact]
    public void past_and_beyond_the_window_are_left()
    {
        Now = new DateTime(2026, 11, 18, 16, 0, 0);                             // after Wednesday's 1:1s
        Assert.Empty(Plan([Away("l", "Jordan Lee annual leave", "2026-11-18", "2026-11-20"),
                           Away("m", "Robin Hall annual leave", "2027-01-04", "2027-01-08")]));
    }
}

/// <summary>run(): acting, recording, queueing, the cap and act = false.</summary>
public sealed class Runs : OneToOneBase
{
    public static List<JsonObject> Leave() =>
    [
        Away("l", "Jordan Lee annual leave", "2026-11-18", "2026-11-20"),          // cancel 18 Nov
        Away("m", "Robin Hall on leave", "2026-11-25", "2026-11-25"),              // move 25 Nov
    ];

    private (List<JsonObject> Items, List<JsonObject> Acted, HashSet<string> Seen, JsonObject Raised) RunWith(List<JsonObject> extra,
        JsonObject? raised = null, bool dryRun = false)
    {
        Calendar = new FakeCalendar([.. Diary(), .. extra]);
        raised ??= [];
        var (items, acted, seen) = OneToOnePlanner.Run(Config, Calendar, raised, Quiet, dryRun, Now);
        return (items, acted, seen, raised);
    }

    [Fact]
    public void act_cancels_and_moves_the_occurrence_only()
    {
        var (items, acted, _, _) = RunWith(Leave());
        Assert.Empty(items);
        Assert.Equal([("jordan-11-18", "Cancelling as you're on leave - enjoy the time off")], Calendar.Cancels);
        Assert.Equal([("robin-11-25", At("2026-11-26", "11:00"), At("2026-11-26", "11:50"))], Calendar.Moves);
        Assert.Equal(["1:1 cancelled: leave", "1:1 moved: leave"], acted.Select(a => a.Req("rule")));
        Assert.Contains("moved to Thu 26 Nov 11:00–11:50", acted[1].Req("why"));
        Assert.Empty(Calendar.Answers);
    }

    [Fact]
    public void act_false_records_each_would_do_once_and_changes_nothing()
    {
        Config.OneToOnes!.Act = false;
        var (_, acted, _, raised) = RunWith(Leave());
        Assert.Equal((0, 0), (Calendar.Cancels.Count, Calendar.Moves.Count));
        Assert.Equal(["1:1 would be cancelled: leave (act = false)", "1:1 would be moved: leave (act = false)"], acted.Select(a => a.Req("rule")));
        var (_, again, _, _) = RunWith(Leave(), raised);
        Assert.Empty(again);                                            // recorded once, not every 15 minutes
    }

    [Fact]
    public void a_dry_run_changes_and_remembers_nothing()
    {
        var (_, acted, _, raised) = RunWith(Leave(), dryRun: true);
        Assert.Equal((0, 0, 0), (Calendar.Cancels.Count, Calendar.Moves.Count, raised.Count));
        Assert.Equal(2, acted.Count);
    }

    [Fact]
    public void at_most_max_actions_a_run()
    {
        Config.OneToOnes!.MaxActions = 1;
        var (_, acted, _, _) = RunWith(Leave());
        Assert.Equal((1, 1, 0), (acted.Count, Calendar.Cancels.Count, Calendar.Moves.Count));
    }

    [Fact]
    public void what_cant_be_settled_is_queued_once()
    {
        var (items, acted, seen, raised) = RunWith([Leave()[1], .. Workshops(23, 24, 26, 27)]);
        Assert.Equal((0, 0), (acted.Count, Calendar.Moves.Count));
        Assert.Equal([("one-to-one", "robin-11-25", "Robin Hall", "1:1 and leave")],
                     items.Select(i => (i.Req("type"), i.Req("id"), i.Req("from"), i.Req("rule"))));
        Assert.StartsWith("DECIDE: Robin Hall is on leave Wed 25 Nov", items[0].Req("check"));
        var (again, _, seenAgain, _) = RunWith([Leave()[1], .. Workshops(23, 24, 26, 27)], raised);
        Assert.Empty(again);
        Assert.Equal(seen, seenAgain);                                  // still waiting, so it stays queued
    }

    [Fact]
    public void a_queued_meeting_isnt_deleted_or_answered_like_an_email_or_invite()
    {
        var (items, _, _, _) = RunWith([Leave()[1], .. Workshops(23, 24, 26, 27)]);
        Triage.SaveQueue(Config, items);
        Assert.Throws<LookupException>(() => Triage.Delete(Config, null, ["1"], Quiet));
        Assert.Throws<LookupException>(() => Triage.Answer(Config, Calendar, ["1"], "decline", log: Quiet));
        Assert.Throws<LookupException>(() => Triage.File(Config, null, ["1"], "Inbox/X", Quiet));
    }
}

/// <summary>Through Triage.Run, as the watcher runs it: recorded for `mail-triage deleted`, invites unaffected.</summary>
public sealed class InARun : OneToOneBase
{
    [Fact]
    public void recorded_alongside_invites()
    {
        var calendar = new FakeCalendar([.. Diary(), .. Runs.Leave(), Ev("i", "Planning", "2026-11-19", "10:00", "11:00")]);
        RunReport report;
        using (Clock.Use(new FixedTime(Now)))
            report = Triage.Run(Config, new Mailbox(new FakeGraph([]), WriteDoor.Refusing().Mail), calendar, false, Quiet);
        Assert.Equal(1, report.Invites);                                 // the invite, queued as before
        var gone = Triage.Deleted(Config);
        Assert.Equal([("1:1 cancelled: leave", "Jordan Lee"), ("1:1 moved: leave", "Robin Hall")], gone.Select(g => (g.Req("rule"), g.Req("from"))));
        Assert.Contains("note:", gone[0].Req("did"));
    }
}
