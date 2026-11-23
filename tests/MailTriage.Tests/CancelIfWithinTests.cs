using System.Text.Json.Nodes;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

// cancel_if_within_working_days (1.27.0): a 1:1 or catch-up that would be moved (for leave or a clash) to within that many
// working days after the occurrence before it is cancelled instead. "Now" is Thu 12 Nov 2026, 09:00. Pat's 1:1 is
// Wednesdays 10:00-10:30 from 18 Nov; moved off Wed 18 Nov, it goes to Thu 19 Nov 10:00.

public sealed class WorkingDaysCount
{
    private static readonly HashSet<DateOnly> None = [];

    [Theory]
    [InlineData("2026-11-13", "2026-11-18", 3)]      // Fri to Wed: Mon, Tue, Wed
    [InlineData("2026-11-13", "2026-11-19", 4)]      // Fri to Thu
    [InlineData("2026-11-16", "2026-11-19", 3)]      // Mon to Thu
    [InlineData("2026-11-18", "2026-11-18", 0)]      // the same day
    [InlineData("2026-11-19", "2026-11-18", 0)]      // before it
    [InlineData("2026-11-13", "2026-11-16", 1)]      // a weekend in between doesn't count
    public void after_the_first_up_to_and_including_the_second_weekdays_only(string from, string to, int count) =>
        Assert.Equal(count, OneToOnePlanner.WorkingDaysAfter(DateOnly.Parse(from), DateOnly.Parse(to), None));

    [Fact]
    public void bank_holidays_dont_count() =>
        Assert.Equal(2, OneToOnePlanner.WorkingDaysAfter(new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 18), new HashSet<DateOnly> { new(2026, 11, 16) }));
}

public sealed class CancelIfWithin : ManagerBase
{
    private const string On = "max_moves_per_occurrence = 2\ncancel_if_within_working_days = 3";

    public CancelIfWithin() => Config = With("max_moves_per_occurrence = 2", On, "within.toml");

    /// <summary>An occurrence of Pat's 1:1 series; an exception when it was moved.</summary>
    private static JsonObject Boss(string id, string day, string start = "10:00", string end = "10:30", string type = "exception") =>
        Ev(id, "Weekly 1-2-1 - Alex", day, start, end, organiser: "Alex Smith", orgAddr: Me,
           attendees: [("Alex Smith", Me, "required"), ("Pat Boss", Pat, "required")], type: type, seriesMasterId: "boss-series");

    /// <summary>The occurrence before Wed 18 Nov (Wed 11 Nov's), moved to this day.</summary>
    private static JsonObject Before(string day) => Boss("boss-11-11", day);

    private static ScheduleItem Board(string day) => Theirs("Board", day, "10:00", day, "11:00", status: "busy");

    [Fact]
    public void the_boundary_n_cancels_n_plus_one_moves()
    {
        // Previous moved to Mon 16 Nov: Thu 19 is 3 working days after it, so cancelled, with the clash note
        var d = PlanPat([Before("2026-11-16")], Board("2026-11-18"))["boss-11-18"];
        Assert.Equal(("cancel", true, "Cancelling this week as you've got a clash - we'll pick up next week", (ValueTuple<DateTime, DateTime>?)null),
                     (d.Action, d.DoubleBooked, d.Note, d.To));
        Assert.Contains("Pat Boss is double-booked", d.Why);
        Assert.Contains("moving it to Thu 19 Nov 10:00 would be 3 working days after the last one (Mon 16 Nov), so cancelled instead "
                        + "(cancel_if_within_working_days = 3)", d.Why);
        Assert.Contains(": CANCEL (", OneToOnePlanner.Describe(d));
        // Previous moved to Fri 13 Nov: 4 working days, so moved as before
        d = PlanPat([Before("2026-11-13")], Board("2026-11-18"))["boss-11-18"];
        Assert.Equal(("move", (At("2026-11-19", "10:00"), At("2026-11-19", "10:30"))), (d.Action, d.To!.Value));
        Assert.DoesNotContain("working days", d.Why);
        // ...and with 4, that one is cancelled too
        Config = With("max_moves_per_occurrence = 2", "max_moves_per_occurrence = 2\ncancel_if_within_working_days = 4", "four.toml");
        Assert.Equal("cancel", PlanPat([Before("2026-11-13")], Board("2026-11-18"))["boss-11-18"].Action);
    }

    [Fact]
    public void their_leave_cancels_with_note()
    {
        var d = PlanPat([Before("2026-11-16")], AllDay("Annual Leave", "2026-11-18", "2026-11-18"))["boss-11-18"];
        Assert.Equal(("cancel", false, "Cancelling as you're on leave - enjoy the time off"), (d.Action, d.DoubleBooked, d.Note));
        Assert.Contains("Pat Boss is on leave Wed 18 Nov (Pat off 1 of 5 days that week); moving it to Thu 19 Nov 10:00 would be 3 working days", d.Why);
    }

    [Fact]
    public void your_leave_cancels_with_my_note()
    {
        var d = PlanPat([Before("2026-11-16"), OneToOneBase.Away("a", "Annual leave", "2026-11-18", "2026-11-18")])["boss-11-18"];
        Assert.Equal(("cancel", "Cancelling as I'm on leave"), (d.Action, d.Note));
        Assert.Contains("you're on leave Wed 18 Nov", d.Why);
    }

    [Fact]
    public void double_booked_cancel_note_is_a_setting_with_the_usual_precedence()
    {
        Config = With("max_moves_per_occurrence = 2", On + "\ndouble_booked_cancel_note = \"Skipping this one\"", "note.toml");
        Assert.Equal("Skipping this one", PlanPat([Before("2026-11-16")], Board("2026-11-18"))["boss-11-18"].Note);
        // Set on [catch_ups] only: the entry takes it
        var group = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText + CatchUpsText.Replace("cancel_days = 3", "cancel_days = 3\ndouble_booked_cancel_note = \"Group's\"")
                                 + PatEntry.Replace("max_moves_per_occurrence = 2", On), "group-note.toml");
        Assert.Equal(("Group's", "Group's"), (group.CatchUps!.DoubleBooked.CancelNote, group.CatchUps.Entries.Single().Handling.DoubleBooked.CancelNote));
        Assert.Equal(DoubleBookedSettings.DefaultCancelNote, Config.OneToOnes!.DoubleBooked.CancelNote);
    }

    [Fact]
    public void no_occurrence_before_it_moves_as_normal()
    {
        // The diary's first occurrence is Wed 18 Nov: nothing before it to be too close to
        Assert.Equal("move", PlanPat(null, Board("2026-11-18"))["boss-11-18"].Action);
        // Another series' meeting just before it isn't the one before it
        var other = Before("2026-11-17");
        other["seriesMasterId"] = "another-series";
        Assert.Equal("move", PlanPat([other], Board("2026-11-18"))["boss-11-18"].Action);
    }

    [Fact]
    public void a_cancelled_occurrence_before_it_doesnt_count_but_the_one_before_that_does()
    {
        var cancelled = Before("2026-11-16");
        cancelled["isCancelled"] = true;
        Assert.Equal("move", PlanPat([cancelled, Boss("boss-11-04", "2026-11-11")], Board("2026-11-18"))["boss-11-18"].Action);   // Wed 11: 6 working days
        Assert.Equal("cancel", PlanPat([cancelled, Boss("boss-11-04", "2026-11-17", "09:00", "09:30")], Board("2026-11-18"))["boss-11-18"].Action);
    }

    [Fact]
    public void the_previous_occurrence_is_where_it_was_moved_to()
    {
        // Wed 11 Nov's occurrence, left where it was: 6 working days before Thu 19, moved
        Assert.Equal("move", PlanPat([Boss("boss-11-11", "2026-11-11", type: "occurrence")], Board("2026-11-18"))["boss-11-18"].Action);
        // The same occurrence moved to Tue 17 Nov: 2 working days, cancelled
        var d = PlanPat([Before("2026-11-17")], Board("2026-11-18"))["boss-11-18"];
        Assert.Equal("cancel", d.Action);
        Assert.Contains("would be 2 working days after the last one (Tue 17 Nov)", d.Why);
    }

    [Fact]
    public void a_bank_holiday_in_the_gap_isnt_a_working_day()
    {
        // Fri 13 to Thu 19 is 4 working days, but Mon 16 is a bank holiday: 3, so cancelled
        var d = PlanPat([Before("2026-11-13"), OneToOneBase.Away("bh", "Founders' Day", "2026-11-16", "2026-11-16", ["Holiday"])], Board("2026-11-18"))["boss-11-18"];
        Assert.Equal("cancel", d.Action);
        Assert.Contains("would be 3 working days after the last one (Fri 13 Nov)", d.Why);
    }

    [Fact]
    public void off_by_default()
    {
        Config = With("act = true", "act = true", "default.toml");
        Assert.Equal(0, Config.CatchUps!.Entries.Single().Handling.CancelIfWithinWorkingDays);
        Assert.Equal("move", PlanPat([Before("2026-11-17")], Board("2026-11-18"))["boss-11-18"].Action);
    }

    [Fact]
    public void a_run_cancels_through_the_cancel_path_once_and_says_why()
    {
        var raised = new JsonObject();
        var log = new List<string>();
        Calendar = new FakeCalendar([.. Diary(), Before("2026-11-16")]);
        Calendar.Entries[Pat] = [Board("2026-11-18")];
        var (_, acted, _) = OneToOnePlanner.RunCatchUps(Config, Calendar, raised, log.Add, now: Now);
        Assert.Equal([("boss-11-18", "Cancelling this week as you've got a clash - we'll pick up next week")], Calendar.Cancels);
        Assert.Empty(Calendar.Moves);
        var entry = acted.Single(a => a.Req("subject").StartsWith("Weekly 1-2-1 - Alex", StringComparison.Ordinal));
        Assert.Equal("catch-up cancelled: double-booked", entry.Req("rule"));
        Assert.Contains("would be 3 working days after the last one", entry.Req("why"));
        Assert.Contains(log, l => l.StartsWith("cancelled  [catch-up and double-booked]", StringComparison.Ordinal) && l.Contains("so cancelled instead"));
        Assert.DoesNotContain(raised, k => k.Key.StartsWith("catch-up-clash-moves:", StringComparison.Ordinal));   // not a move
        // Lost: never sent again
        Calendar = new FakeCalendar([.. Diary(), Before("2026-11-16")]);
        Calendar.Entries[Pat] = [Board("2026-11-18")];
        Calendar.Lost.Add("boss-11-18");
        raised = [];
        OneToOnePlanner.RunCatchUps(Config, Calendar, raised, Quiet, now: Now);
        Calendar.Cancels.Clear();
        OneToOnePlanner.RunCatchUps(Config, Calendar, raised, Quiet, now: Now);
        Assert.Empty(Calendar.Cancels);
    }
}

public sealed class CancelIfWithinPrecedence : CatchUpBase
{
    [Fact]
    public void an_entry_wins_then_catch_ups_and_one_to_ones_isnt_inherited()
    {
        var group = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText.Replace("days_ahead = 42", "days_ahead = 42\ncancel_if_within_working_days = 5")
                                 + CatchUpsText.Replace("cancel_days = 3", "cancel_days = 3\ncancel_if_within_working_days = 2")
                                 + "\n[[catch_up]]\nemail = \"pat@example.com\"\ncancel_if_within_working_days = 0\n"
                                 + "\n[[catch_up]]\nemail = \"drew@example.com\"\ncancel_if_within_working_days = 3\n"
                                 + "\n[[catch_up]]\nemail = \"kim@example.com\"\nwhen_shorter = \"ask\"\n", "precedence.toml");
        Assert.Equal((5L, 2L), (group.OneToOnes!.CancelIfWithinWorkingDays, group.CatchUps!.CancelIfWithinWorkingDays));
        Assert.Equal([0L, 3L, 2L], group.CatchUps.Entries.Select(e => e.Handling.CancelIfWithinWorkingDays));
        // [catch_ups] doesn't take [one_to_ones]'
        var alone = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText.Replace("days_ahead = 42", "days_ahead = 42\ncancel_if_within_working_days = 5")
                                 + CatchUpsText, "alone.toml");
        Assert.Equal(0L, alone.CatchUps!.CancelIfWithinWorkingDays);
    }

    [Fact]
    public void a_one_to_one_entry_wins_over_one_to_ones()
    {
        var config = Dir.Settings(InviteSettings + OneToOneBase.OneToOnesText
            .Replace("days_ahead = 42", "days_ahead = 42\ncancel_if_within_working_days = 3")
            .Replace("email = \"jordan@example.com\"", "email = \"jordan@example.com\"\ncancel_if_within_working_days = 0"), "ones.toml");
        Assert.Equal([0L, 3L], config.OneToOnes!.Reports.Take(2).Select(r => config.OneToOnes.HandlingFor(r).CancelIfWithinWorkingDays));
        // Both off Wed 25 Nov, both moved to Thu 26; both last met Mon 23 (their Wed 18 occurrences, moved): 3 working days
        static JsonObject Moved(string who, string email, string start, string end)
        {
            var e = OneToOneBase.OneToOne(who, email, "2026-11-23", start, end);
            e["id"] = $"{who.ToLowerInvariant()}-11-18";
            e["type"] = "exception";
            return e;
        }
        var diary = OneToOneBase.Diary().Where(e => e.Req("id") is not ("jordan-11-18" or "robin-11-18")).ToList();
        Calendar = new FakeCalendar([.. diary, Moved("Jordan", "jordan@example.com", "15:30", "16:20"), Moved("Robin", "robin@example.com", "11:00", "11:50"),
            OneToOneBase.Away("j", "Jordan Lee on leave", "2026-11-25", "2026-11-25"), OneToOneBase.Away("r", "Robin Hall on leave", "2026-11-25", "2026-11-25")]);
        var found = OneToOnePlanner.Plan(config, Calendar, Now).ToDictionary(d => d.Meeting.Req("id"));
        Assert.Equal(("move", "cancel"), (found["jordan-11-25"].Action, found["robin-11-25"].Action));
        Assert.Equal("Cancelling as you're on leave - enjoy the time off", found["robin-11-25"].Note);
        Assert.Contains("would be 3 working days after the last one (Mon 23 Nov)", found["robin-11-25"].Why);
    }

    [Theory]
    [InlineData("[catch_ups]\ncancel_if_within_working_days = -1\n", "cancel_if_within_working_days in [catch_ups]")]
    [InlineData("[catch_ups]\ncancel_if_within_working_days = 11\n", "from 0 (off) to 10")]
    [InlineData("[catch_ups]\ncancel_if_within_working_days = \"3\"\n", "cancel_if_within_working_days in [catch_ups]")]
    [InlineData("[catch_ups]\n[[catch_up]]\nemail = \"a@example.com\"\ncancel_if_within_working_days = 1.5\n", "cancel_if_within_working_days in [[catch_up]] 1")]
    [InlineData("[catch_ups]\ndouble_booked_cancel_note = 3\n", "double_booked_cancel_note in [catch_ups] in")]
    [InlineData("[one_to_ones]\ncancel_if_within_working_days = 20\n", "cancel_if_within_working_days in [one_to_ones]")]
    [InlineData("[one_to_ones]\n[[one_to_one]]\nname = \"Sam Jones\"\nemail = \"sam@example.com\"\ncancel_if_within_working_days = -2\n",
                "cancel_if_within_working_days in the [[one_to_one]] for Sam Jones")]
    public void refused(string text, string message)
    {
        var error = Assert.Throws<SettingsException>(() => Dir.Settings(InviteSettings + "\n" + text, "bad.toml"));
        Assert.Contains(message, error.Message);
    }
}
