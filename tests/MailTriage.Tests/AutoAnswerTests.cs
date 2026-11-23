using System.Text.Json.Nodes;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

// 1.11.0: invites answered without asking (auto_accept, auto_move_blocks, decline_days_off), meetings on days off
// declined, and double bookings raised. "Now" is Wed 11 Nov 2026, 09:00.
public sealed class AutoAnswers : IDisposable
{
    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly DateTime now = new(2026, 11, 11, 9, 0, 0);

    public AutoAnswers()
    {
        config = dir.Settings(InviteSettings);
        var invites = config.Invites!;
        invites.AutoAccept = true;
        invites.AutoMoveBlocks = true;
        invites.DeclineDaysOff = true;
        invites.MonthsAhead = 3;
        config.Leave = config.Leave with { BankHolidayCategories = ["Bank holiday"] };   // [leave] (1.17.0)
    }

    public void Dispose() => dir.Dispose();

    private static readonly (string, string, string)[] Two = [("Alex Smith", Me, "required"), ("Sam Jones", "sam@example.com", "required")];

    private (List<JsonObject> Queued, List<JsonObject> Acted) Triage(FakeCalendar calendar)
    {
        var acted = new List<JsonObject>();
        var (items, _, _) = InviteTriage.Triage(config, calendar, [], Quiet, now, acted: acted);
        return (items, acted);
    }

    private static string Subject(JsonObject item) => item.Req("subject").Split(':')[0];

    [Fact]
    public void no_clash_and_markers_are_accepted_but_outside_optional_and_real_clashes_are_asked()
    {
        var calendar = new FakeCalendar([
            Self("block", "Focus time", "2026-11-12", "14:00", "15:00"),
            Ev("held", "Quarterly review", "2026-11-18", "15:00", "16:00", response: "accepted"),
            Ev("i-free", "Project sync", "2026-11-13", "10:00", "11:00", attendees: Two),
            Ev("i-block", "Design review", "2026-11-12", "14:30", "15:30", attendees: Two),
            Ev("i-clash", "Steering group", "2026-11-18", "15:30", "16:30", attendees: Two),
            Ev("i-outside", "Vendor demo", "2026-11-13", "13:00", "14:00", organiser: "Rudi", orgAddr: "rudi@vendor.com",
               attendees: [("Alex Smith", Me, "required"), ("Rudi", "rudi@vendor.com", "required")]),
            Ev("i-optional", "Lunch and learn", "2026-11-19", "12:00", "13:00", attendees: [("Alex Smith", Me, "optional")]),
            Ev("i-group", "Wellbeing webinar", "2026-11-19", "14:00", "15:00", attendees: [("Everyone", "all@example.com", "required")]),
            Ev("i-marker", "Salary changes due", "2026-11-12", "00:00", "23:59", isAllDay: true, showAs: "free",
               attendees: [("Managers", "managers@example.com", "required")]),
        ]);
        var (queued, acted) = Triage(calendar);
        Assert.Equal(["Lunch and learn", "Steering group", "Vendor demo", "Wellbeing webinar"], queued.Select(Subject).Order());
        Assert.Equal(new Dictionary<string, string>
            {
                ["Design review"] = "Accepted: your own blocks moved", ["Project sync"] = "Accepted: no clash",
                ["Salary changes due"] = "Accepted: holds no time",
            }.OrderBy(k => k.Key), acted.ToDictionary(Subject, a => a.Req("rule")).OrderBy(k => k.Key));
        Assert.Contains(("i-free", "accept", "", true), calendar.Answers);
        Assert.Single(calendar.Moves, m => m.Id == "block");   // Focus time moved out of the way
    }

    [Fact]
    public void a_clash_with_a_meeting_you_organise_is_asked_and_auto_accept_off_asks_everything()
    {
        var calendar = new FakeCalendar([
            Ev("mine", "Weekly 1-2-1 - Jordan", "2026-11-12", "15:00", "15:50", organiser: "Alex Smith", orgAddr: Me,
               attendees: [("Alex Smith", Me, "required"), ("Jordan", "jordan@example.com", "required")], response: "organizer"),
            Ev("i-mine", "Architecture catch-up", "2026-11-12", "15:00", "15:50", attendees: Two),
            Ev("i-free", "Project sync", "2026-11-13", "10:00", "11:00", attendees: Two),
        ]);
        var (queued, acted) = Triage(calendar);
        Assert.Equal(["Architecture catch-up"], queued.Select(Subject));   // moving your meeting notifies its attendees: asked
        Assert.Single(acted);
        config.Invites!.AutoAccept = false;
        (queued, acted) = Triage(new FakeCalendar(calendar.Items));
        Assert.Equal(2, queued.Count);
        Assert.Empty(acted);
    }

    [Fact]
    public void a_series_free_apart_from_days_off_is_accepted_with_those_declined_and_one_wholly_on_leave_is_declined()
    {
        var calendar = new FakeCalendar([
            Self("leave", "Holiday (Architect)", "2026-11-26", "00:00", "23:59", showAs: "oof"),
            Self("bh", "Bank holiday", "2026-12-03", "09:00", "17:00", showAs: "oof"),
            .. new[] { "2026-11-19", "2026-11-26", "2026-12-03", "2026-12-10" }.Select((d, i) =>
                Ev($"s{i}", "Project weekly", d, "10:00", "11:00", attendees: Two, type: "occurrence", seriesMasterId: "series-p")),
            Ev("i-leave", "Planning day", "2026-11-26", "13:00", "15:00", attendees: Two),
        ]);
        var (queued, acted) = Triage(calendar);
        Assert.True(queued.Count == 0, string.Join(" | ", queued.Select(q => q.Req("subject") + " " + q.Req("check"))));
        Assert.Equal(new Dictionary<string, string> { ["Planning day"] = "Declined: on your days off", ["Project weekly"] = "Accepted: declined on your days off" }
            .OrderBy(k => k.Key), acted.ToDictionary(Subject, a => a.Req("rule")).OrderBy(k => k.Key));
        Assert.Contains(("series-p", "accept", "", true), calendar.Answers);
        Assert.Contains(("s1", "decline", "On annual leave", true), calendar.Answers);
        Assert.Contains(calendar.Answers, a => a.Id == "s2" && a.Action == "decline");   // the bank holiday
        Assert.Contains(("i-leave", "decline", "On annual leave", true), calendar.Answers);
    }

    [Fact]
    public void accepted_meetings_on_days_off_are_declined_once_each_and_half_days_only_in_their_hours()
    {
        var calendar = new FakeCalendar([
            Self("leave", "Holiday (Architect)", "2026-12-16", "00:00", "23:59", endDay: "2026-12-18", showAs: "oof"),
            Self("half", "Holiday (half day) (Architect)", "2026-11-27", "12:00", "23:59", showAs: "oof"),
            Self("bh", "Good Friday", "2027-01-01", "00:00", "00:00", endDay: "2027-01-02", isAllDay: true, showAs: "free", categories: ["Bank holiday"]),
            Ev("a1", "Project sync", "2026-12-17", "10:00", "11:00", response: "accepted", attendees: Two),
            Ev("a2", "SMT catch-up", "2027-01-01", "11:00", "12:00", response: "tentativelyAccepted", attendees: Two),
            Ev("a3", "Morning stand-up", "2026-11-27", "09:00", "09:30", response: "accepted", attendees: Two),
            Ev("a4", "Afternoon review", "2026-11-27", "14:00", "15:00", response: "accepted", attendees: Two),
            Ev("a5", "Already declined", "2026-12-16", "10:00", "11:00", response: "declined", attendees: Two),
            Ev("a6", "My own meeting", "2026-12-16", "14:00", "15:00", response: "organizer", organiser: "Alex Smith", orgAddr: Me, attendees: Two),
            Ev("a7", "Following: all hands", "2026-12-18", "10:00", "11:00", response: "accepted", showAs: "free", attendees: Two),
        ]);
        var done = new JsonObject();
        var acted = InviteTriage.DeclineDaysOff(config, calendar, calendar.Items, done, Quiet, now);
        Assert.Equal([
            ("a4", "decline", "On annual leave", true),
            ("a1", "decline", "On annual leave 16/12 to 18/12", true),
            ("a2", "decline", "Bank holiday", true),
        ], calendar.Answers);
        Assert.Equal(["Declined: on leave", "Declined: on leave", "Declined: bank holiday"], acted.Select(a => a.Req("rule")));
        // Each once: a meeting accepted again after the decline (you've decided to join) is left alone
        calendar.Answers.Clear();
        Assert.Empty(InviteTriage.DeclineDaysOff(config, calendar, calendar.Items, done, Quiet, now));
        Assert.Empty(calendar.Answers);
        // max_declines
        config.Invites!.MaxDeclines = 1;
        Assert.Single(InviteTriage.DeclineDaysOff(config, calendar, calendar.Items, [], Quiet, now));
    }

    [Fact]
    public void a_decline_whose_answer_was_lost_is_recorded_as_unconfirmed_and_never_sent_again()
    {
        var calendar = new FakeCalendar([
            Self("leave", "Holiday (Architect)", "2026-12-16", "00:00", "23:59", endDay: "2026-12-18", showAs: "oof"),
            Ev("a1", "Project sync", "2026-12-17", "10:00", "11:00", response: "accepted", attendees: Two),
            Ev("a2", "Design review", "2026-12-17", "14:00", "15:00", response: "accepted", attendees: Two),
        ]);
        calendar.Lost.Add("a1");
        var done = new JsonObject();
        var acted = InviteTriage.DeclineDaysOff(config, calendar, calendar.Items, done, Quiet, now);
        Assert.Equal(2, calendar.Answers.Count);   // the lost one didn't stop the run
        var lost = acted.Single(a => a.Req("subject").StartsWith("Project sync", StringComparison.Ordinal));
        Assert.Equal("Couldn't confirm: Declined: on leave", lost.Req("rule"));
        Assert.Contains("may have gone, check the calendar", lost.Req("why"));
        Assert.True(Unconfirmed.Is(lost));
        Assert.Equal("Declined: on leave", acted.Single(a => a != lost).Req("rule"));
        // Never sent again: it counts as done
        calendar.Answers.Clear();
        Assert.Empty(InviteTriage.DeclineDaysOff(config, calendar, calendar.Items, done, Quiet, now));
        Assert.Empty(calendar.Answers);
    }

    [Fact]
    public void double_bookings_are_raised_once_and_your_own_blocks_dont_count()
    {
        var calendar = new FakeCalendar([
            Ev("m1", "Project sync", "2026-11-19", "10:00", "11:00", response: "accepted", attendees: Two),
            Ev("m2", "Steering group", "2026-11-19", "10:30", "11:30", response: "tentativelyAccepted", attendees: Two),
            Self("focus", "Focus time", "2026-11-20", "10:00", "12:00"),
            Ev("m3", "Design review", "2026-11-20", "11:00", "12:00", response: "accepted", attendees: Two),
            Self("away", "Dentist", "2026-11-24", "14:00", "15:00", showAs: "oof"),
            Ev("m4", "Ops review", "2026-11-24", "14:30", "15:30", response: "accepted", attendees: Two),
        ]);
        var raised = new JsonObject();
        var (items, seen) = InviteTriage.DoubleBookings(config, calendar.Items, raised, Quiet, now);
        Assert.Equal(2, items.Count);   // the two meetings, and the out-of-office block; not Focus time
        Assert.Contains("Project sync", items[0].Req("subject"));
        Assert.Contains("Steering group", items[0].Req("subject"));
        Assert.Contains("Dentist", items[1].Req("subject"));
        Assert.Equal("double-booking", items[0].Req("type"));
        (items, var again) = InviteTriage.DoubleBookings(config, calendar.Items, raised, Quiet, now);
        Assert.Empty(items);           // raised once
        Assert.Equal(seen, again);     // but still seen, so the queued item stays until you keep it
    }

    [Fact]
    public void months_ahead_overrides_days_ahead()
    {
        Assert.Equal(92, config.Invites!.Days(now));   // 11 Nov to 11 Feb
        config.Invites.MonthsAhead = null;
        Assert.Equal(config.Invites.DaysAhead, config.Invites.Days(now));
    }

    // 1.14.0: what you organise on your days off.
    [Fact]
    public void your_one_to_ones_are_cancelled_your_blocks_removed_and_your_group_meetings_raised_on_days_off()
    {
        config.Invites!.CancelDaysOff = true;
        config.Invites.RemoveBlocksDaysOff = true;
        config.Invites.RaiseGroupDaysOff = true;
        var mine = new (string, string, string)[] { ("Alex Smith", Me, "required"), ("Pat Boss", "pat@example.com", "required") };
        var group = new (string, string, string)[] { ("Alex Smith", Me, "required"), ("Sam", "sam@example.com", "required"), ("Robin", "robin@example.com", "required") };
        JsonObject Own(string id, string subject, string day, string start, string end, (string, string, string)[]? people = null, string showAs = "busy") =>
            Ev(id, subject, day, start, end, organiser: "Alex Smith", orgAddr: Me, attendees: people ?? [], response: "organizer", showAs: showAs);
        var calendar = new FakeCalendar([
            Self("leave", "Holiday (Architect)", "2026-12-16", "00:00", "23:59", endDay: "2026-12-18", showAs: "oof"),
            Self("half", "Holiday (half day) (Architect)", "2026-11-27", "12:00", "23:59", showAs: "oof"),
            Self("bh", "Bank holiday", "2026-12-25", "09:00", "17:00", showAs: "oof"),
            Own("boss", "Weekly 1-2-1 - Alex", "2026-12-17", "16:00", "16:25", mine),
            Own("catch", "Catch-up with Pat", "2026-11-27", "10:00", "10:30", mine),        // the morning of a PM half day: stays
            Own("catch2", "Catch-up with Pat", "2026-11-27", "14:00", "14:30", mine),       // the afternoon: cancelled
            Own("block", "JL pre-read", "2026-12-16", "16:30", "16:55"),
            Own("block2", "Review briefings", "2026-12-25", "08:30", "09:25"),
            Own("team", "Architecture catch-up", "2026-12-17", "14:00", "14:50", group),
            Own("free", "Reminder", "2026-12-17", "09:00", "09:15", showAs: "free"),
            Own("later", "JL pre-read", "2026-12-21", "16:30", "16:55"),                     // not a day off
        ]);
        var done = new JsonObject();
        var raised = new JsonObject();
        var (items, acted, seen) = InviteTriage.OwnDaysOff(config, calendar, calendar.Items, done, raised, Quiet, now);
        Assert.Equal([("catch2", "Cancelling as I'm on leave"), ("boss", "Cancelling as I'm on leave")], calendar.Cancels);
        Assert.Equal(["block", "block2"], calendar.Removed);
        Assert.Equal(["Cancelled: your meeting, on leave", "Removed: your block, on leave", "Cancelled: your meeting, on leave",
            "Removed: your block, bank holiday"], acted.Select(a => a.Req("rule")));
        Assert.Single(items);
        Assert.Equal("own-meeting", items[0].Req("type"));
        Assert.StartsWith("On your leave: Architecture catch-up", items[0].Req("subject"));
        // Each once; the group meeting stays seen (so it stays queued) but isn't queued twice
        calendar.Cancels.Clear();
        calendar.Removed.Clear();
        var (again, acted2, seen2) = InviteTriage.OwnDaysOff(config, calendar, calendar.Items, done, raised, Quiet, now);
        Assert.Empty(again);
        Assert.Empty(acted2);
        Assert.Equal(seen, seen2);
        Assert.Empty(calendar.Cancels);
    }
}
