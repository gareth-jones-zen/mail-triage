using System.Text.Json.Nodes;
using PyCompat;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

// [catch_ups] (1.26.0): recurring meetings you organise with one other person in your organisation, cancelled or moved
// when either of you is off, their leave read from their own calendar. "Now" is Thu 12 Nov 2026, 09:00.

public abstract class CatchUpBase : IDisposable
{
    public const string CatchUpsText = """

        [catch_ups]
        act = true
        cancel_days = 3

        [[catch_up_skip]]
        subject = '^Mentoring'

        [[catch_up_skip]]
        subject = 'board'
        email = "drew@example.com"

        """;

    public const string Sam = "sam@example.com";

    /// <summary>Alex's fortnightly-ish catch-up with Sam: Tuesdays 13:30-14:00.</summary>
    public static readonly string[] Tuesdays = ["2026-11-17", "2026-11-24", "2026-12-01", "2026-12-08", "2026-12-15"];

    protected readonly TempDir Dir = new();
    protected Settings Config;
    protected DateTime Now = new(2026, 11, 12, 9, 0, 0);
    protected FakeCalendar Calendar = new([]);

    protected CatchUpBase() => Config = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText + CatchUpsText);

    public void Dispose() => Dir.Dispose();

    /// <summary>A meeting Alex organises with these people, an occurrence of a series unless said otherwise.</summary>
    public static JsonObject Mine(string id, string subject, string day, string start, string end, (string, string, string)[] people,
        string type = "occurrence") =>
        Ev(id, subject, day, start, end, organiser: "Alex Smith", orgAddr: Me, attendees: [("Alex Smith", Me, "required"), .. people], type: type,
           seriesMasterId: type == "singleInstance" ? null : $"{id}-series");

    public static JsonObject CatchUp(string day) =>
        Mine($"sam-{day[5..]}", "Alex / Sam catch-up", day, "13:30", "14:00", [("Sam Jones", Sam, "required")]);

    public static List<JsonObject> Diary() => [.. Tuesdays.Select(CatchUp), .. OneToOneBase.Diary()];

    /// <summary>An entry in someone else's calendar as getSchedule gives it.</summary>
    public static ScheduleItem Theirs(string subject, string first, string start, string last, string end, string status = "oof", bool meeting = false) =>
        new(At(first, start), At(last, end), status, subject, false, meeting);

    /// <summary>An all-day entry of theirs, first to last inclusive.</summary>
    public static ScheduleItem AllDay(string subject, string first, string last, string status = "oof") =>
        Theirs(subject, first, "00:00", PyTime.Iso(DateOnly.Parse(last).AddDays(1)), "00:00", status);

    protected Dictionary<string, Decision> Plan(List<JsonObject> extra, params ScheduleItem[] sams)
    {
        Calendar = new FakeCalendar([.. Diary(), .. extra]);
        Calendar.Entries[Sam] = [.. sams];
        return OneToOnePlanner.PlanCatchUps(Config, Calendar, Now).Decisions.ToDictionary(d => d.Meeting.Req("id"));
    }
}

public sealed class CatchUpDetection : CatchUpBase
{
    private (string, string)? Other(JsonObject meeting) => CatchUpRules.Other(Config, meeting) is { } o ? (o.Name, o.Email) : null;

    [Fact]
    public void a_recurring_meeting_you_organise_with_one_colleague_is_a_catch_up()
    {
        Assert.Equal(("Sam Jones", Sam), Other(CatchUp("2026-11-24")));
        var exception = CatchUp("2026-11-24");
        exception["type"] = "exception";                                                // one occurrence moved: still the series
        Assert.Equal(("Sam Jones", Sam), Other(exception));
        // A room is not a person, as a resource
        Assert.Equal(("Sam Jones", Sam), Other(Mine("r", "Alex / Sam", "2026-11-24", "10:00", "10:30",
            [("Sam Jones", Sam, "required"), ("Room 1", "room1@example.com", "resource")])));
    }

    [Fact]
    public void what_isnt_a_catch_up()
    {
        (string, string, string)[] sam = [("Sam Jones", Sam, "required")];
        Assert.Null(Other(Mine("once", "Alex / Sam", "2026-11-24", "10:00", "10:30", sam, type: "singleInstance")));     // not recurring
        Assert.Null(Other(Ev("theirs", "Sam / Alex", "2026-11-24", "10:00", "10:30", organiser: "Sam Jones", orgAddr: Sam,
            attendees: [("Alex Smith", Me, "required"), ("Sam Jones", Sam, "required")], type: "occurrence", seriesMasterId: "s")));   // Sam organises it
        Assert.Null(Other(Mine("ext", "Supplier sync", "2026-11-24", "10:00", "10:30", [("Kim Lee", "kim@supplier.org", "required")])));   // outside
        Assert.Null(Other(Mine("three", "Trio", "2026-11-24", "10:00", "10:30", [.. sam, ("Robin Hall", "robin@example.com", "required")])));
        Assert.Null(Other(Mine("solo", "Focus time", "2026-11-24", "10:00", "10:30", [])));                                 // a block
        Assert.Null(Other(OneToOneBase.OneToOne("Jordan", "jordan@example.com", "2026-11-25", "15:30", "16:20")));         // a [[one_to_one]]
        var cancelled = CatchUp("2026-11-24");
        cancelled["isCancelled"] = true;
        Assert.Null(Other(cancelled));
        var free = Mine("free", "Alex / Sam", "2026-11-24", "10:00", "10:30", sam);
        free["showAs"] = "free";
        Assert.Null(Other(free));
    }

    [Fact]
    public void a_report_in_a_meeting_that_isnt_their_1_1_is_a_catch_up()
    {
        Assert.Equal(("Jordan Lee", "jordan@example.com"),
            Other(Mine("arch", "Architecture sync", "2026-11-24", "10:00", "10:30", [("Jordan Lee", "jordan@example.com", "required")])));
    }

    [Fact]
    public void skip_rules_by_subject_by_email_or_both()
    {
        Assert.Null(Other(Mine("m", "Mentoring - Sam", "2026-11-24", "10:00", "10:30", [("Sam Jones", Sam, "required")])));
        // subject and email: both must match
        Assert.Null(Other(Mine("b", "Board prep", "2026-11-24", "10:00", "10:30", [("Drew Park", "drew@example.com", "required")])));
        Assert.NotNull(Other(Mine("b2", "Board prep", "2026-11-24", "10:00", "10:30", [("Sam Jones", Sam, "required")])));
        Assert.NotNull(Other(Mine("d", "Drew / Alex", "2026-11-24", "10:00", "10:30", [("Drew Park", "drew@example.com", "required")])));
    }

    [Fact]
    public void a_hold_room_on_the_invite_is_not_a_person()
    {
        Config = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText + CatchUpsText.Replace("[catch_ups]",
            "[hold]\nrooms = [{ name = \"Room 3\", address = \"room3@example.com\" }]\n\n[catch_ups]"), "rooms.toml");
        Assert.Equal(("Sam Jones", Sam), Other(Mine("r", "Alex / Sam", "2026-11-24", "10:00", "10:30",
            [("Sam Jones", Sam, "required"), ("Room 3", "room3@example.com", "required")])));
    }

    [Fact]
    public void off_without_catch_ups()
    {
        Config = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText, "none.toml");
        Assert.Null(Config.CatchUps);
        Assert.Null(Other(CatchUp("2026-11-24")));
    }
}

public sealed class CatchUpLeave : CatchUpBase
{
    [Fact]
    public void nobody_off_nothing_happens() => Assert.Empty(Plan([]));

    [Fact]
    public void their_annual_leave_most_of_the_week_cancels_with_the_note()
    {
        var found = Plan([], AllDay("Annual Leave", "2026-11-23", "2026-11-27"));
        Assert.Equal(["sam-11-24"], found.Keys);
        var d = found["sam-11-24"];
        Assert.True(d.CatchUp);
        Assert.Equal(("cancel", "Cancelling as you're on leave - enjoy the time off"), (d.Action, d.Note));
        Assert.Contains("Sam Jones is on leave Tue 24 Nov (Sam off 5 of 5 days that week)", d.Why);
        Assert.Contains("Sam Jones (catch-up \"Alex / Sam catch-up\"): CANCEL", OneToOnePlanner.Describe(d));
    }

    [Fact]
    public void titles_only_or_free_busy_only_an_all_day_out_of_office_counts()
    {
        // No subject (only free/busy can be seen, or it's private): an all-day oof entry is leave, a busy one isn't
        var found = Plan([], AllDay("", "2026-11-23", "2026-11-25"), AllDay("", "2026-12-01", "2026-12-01", status: "busy"));
        Assert.Equal(["sam-11-24"], found.Keys);
        Assert.Equal("cancel", found["sam-11-24"].Action);
    }

    [Fact]
    public void people_first_holiday_entries_are_full_days()
    {
        // People First: "Holiday (<job title>)", 00:00 to 23:59, timed, private (so no subject when shared as titles)
        var found = Plan([], Theirs("Holiday (Engineer)", "2026-11-24", "00:00", "2026-11-24", "23:59", status: "busy"),
                         Theirs("", "2026-12-08", "00:00", "2026-12-10", "23:59"));
        Assert.Equal(("move", "cancel"), (found["sam-11-24"].Action, found["sam-12-08"].Action));
        Assert.Contains("Sam off 1 of 5 days", found["sam-11-24"].Why);
        Assert.Contains("Sam off 3 of 5 days", found["sam-12-08"].Why);   // 8-10 Dec, oof with no subject: three full days
    }

    [Fact]
    public void a_half_day_counts_only_if_it_overlaps_and_an_unknown_half_is_raised()
    {
        var found = Plan([],
            Theirs("Annual leave", "2026-11-24", "08:00", "2026-11-24", "12:00", status: "oof"),             // the morning: 13:30 is clear
            Theirs("Annual leave", "2026-12-01", "13:00", "2026-12-01", "18:00", status: "oof"),             // the afternoon: hits
            AllDay("Holiday (half day) (Engineer)", "2026-12-08", "2026-12-08"));                           // which half?
        Assert.Equal(["sam-12-01", "sam-12-08"], found.Keys.Order());
        Assert.Equal("move", found["sam-12-01"].Action);
        Assert.Contains("Sam off 0.5 of 5 days", found["sam-12-01"].Why);
        Assert.Equal("raise", found["sam-12-08"].Action);
        Assert.Contains("Sam Jones has a half day Tue 08 Dec and which half isn't known", found["sam-12-08"].Why);
    }

    [Fact]
    public void a_meeting_called_holiday_isnt_leave()
    {
        Assert.Empty(Plan([], Theirs("Holiday party", "2026-11-24", "00:00", "2026-11-25", "00:00", meeting: true)));
    }

    [Fact]
    public void the_move_uses_both_calendars()
    {
        Calendar = new FakeCalendar([.. Diary()], new() { [Sam] = [(At("2026-11-25", "13:00"), At("2026-11-25", "15:00"), "busy")] });
        Calendar.Entries[Sam] = [AllDay("Annual Leave", "2026-11-24", "2026-11-24")];
        var d = OneToOnePlanner.PlanCatchUps(Config, Calendar, Now).Decisions.Single();
        // Mon or Wed 13:30 are a day away; Wed is the later but Sam's busy then, so Monday
        Assert.Equal((At("2026-11-23", "13:30"), At("2026-11-23", "14:00")), d.To);
    }

    [Fact]
    public void their_calendar_is_read_in_chunks_getschedule_allows()
    {
        Calendar = new FakeCalendar(Diary());
        var (_, _, watched) = OneToOnePlanner.PlanCatchUps(Config, Calendar, Now, daysAhead: 90);
        Assert.Equal(["Alex / Sam catch-up (Sam Jones)", "Weekly 1-2-1 - Alex (Pat Boss)"], watched);
        Assert.Equal([Sam, Sam, "pat@example.com", "pat@example.com"], Calendar.AskedItems.Select(a => a.Address));   // once per person, in two chunks
        Assert.All(Calendar.AskedItems, a => Assert.True((a.End - a.Start).TotalDays <= 60));
        Assert.Equal(Calendar.AskedItems[0].End, Calendar.AskedItems[1].Start);
    }

    [Fact]
    public void an_unreadable_calendar_is_reported_and_only_your_leave_counts()
    {
        var meetings = Tuesdays.Select(day =>
            Mine($"u-{day[5..]}", "Alex / Uma", day, "13:30", "14:00", [("Uma Reed", "unreadable@example.com", "required")])).ToList();
        Calendar = new FakeCalendar([.. meetings, OneToOneBase.Away("l", "Annual leave", "2026-11-24", "2026-11-24")]);
        var (decisions, unreadable, _) = OneToOnePlanner.PlanCatchUps(Config, Calendar, Now);
        Assert.Equal(["Uma Reed"], unreadable);
        var d = decisions.Single();
        Assert.Equal("raise", d.Action);                                // you're off; it can't be moved without their free/busy
        Assert.Contains("couldn't read Uma Reed's calendar", d.Why);
    }
}

public sealed class CatchUpRulesOfTheWeek : CatchUpBase
{
    [Fact]
    public void your_own_leave_moves_or_cancels_with_your_note()
    {
        var found = Plan([OneToOneBase.Away("a", "Annual leave", "2026-11-24", "2026-11-24"),
                          OneToOneBase.Away("b", "Annual leave", "2026-12-07", "2026-12-09")]);
        Assert.Equal("move", found["sam-11-24"].Action);
        Assert.Contains("you're on leave Tue 24 Nov (you off 1 of 5 days that week)", found["sam-11-24"].Why);
        Assert.Equal(("cancel", "Cancelling as I'm on leave"), (found["sam-12-08"].Action, found["sam-12-08"].Note));
    }

    [Fact]
    public void a_bank_holiday_counts_for_both()
    {
        var found = Plan([OneToOneBase.Away("bh", "Founders' Day", "2026-12-01", "2026-12-01", ["Holiday"])],
                         AllDay("Annual Leave", "2026-12-02", "2026-12-03"));
        Assert.Equal(("cancel", "Cancelling for the bank holiday"), (found["sam-12-01"].Action, found["sam-12-01"].Note));   // Sam off 3 with it
        Assert.Contains("the bank holiday Tue 01 Dec", found["sam-12-01"].Why);
    }

    [Fact]
    public void cancel_days_is_its_own()
    {
        Config.CatchUps!.CancelDays = 1;
        Config.OneToOnes!.CancelDays = 5;
        var found = Plan([OneToOneBase.Away("j", "Jordan Lee on leave", "2026-11-25", "2026-11-25")], AllDay("Annual Leave", "2026-11-24", "2026-11-24"));
        Assert.Equal("cancel", found["sam-11-24"].Action);
        Assert.Equal("move", OneToOnePlanner.Plan(Config, Calendar, Now).Single(d => d.Report.Name == "Jordan Lee").Action);
    }

    [Fact]
    public void when_shorter_ask_queues_it_with_the_slot_as_a_suggestion()
    {
        Config.CatchUps!.WhenShorter = "ask";
        var found = Plan([], AllDay("Annual Leave", "2026-11-24", "2026-11-24"), AllDay("Annual Leave", "2026-12-07", "2026-12-11"));
        var d = found["sam-11-24"];
        Assert.Equal("raise", d.Action);
        Assert.Contains("under 3 days, so yours to decide (when_shorter = \"ask\")", d.Why);
        Assert.Equal("move it to Wed 25 Nov 13:30–14:00, or cancel it", d.Suggest);
        Assert.Equal("cancel", found["sam-12-08"].Action);              // a long absence still cancels
        // The 1:1s keep their own when_shorter (move)
        Calendar.Items.Add(OneToOneBase.Away("j", "Jordan Lee on leave", "2026-11-25", "2026-11-25"));
        Assert.Equal("move", OneToOnePlanner.Plan(Config, Calendar, Now).Single().Action);
    }

    [Fact]
    public void when_shorter_ask_for_the_1_1s()
    {
        Config.OneToOnes!.WhenShorter = "ask";
        Calendar = new FakeCalendar([.. OneToOneBase.Diary(), OneToOneBase.Away("j", "Jordan Lee on leave", "2026-11-25", "2026-11-25")]);
        var d = OneToOnePlanner.Plan(Config, Calendar, Now).Single();
        Assert.Equal("raise", d.Action);
        Assert.StartsWith("move it to Thu 26 Nov 15:30–16:20", d.Suggest);
    }
}

public sealed class CatchUpSettings : CatchUpBase
{
    [Fact]
    public void read_with_defaults_from_one_to_ones()
    {
        var c = Config.CatchUps!;
        Assert.Equal((true, 20L, 3.0, "move", (long?)null), (c.Act, c.MaxActions, c.CancelDays, c.WhenShorter, c.DaysAhead));
        Assert.Equal(("Cancelling as you're on leave - enjoy the time off", "Cancelling as I'm on leave", "Cancelling for the bank holiday"),
                     (c.Note, c.MyNote, c.BankHolidayNote));
        Assert.Equal([("^Mentoring", (string?)null), ("board", "drew@example.com")], c.Skip.Select(s => (s.Subject!, s.Email)));
        Assert.Equal("move", Config.OneToOnes!.WhenShorter);
        // [one_to_ones]' notes are the defaults
        var custom = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText.Replace("days_ahead = 42", "days_ahead = 42\nmy_note = \"Off, sorry\"\nwhen_shorter = \"ask\"")
                                  + "\n[catch_ups]\nnote = \"Enjoy the break\"\n", "custom.toml");
        Assert.Equal(("Enjoy the break", "Off, sorry", false, "move", "ask"),
                     (custom.CatchUps!.Note, custom.CatchUps.MyNote, custom.CatchUps.Act, custom.CatchUps.WhenShorter, custom.OneToOnes!.WhenShorter));
        // without [one_to_ones]
        Assert.NotNull(Dir.Settings(InviteSettings + "\n[catch_ups]\nact = false\n", "alone.toml").CatchUps);
    }

    [Theory]
    [InlineData("[catch_ups]\nwhen_shorter = \"later\"\n", "when_shorter in [catch_ups]")]
    [InlineData("[catch_ups]\nreport_note = \"x\"\n", "it takes act")]
    [InlineData("[catch_ups]\nact = \"yes\"\n", "act in [catch_ups]")]
    [InlineData("[catch_ups]\ncancel_days = 6\n", "cancel_days in [catch_ups]")]
    [InlineData("[catch_ups]\n[[catch_up_skip]]\nwhy = \"x\"\n", "subject (a pattern), email, or both")]
    [InlineData("[catch_ups]\n[[catch_up_skip]]\nsubject = '(unclosed'\n", "isn't a valid regular expression")]
    [InlineData("[catch_ups]\n[[catch_up_skip]]\nemail = \"not an address\"\n", "should be an address")]
    [InlineData("[[catch_up_skip]]\nemail = \"a@example.com\"\n", "need a [catch_ups] section")]
    [InlineData("[[catch_up]]\nemail = \"a@example.com\"\n", "need a [catch_ups] section")]
    [InlineData("[catch_ups]\nwhen_double_booked = \"always\"\n", "when_double_booked in [catch_ups]")]
    [InlineData("[catch_ups]\n[[catch_up]]\nemail = \"a@example.com\"\nmax_actions = 3\n", "isn't a setting")]
    [InlineData("[catch_ups]\n[[catch_up]]\nemail = \"a@example.com\"\ndouble_booked_days = -1\n", "double_booked_days in [[catch_up]] 1")]
    [InlineData("[catch_ups]\n[[catch_up]]\nwhen_shorter = \"move\"\n", "subject (a pattern), email, or both")]
    public void refused(string text, string message)
    {
        var error = Assert.Throws<SettingsException>(() => Dir.Settings(InviteSettings + "\n" + text, "bad.toml"));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void when_shorter_in_one_to_ones_is_checked()
    {
        var error = Assert.Throws<SettingsException>(() => Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText.Replace("act = true", "act = true\nwhen_shorter = \"cancel\""), "bad.toml"));
        Assert.Contains("when_shorter in [one_to_ones]", error.Message);
    }

    [Fact]
    public void it_needs_invites() =>
        Assert.Contains("needs an [invites] section", Assert.Throws<SettingsException>(() => Dir.Settings($"email = \"{Me}\"\n[[folder]]\nname = \"X\"\n[catch_ups]\n", "bad.toml")).Message);
}

/// <summary>RunCatchUps(): acting, recording, queueing, separately from the 1:1s.</summary>
public sealed class CatchUpRuns : CatchUpBase
{
    private (List<JsonObject> Items, List<JsonObject> Acted, JsonObject Raised) RunWith(List<JsonObject> extra, ScheduleItem[] sams, JsonObject? raised = null)
    {
        Calendar = new FakeCalendar([.. Diary(), .. extra]);
        Calendar.Entries[Sam] = [.. sams];
        raised ??= [];
        var (items, acted, _) = OneToOnePlanner.RunCatchUps(Config, Calendar, raised, Quiet, now: Now);
        return (items, acted, raised);
    }

    private static readonly ScheduleItem[] Leave = [AllDay("Annual Leave", "2026-11-23", "2026-11-27"), AllDay("Annual Leave", "2026-12-01", "2026-12-01")];

    [Fact]
    public void act_cancels_and_moves_the_occurrence_only()
    {
        var (items, acted, _) = RunWith([], Leave);
        Assert.Empty(items);
        Assert.Equal([("sam-11-24", "Cancelling as you're on leave - enjoy the time off")], Calendar.Cancels);
        Assert.Equal([("sam-12-01", At("2026-12-02", "13:30"), At("2026-12-02", "14:00"))], Calendar.Moves);
        Assert.Equal([("catch-up cancelled: leave", "Sam Jones"), ("catch-up moved: leave", "Sam Jones")], acted.Select(a => (a.Req("rule"), a.Req("from"))));
    }

    [Fact]
    public void its_own_act_and_max_actions()
    {
        Config.CatchUps!.Act = false;
        var (_, acted, raised) = RunWith([], Leave);
        Assert.Equal((0, 0), (Calendar.Cancels.Count, Calendar.Moves.Count));
        Assert.Equal(["catch-up would be cancelled: leave (act = false)", "catch-up would be moved: leave (act = false)"], acted.Select(a => a.Req("rule")));
        Assert.All(raised.Select(k => k.Key), k => Assert.StartsWith("catch-up-would:", k));
        Assert.Empty(RunWith([], Leave, raised).Acted);                     // recorded once
        Config.CatchUps.Act = true;
        Config.CatchUps.MaxActions = 1;
        Assert.Single(RunWith([], Leave).Acted);
        Assert.Single(Calendar.Cancels);
    }

    [Fact]
    public void what_isnt_settled_is_queued_once()
    {
        Config.CatchUps!.WhenShorter = "ask";
        var (items, acted, raised) = RunWith([], [Leave[1]]);
        Assert.Empty(acted);
        Assert.Equal([("one-to-one", "sam-12-01", "Sam Jones", "catch-up and leave")], items.Select(i => (i.Req("type"), i.Req("id"), i.Req("from"), i.Req("rule"))));
        Assert.StartsWith("catch-up:sam-12-01@", items[0].Req("key"));
        Assert.Empty(RunWith([], [Leave[1]], raised).Items);
    }

    [Fact]
    public void a_change_whose_answer_was_lost_is_never_made_again()
    {
        Calendar = new FakeCalendar(Diary());
        Calendar.Entries[Sam] = [Leave[0]];
        Calendar.Lost.Add("sam-11-24");
        var raised = new JsonObject();
        var (_, acted, _) = OneToOnePlanner.RunCatchUps(Config, Calendar, raised, Quiet, now: Now);
        Assert.Equal(Unconfirmed.Prefix + "catch-up cancelled: leave", acted.Single().Req("rule"));
        Assert.Contains(raised, k => k.Key.StartsWith("catch-up-unconfirmed:sam-11-24@", StringComparison.Ordinal));
        Calendar.Cancels.Clear();
        OneToOnePlanner.RunCatchUps(Config, Calendar, raised, Quiet, now: Now);
        Assert.Empty(Calendar.Cancels);
    }
}

/// <summary>Through Triage.Run: catch-ups alongside 1:1s, and kept out of cancel_days_off.</summary>
public sealed class CatchUpsInARun : CatchUpBase
{
    [Fact]
    public void recorded_for_deleted_and_not_cancelled_twice_on_your_days_off()
    {
        Config.Invites!.CancelDaysOff = true;
        var calendar = new FakeCalendar([.. Diary(), OneToOneBase.Away("a", "Annual leave", "2026-11-24", "2026-11-24"), .. Runs.Leave()]);
        calendar.Entries[Sam] = [AllDay("Annual Leave", "2026-12-01", "2026-12-04")];
        using (Clock.Use(new FixedTime(Now)))
            Triage.Run(Config, new Mailbox(new FakeGraph([]), WriteDoor.Refusing().Mail), calendar, false, Quiet);
        // Your one day off: the catch-up is moved ([catch_ups]), not cancelled by cancel_days_off; Sam's four days: cancelled
        Assert.Equal([("sam-11-24", At("2026-11-25", "13:30"), At("2026-11-25", "14:00"))], calendar.Moves.Where(m => m.Id.StartsWith("sam", StringComparison.Ordinal)));
        Assert.Equal([("sam-12-01", "Cancelling as you're on leave - enjoy the time off")], calendar.Cancels.Where(c => c.Id.StartsWith("sam", StringComparison.Ordinal)));
        var gone = Triage.Deleted(Config).Select(g => (g.Req("rule"), g.Req("from"))).ToList();
        Assert.Contains(("catch-up moved: leave", "Sam Jones"), gone);
        Assert.Contains(("catch-up cancelled: leave", "Sam Jones"), gone);
        Assert.Contains(("1:1 cancelled: leave", "Jordan Lee"), gone);
    }

    [Fact]
    public void without_catch_ups_cancel_days_off_still_cancels_them()
    {
        Config.Invites!.CancelDaysOff = true;
        Config.CatchUps = null;
        var calendar = new FakeCalendar([.. Diary(), OneToOneBase.Away("a", "Annual leave", "2026-11-24", "2026-11-24")]);
        using (Clock.Use(new FixedTime(Now)))
            Triage.Run(Config, new Mailbox(new FakeGraph([]), WriteDoor.Refusing().Mail), calendar, false, Quiet);
        Assert.Equal([("sam-11-24", "Cancelling as I'm on leave")], calendar.Cancels);
    }
}

public sealed class ScheduleItemReading
{
    [Fact]
    public void subject_privacy_and_meeting_from_getschedule()
    {
        var item = Calendar.ReadScheduleItem(new JsonObject
        {
            ["status"] = "oof", ["subject"] = "Annual Leave", ["isPrivate"] = false, ["isMeeting"] = false,
            ["start"] = new JsonObject { ["dateTime"] = "2026-11-23T00:00:00.0000000", ["timeZone"] = "GMT Standard Time" },
            ["end"] = new JsonObject { ["dateTime"] = "2026-11-28T00:00:00.0000000", ["timeZone"] = "GMT Standard Time" },
        });
        Assert.Equal(new ScheduleItem(At("2026-11-23", "00:00"), At("2026-11-28", "00:00"), "oof", "Annual Leave", false, false), item);
        var hidden = Calendar.ReadScheduleItem(new JsonObject
        {
            ["status"] = "busy", ["isPrivate"] = true,
            ["start"] = new JsonObject { ["dateTime"] = "2026-11-23T09:00:00.0000000" }, ["end"] = new JsonObject { ["dateTime"] = "2026-11-23T10:00:00.0000000" },
        });
        Assert.Equal(("", true, false), (hidden.Subject, hidden.IsPrivate, hidden.IsMeeting));
    }
}

/// <summary>Alex's manager's 1:1 ("Weekly 1-2-1 - Alex", with Pat, protected): a catch-up of its own, with a [[catch_up]]
/// that sets everything, including when_double_booked = "move".</summary>
public abstract class ManagerBase : CatchUpBase
{
    public const string Pat = "pat@example.com";

    public const string PatEntry = """

        [[catch_up]]
        subject = '^Weekly 1-2-1 - Alex$'
        email = "pat@example.com"
        act = true
        cancel_days = 3
        when_shorter = "move"
        note = "Cancelling as you're on leave - enjoy the time off"
        my_note = "Cancelling as I'm on leave"
        bank_holiday_note = "Cancelling for the bank holiday"
        when_double_booked = "move"
        double_booked_note = "Moved as you've got a clash"
        double_booked_days = 7
        max_moves_per_occurrence = 2

        """;

    protected ManagerBase() => Config = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText + CatchUpsText + PatEntry, "manager.toml");

    /// <summary>The plan for Pat's 1:1s, with these entries in Pat's calendar (as free/busy shows them, subjects visible).</summary>
    protected Dictionary<string, Decision> PlanPat(List<JsonObject>? extra = null, params ScheduleItem[] pats)
    {
        Calendar = new FakeCalendar([.. Diary(), .. extra ?? []]);
        Calendar.Entries[Pat] = [.. pats];
        return OneToOnePlanner.PlanCatchUps(Config, Calendar, Now).Decisions.Where(d => d.Report.Email == Pat).ToDictionary(d => d.Meeting.Req("id"));
    }

    /// <summary>Pat's own copy of the 1:1, as it sits in Pat's calendar.</summary>
    protected static ScheduleItem PatsCopy(string day) => Theirs("Weekly 1-2-1 - Alex", day, "10:00", day, "10:30", status: "busy", meeting: true);

    protected Settings With(string from, string to, string name) =>
        Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText + CatchUpsText + PatEntry.Replace(from, to), name);
}

public sealed class ManagersOneToOne : ManagerBase
{
    [Fact]
    public void protected_is_still_fixed_for_other_meetings_but_handled_as_a_catch_up()
    {
        var boss = Diary().First(e => e.Req("id") == "boss-11-18");
        Assert.Equal("protected", InviteTriage.Commitments(Config, [boss]).Single().Kind);   // never moved to make room for an invite
        Assert.Equal(("Pat Boss", Pat), CatchUpRules.Other(Config, boss));
        var found = PlanPat(null, AllDay("Annual Leave", "2026-11-16", "2026-11-18"));
        Assert.Equal(("cancel", "Cancelling as you're on leave - enjoy the time off"), (found["boss-11-18"].Action, found["boss-11-18"].Note));
        found = PlanPat(null, AllDay("Annual Leave", "2026-11-18", "2026-11-18"));
        Assert.Equal("move", found["boss-11-18"].Action);                                      // a day: moved
    }

    [Fact]
    public void an_entry_wins_over_the_group_and_what_it_leaves_out_comes_from_the_group()
    {
        var pat = Config.CatchUps!.Entries.Single().Handling;
        Assert.Equal((true, 3.0, "move", "move", "Moved as you've got a clash", 7L, 2L),
                     (pat.Act, pat.CancelDays, pat.WhenShorter, pat.DoubleBooked.When, pat.DoubleBooked.Note, pat.DoubleBooked.Days, pat.DoubleBooked.MaxMoves));
        // Different [catch_ups] and [one_to_ones]: Pat's entry, which sets everything, is the same
        var other = Dir.Settings(InviteSettings
            + OneToOneBase.OneToOnesText.Replace("act = true", "act = false\ncancel_days = 1\nwhen_shorter = \"ask\"\nreport_note = \"x\"\nmy_note = \"y\"\nwhen_double_booked = \"ask\"")
            + CatchUpsText.Replace("act = true\ncancel_days = 3", "act = false\ncancel_days = 5\nwhen_shorter = \"ask\"\nnote = \"n\"\nmy_note = \"m\"\nbank_holiday_note = \"b\"\n"
                                   + "when_double_booked = \"ask\"\ndouble_booked_note = \"d\"\ndouble_booked_days = 1\nmax_moves_per_occurrence = 9")
            + PatEntry, "other.toml");
        Assert.Equal(pat, other.CatchUps!.Entries.Single().Handling);
        Assert.Equal((false, 5.0, "ask", "n", "ask"), (other.CatchUps.Act, other.CatchUps.CancelDays, other.CatchUps.WhenShorter, other.CatchUps.Note,
                     other.CatchUps.DoubleBooked.When));
        // An entry that sets only one thing takes the rest from [catch_ups]
        var partial = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText
            + CatchUpsText.Replace("cancel_days = 3", "cancel_days = 2\nnote = \"group note\"")
            + "\n[[catch_up]]\nemail = \"pat@example.com\"\nwhen_double_booked = \"ask\"\n", "partial.toml").CatchUps!.Entries.Single().Handling;
        Assert.Equal((true, 2.0, "group note", "ask", 7L), (partial.Act, partial.CancelDays, partial.TheirNote, partial.DoubleBooked.When, partial.DoubleBooked.Days));
    }

    [Fact]
    public void an_entrys_act_is_its_own()
    {
        Config = With("act = true", "act = false", "quiet.toml");
        Calendar = new FakeCalendar(Diary());
        Calendar.Entries[Pat] = [AllDay("Annual Leave", "2026-11-16", "2026-11-20")];
        Calendar.Entries[Sam] = [AllDay("Annual Leave", "2026-11-16", "2026-11-20")];
        var (_, acted, _) = OneToOnePlanner.RunCatchUps(Config, Calendar, [], Quiet, now: Now);
        Assert.Equal([("Alex / Sam catch-up", "catch-up cancelled: leave"), ("Weekly 1-2-1 - Alex", "catch-up would be cancelled: leave (act = false)")],
                     acted.Select(a => (a.Req("subject").Split(':')[0], a.Req("rule"))));
        Assert.Equal(["sam-11-17"], Calendar.Cancels.Select(c => c.Id));
    }
}

public sealed class DoubleBooked : ManagerBase
{
    private static ScheduleItem Board(string day, string status = "busy") => Theirs("Board", day, "10:00", day, "11:00", status: status);

    [Fact]
    public void busy_or_out_of_office_over_it_moves_it_with_the_note()
    {
        var d = PlanPat(null, PatsCopy("2026-11-18"), Board("2026-11-18"))["boss-11-18"];
        Assert.Equal(("move", true, "Moved as you've got a clash"), (d.Action, d.DoubleBooked, d.Note));
        Assert.Contains("Pat Boss is double-booked (busy \"Board\" 10:00–11:00)", d.Why);
        // The same time on the nearest day, Tue or Thu: the later
        Assert.Equal((At("2026-11-19", "10:00"), At("2026-11-19", "10:30")), d.To);
        Assert.Contains("note: \"Moved as you've got a clash\"", OneToOnePlanner.Describe(d));
        // Out of office for an hour (a dentist) counts too; titles-only (no subject) as well
        Assert.Equal("move", PlanPat(null, Theirs("", "2026-11-18", "09:30", "2026-11-18", "10:15", status: "oof"))["boss-11-18"].Action);
    }

    [Fact]
    public void tentative_free_and_the_meeting_itself_dont_count()
    {
        Assert.Empty(PlanPat(null, PatsCopy("2026-11-18"), Board("2026-11-18", "tentative"),
            Theirs("Reminder", "2026-11-18", "00:00", "2026-11-19", "00:00", status: "free"),
            Theirs("Before", "2026-11-18", "09:00", "2026-11-18", "10:00", status: "busy")));      // touching, not overlapping
        // Something else at exactly the same time is still a clash
        var d = PlanPat(null, PatsCopy("2026-11-18"), Theirs("Exec", "2026-11-18", "10:00", "2026-11-18", "10:30", status: "busy"))["boss-11-18"];
        Assert.Equal("move", d.Action);
        Assert.Contains("\"Exec\"", d.Why);
    }

    [Fact]
    public void a_decline_is_a_clash()
    {
        var declined = Diary().First(e => e.Req("id") == "boss-11-18");
        ((JsonObject)declined["attendees"]![1]!)["status"] = new JsonObject { ["response"] = "declined" };
        Calendar = new FakeCalendar([.. Diary().Where(e => e.Req("id") != "boss-11-18"), declined]);
        var d = OneToOnePlanner.PlanCatchUps(Config, Calendar, Now).Decisions.Single(x => x.Meeting.Req("id") == "boss-11-18");
        Assert.Equal("move", d.Action);
        Assert.Contains("Pat Boss has declined it", d.Why);
    }

    [Fact]
    public void only_within_double_booked_days()
    {
        // Wed 25 Nov is 13 days away: left to sort itself out
        Assert.Empty(PlanPat(null, Board("2026-11-25")));
        Config = With("double_booked_days = 7", "double_booked_days = 14", "wide.toml");
        Assert.Equal("move", PlanPat(null, Board("2026-11-25"))["boss-11-25"].Action);
    }

    [Fact]
    public void off_by_default_and_leave_comes_first()
    {
        // Sam's catch-ups take [catch_ups]' when_double_booked: "ignore"
        Calendar = new FakeCalendar(Diary());
        Calendar.Entries[Sam] = [Theirs("Board", "2026-11-17", "13:00", "2026-11-17", "15:00", status: "busy")];
        Assert.Empty(OneToOnePlanner.PlanCatchUps(Config, Calendar, Now).Decisions);
        // Pat off three days and busy: cancelled for the leave
        var d = PlanPat(null, AllDay("Annual Leave", "2026-11-16", "2026-11-18"), Board("2026-11-18"))["boss-11-18"];
        Assert.Equal(("cancel", false), (d.Action, d.DoubleBooked));
    }

    [Fact]
    public void ask_and_no_slot_are_queued()
    {
        Config = With("when_double_booked = \"move\"", "when_double_booked = \"ask\"", "ask.toml");
        var d = PlanPat(null, Board("2026-11-18"))["boss-11-18"];
        Assert.Equal("raise", d.Action);
        Assert.Contains("yours to decide (when_double_booked = \"ask\")", d.Why);
        Assert.Equal("move it to Thu 19 Nov 10:00–10:30, or leave it", d.Suggest);
        var (items, _, _) = OneToOnePlanner.RunCatchUps(Config, Calendar, [], Quiet, now: Now);
        Assert.Equal("catch-up and a clash", items.Single(i => i.Req("id") == "boss-11-18").Req("rule"));
        // "move" with no free slot that week: queued too
        Config = With("act = true", "act = true", "full.toml");
        var full = new[] { 16, 17, 18, 19, 20 }.Select(n => Ev($"w{n}", "Workshop", $"2026-11-{n}", "08:00", "18:00", response: "accepted")).ToList();
        Assert.Contains("no time that week when you're both free", PlanPat(full, Board("2026-11-18"))["boss-11-18"].Why);
    }

    [Fact]
    public void the_1_1s_can_have_it_too()
    {
        Config = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText.Replace("act = true", "act = true\nwhen_double_booked = \"move\""), "ones.toml");
        Calendar = new FakeCalendar(OneToOneBase.Diary());
        Calendar.Entries["jordan@example.com"] = [Theirs("Offsite", "2026-11-18", "15:00", "2026-11-18", "17:00", status: "busy")];
        var d = OneToOnePlanner.Plan(Config, Calendar, Now).Single();
        Assert.Equal(("jordan-11-18", "move", true), (d.Meeting.Req("id"), d.Action, d.DoubleBooked));
    }

    [Fact]
    public void moved_at_most_max_moves_per_occurrence_then_asked()
    {
        var raised = new JsonObject();
        JsonObject Run()
        {
            Calendar = new FakeCalendar(Diary());
            Calendar.Entries[Pat] = [Board("2026-11-18")];
            var (items, acted, _) = OneToOnePlanner.RunCatchUps(Config, Calendar, raised, Quiet, now: Now);
            return items.FirstOrDefault(i => i.Req("id") == "boss-11-18")
                   ?? acted.First(a => a.Req("subject").StartsWith("Weekly 1-2-1 - Alex", StringComparison.Ordinal));
        }
        Assert.Equal("catch-up moved: double-booked", Run().Req("rule"));
        Assert.Equal(("boss-11-18", "Moved as you've got a clash"), (Calendar.Moves.Single().Id, Calendar.MoveNotes["boss-11-18"]));
        Assert.Equal("catch-up moved: double-booked", Run().Req("rule"));    // the new time clashed too (the fake doesn't move it)
        var asked = Run();
        Assert.Equal("catch-up and a clash", asked.Req("rule"));
        Assert.Contains("moved for a clash 2 time(s) already (max_moves_per_occurrence)", asked.Req("check"));
        Assert.Empty(Calendar.Moves);
    }
}

public sealed class MoveNote
{
    [Fact]
    public void the_note_goes_on_top_of_the_description_once()
    {
        var once = Calendar.WithNote("html", "<html><body class=\"x\"><p>Agenda</p></body></html>", "Moved as you've got a clash");
        Assert.Equal("<html><body class=\"x\"><p>Moved as you&#39;ve got a clash</p><p>Agenda</p></body></html>", once.Req("content"));
        Assert.Equal(once.Req("content"), Calendar.WithNote("html", once.Req("content"), "Moved as you've got a clash").Req("content"));
        Assert.Equal("Moved\n\nAgenda", Calendar.WithNote("text", "Agenda", "Moved").Req("content"));
    }

    [Fact]
    public void a_move_with_a_note_sends_the_description_in_the_same_update()
    {
        var door = WriteDoor.Journaling();
        var graph = new FakeGraph([], "<p>Agenda</p>");
        new Calendar(graph, door.Calendar).Move("e1", At("2026-11-19", "10:00"), At("2026-11-19", "10:30"), "Moved");
        var call = door.Journal.Single();
        Assert.Equal(("PATCH", "/me/events/e1"), (call.Method, call.Url));
        Assert.Equal("<p>Moved</p><p>Agenda</p>", call.Body!["body"]!["content"]!.GetValue<string>());
        new Calendar(graph, door.Calendar).Move("e2", At("2026-11-19", "10:00"), At("2026-11-19", "10:30"));
        Assert.Null(door.Journal[1].Body!["body"]);
    }
}
