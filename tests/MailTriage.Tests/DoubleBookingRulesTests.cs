using System.Text.Json.Nodes;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

// 1.13.0: double bookings settled by [[double_booking_rule]]s. "Now" is Wed 11 Nov 2026, 09:00; the in-person SLT is
// Thu 19 Nov, 10:00-14:00.
[Collection("Checks.Gh")]   // through_a_run calls Triage.Run, which sets the static Checks.Graph
public sealed class DoubleBookingRules_ : IDisposable
{
    public const string Rules = """

        [groups]
        first_team = ['Rich@Example.com', 'kurt@example.com']

        [double_bookings]
        act = true

        [[double_booking_rule]]
        name = "In-person SLT beats a first-team catch-up"
        keep = '^In Person Monthly Catchup- SysDev & PMO SLT'
        other = '(?i)catch-?up'
        other_with = 'first_team'
        action = "move"

        [[double_booking_rule]]
        name = "In-person SLT beats ERT"
        keep = '^In Person Monthly Catchup- SysDev & PMO SLT'
        other = '^Regular ERT/Sys Dev Catchup'
        action = "decline"
        note = "Sorry, there's an in-person SysDev & PMO SLT that day"

        """;

    public static string Text(string rules = Rules) =>
        "double_bookings_file = \"double-bookings.json\"\n" + InviteSettings.Replace("[invites]\n", "[invites]\ndouble_bookings = true\n") + rules;

    private static readonly (string, string, string) Rich = ("Rich Evans", "rich@example.com", "required");
    private static readonly (string, string, string) Kurt = ("Kurt Donoghue", "kurt@example.com", "required");
    private static readonly (string, string, string) Alex = ("Alex Smith", Me, "required");

    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly DateTime now = new(2026, 11, 11, 9, 0, 0);

    public DoubleBookingRules_() => config = dir.Settings(Text());

    public void Dispose() => dir.Dispose();

    public static JsonObject Slt(string day = "2026-11-19") =>
        Ev($"slt-{day[8..]}", "In Person Monthly Catchup- SysDev & PMO SLT", day, "10:00", "14:00", response: "accepted", organiser: "Jo Emsley",
           orgAddr: "jo@example.com", attendees: [Alex, Rich, Kurt, ("Jo Emsley", "jo@example.com", "required")]);

    public static JsonObject CatchUp(string day = "2026-11-19", string start = "11:00", string end = "11:30", (string, string, string)[]? with = null) =>
        Ev($"re-{day[8..]}", "GJ and RE catchup", day, start, end, organiser: "Alex Smith", orgAddr: Me, attendees: with ?? [Alex, Rich],
           type: "occurrence", seriesMasterId: "re-series");

    public static JsonObject Ert(string organiser = "Pat Ops", string address = "pat@example.com") =>
        Ev("ert-19", "Regular ERT/Sys Dev Catchup", "2026-11-19", "13:00", "14:00", response: "accepted", organiser: organiser, orgAddr: address,
           attendees: [Alex, ("Pat Ops", "pat@example.com", "required"), ("Lee Ops", "lee@example.com", "required")], type: "occurrence", seriesMasterId: "ert-series");

    /// <summary>Rich's free/busy: the SLT, and whatever else is given.</summary>
    private static Dictionary<string, List<(DateTime, DateTime, string)>> RichBusy(params (string Day, string Start, string End)[] more) => new()
    {
        ["rich@example.com"] = [(At("2026-11-19", "10:00"), At("2026-11-19", "14:00"), "busy"), .. more.Select(m => (At(m.Day, m.Start), At(m.Day, m.End), "busy"))],
    };

    private (List<JsonObject> Items, List<JsonObject> Acted, HashSet<string> Seen) Run(FakeCalendar calendar, JsonObject? raised = null,
        JsonObject? done = null, bool dryRun = false, Settings? settings = null) =>
        DoubleBookingPlanner.Run(settings ?? config, calendar, calendar.Items, raised ?? [], Quiet, now, done ?? [], dryRun);

    [Fact]
    public void settings_are_read()
    {
        Assert.Equal(["rich@example.com", "kurt@example.com"], config.Groups["first_team"]);
        var rules = config.DoubleBookings!;
        Assert.True(rules.Act);
        Assert.Equal(20, rules.MaxActions);
        Assert.Equal([("move", "first_team", ""), ("decline", null, "Sorry, there's an in-person SysDev & PMO SLT that day")],
                     rules.Rules.Select(r => (r.Action, r.OtherWith, r.Note)));
        Assert.EndsWith("double-bookings.json", config.DoubleBookingsFile);
    }

    [Fact]
    public void a_first_team_catch_up_moves_to_the_nearest_free_time_that_day()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp()], RichBusy());
        var (items, acted, seen) = Run(calendar);
        // Free before 10:00 and after 14:00; 09:30 is nearer 11:00 than 14:00
        Assert.Equal([("re-19", At("2026-11-19", "09:30"), At("2026-11-19", "10:00"))], calendar.Moves);
        Assert.Empty(calendar.Answers);
        Assert.Empty(items);
        Assert.Empty(seen);                                              // settled: an old queued item drops off
        Assert.Equal("Double booking: moved", acted[0].Req("rule"));
        Assert.Equal("Rich Evans", acted[0].Req("from"));
        Assert.Contains("[In-person SLT beats a first-team catch-up] moved \"GJ and RE catchup\" to Thu 19 Nov 09:30–10:00", acted[0].Req("why"));
        Assert.Equal(["rich@example.com"], calendar.Asked);
    }

    [Fact]
    public void no_time_that_day_moves_to_the_same_time_on_the_nearest_day()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp()], RichBusy(("2026-11-19", "08:00", "18:00")));
        Run(calendar);
        // Wednesday and Friday 11:00 are both a day away: the later
        Assert.Equal([("re-19", At("2026-11-20", "11:00"), At("2026-11-20", "11:30"))], calendar.Moves);
    }

    [Fact]
    public void the_same_time_taken_everywhere_falls_back_to_the_nearest_time()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp(), .. new[] { 16, 17, 18, 20 }.Select(d =>
            Ev($"w{d}", "Workshop", $"2026-11-{d}", "10:00", "12:00", response: "accepted"))], RichBusy(("2026-11-19", "08:00", "18:00")));
        Run(calendar);
        // 11:00 is taken every day, so the nearest time: Wed 17:30 (17h30 before), not Fri 09:30 (22h30 after)
        Assert.Equal([("re-19", At("2026-11-18", "17:30"), At("2026-11-18", "18:00"))], calendar.Moves);
    }

    [Fact]
    public void your_days_off_are_skipped_when_moving()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp(), Self("al", "Annual leave", "2026-11-20", "00:00", "00:00", endDay: "2026-11-21", isAllDay: true, showAs: "oof")],
            RichBusy(("2026-11-19", "08:00", "18:00")));
        Run(calendar);
        Assert.Equal([("re-19", At("2026-11-18", "11:00"), At("2026-11-18", "11:30"))], calendar.Moves);   // not Friday: on leave
    }

    [Fact]
    public void no_free_slot_that_week_is_queued_saying_so()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp()],
            RichBusy(("2026-11-16", "08:00", "18:00"), ("2026-11-17", "08:00", "18:00"), ("2026-11-18", "08:00", "18:00"),
                     ("2026-11-19", "08:00", "18:00"), ("2026-11-20", "08:00", "18:00")));
        var (items, acted, _) = Run(calendar);
        Assert.Empty(calendar.Moves);
        Assert.Empty(acted);
        Assert.Contains("Rule \"In-person SLT beats a first-team catch-up\" didn't act: no free slot that week", Assert.Single(items).Req("check"));
    }

    [Fact]
    public void a_catch_up_someone_else_organises_isnt_moved()
    {
        var theirs = Ev("re-19", "GJ and RE catchup", "2026-11-19", "11:00", "11:30", response: "accepted", organiser: "Rich Evans",
                        orgAddr: "rich@example.com", attendees: [Alex, Rich]);
        var (items, _, _) = Run(new FakeCalendar([Slt(), theirs], RichBusy()));
        Assert.Contains("is someone else's meeting", Assert.Single(items).Req("check"));
    }

    [Fact]
    public void the_ert_occurrence_is_declined_with_the_note()
    {
        var calendar = new FakeCalendar([Slt(), Ert()]);
        var (items, acted, _) = Run(calendar);
        Assert.Equal([("ert-19", "decline", "Sorry, there's an in-person SysDev & PMO SLT that day", true)], calendar.Answers);
        Assert.Empty(items);
        Assert.Equal(("Double booking: declined", "Pat Ops"), (acted[0].Req("rule"), acted[0].Req("from")));
    }

    [Fact]
    public void a_decline_sends_no_response_where_none_was_asked_for()
    {
        var ert = Ert();
        ert["responseRequested"] = false;
        var calendar = new FakeCalendar([Slt(), ert]);
        Run(calendar);
        Assert.False(Assert.Single(calendar.Answers).Send);
    }

    [Fact]
    public void a_meeting_you_organise_isnt_declined()
    {
        var calendar = new FakeCalendar([Slt(), Ert("Alex Smith", Me)]);
        var (items, acted, _) = Run(calendar);
        Assert.Empty(calendar.Answers);
        Assert.Empty(acted);
        Assert.Contains("you organise \"Regular ERT/Sys Dev Catchup\"", Assert.Single(items).Req("check"));
    }

    [Fact]
    public void other_with_needs_exactly_one_other_person_from_the_group()
    {
        // Rich and Kurt: two others, so the move rule doesn't fit; nor does the ERT rule: queued as before, no rule
        var (items, _, _) = Run(new FakeCalendar([Slt(), CatchUp(with: [Alex, Rich, Kurt])], RichBusy()));
        Assert.DoesNotContain("Rule", Assert.Single(items).Req("check"));
        // Someone outside the group
        (items, _, _) = Run(new FakeCalendar([Slt(), CatchUp(with: [Alex, ("Sam Jones", "sam@example.com", "required")])]));
        Assert.DoesNotContain("Rule", Assert.Single(items).Req("check"));
    }

    [Fact]
    public void no_rule_fits_and_it_is_queued_as_before()
    {
        var calendar = new FakeCalendar([Slt(), Ev("x", "Board prep", "2026-11-19", "12:00", "13:00", response: "accepted",
                                                    attendees: [Alex, ("Sam Jones", "sam@example.com", "required")])]);
        var (items, acted, _) = Run(calendar);
        Assert.Empty(acted);
        Assert.Equal("CHECK: accepted \"In Person Monthly Catchup- SysDev & PMO SLT\" overlaps accepted \"Board prep\". Decide which goes, "
                     + "change it in Outlook, then `mail-triage keep <n>`.", Assert.Single(items).Req("check"));
    }

    [Fact]
    public void the_first_rule_that_fits_wins()
    {
        var settings = dir.Settings(Text(Rules.Replace("[[double_booking_rule]]\nname = \"In-person SLT beats a first-team catch-up\"", """
            [[double_booking_rule]]
            name = "ERT first"
            keep = '^In Person'
            other = 'ERT'
            action = "decline"
            note = "First"

            [[double_booking_rule]]
            name = "In-person SLT beats a first-team catch-up"
            """)), "first.toml");
        var calendar = new FakeCalendar([Slt(), Ert()]);
        var (_, acted, _) = Run(calendar, settings: settings);
        Assert.Equal("First", Assert.Single(calendar.Answers).Note);
        Assert.StartsWith("[ERT first]", acted[0].Req("why"));
    }

    [Fact]
    public void act_false_records_what_it_would_do_once_and_still_queues()
    {
        config.DoubleBookings!.Act = false;
        var calendar = new FakeCalendar([Slt(), CatchUp(), Ert()], RichBusy());
        var raised = new JsonObject();
        var done = new JsonObject();
        var (items, acted, _) = Run(calendar, raised, done);
        Assert.Empty(calendar.Moves);
        Assert.Empty(calendar.Answers);
        Assert.Equal(["Double booking (preview): would move", "Double booking (preview): would decline"], acted.Select(a => a.Req("rule")));
        Assert.Equal(2, items.Count);
        Assert.Contains("would move \"GJ and RE catchup\" to Thu 19 Nov 09:30–10:00 (act = false)", items[0].Req("check"));
        var (again, actedAgain, seen) = Run(calendar, raised, done);
        Assert.Empty(again);                                               // queued once
        Assert.Empty(actedAgain);                                          // recorded once
        Assert.Equal(2, seen.Count);                                       // still waiting, so they stay queued
        // Switched on: it acts, though the clashes were queued while previewing
        config.DoubleBookings.Act = true;
        Run(calendar, raised, done);
        Assert.Single(calendar.Moves);
        Assert.Single(calendar.Answers);
    }

    [Fact]
    public void a_dry_run_changes_and_remembers_nothing()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp(), Ert()], RichBusy());
        var done = new JsonObject();
        var (_, acted, _) = Run(calendar, done: done, dryRun: true);
        Assert.Equal((0, 0, 0), (calendar.Moves.Count, calendar.Answers.Count, done.Count));
        Assert.Equal(2, acted.Count);
    }

    [Fact]
    public void at_most_max_actions_a_run()
    {
        config.DoubleBookings!.MaxActions = 1;
        var calendar = new FakeCalendar([Slt(), CatchUp(), Ert()], RichBusy());
        var (items, acted, seen) = Run(calendar);
        Assert.Equal((1, 1, 0), (acted.Count, calendar.Moves.Count, calendar.Answers.Count));
        Assert.Empty(items);                                               // the other waits for the next run, not queued
        Assert.Single(seen);
    }

    [Fact]
    public void each_occurrence_is_moved_or_declined_once_only()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp(), Ert()], RichBusy());
        var done = new JsonObject();
        Run(calendar, done: done);
        Assert.Equal(["re-19", "ert-19"], done.Select(kv => kv.Key));
        // Still clashing next run (the fake calendar doesn't change): queued, not acted on again
        var (items, acted, _) = Run(calendar, done: done);
        Assert.Single(calendar.Moves);
        Assert.Single(calendar.Answers);
        Assert.Empty(acted);
        Assert.All(items, i => Assert.Contains("already", i.Req("check")));
    }

    [Fact]
    public void two_catch_ups_moved_in_one_run_dont_take_the_same_slot()
    {
        var second = Ev("ke-19", "GJ and KD catchup", "2026-11-19", "11:00", "11:30", organiser: "Alex Smith", orgAddr: Me, attendees: [Alex, Kurt]);
        var calendar = new FakeCalendar([Slt(), CatchUp(), second], RichBusy());
        Run(calendar);
        Assert.Equal([("re-19", At("2026-11-19", "09:30")), ("ke-19", At("2026-11-19", "09:00"))], calendar.Moves.Select(m => (m.Id, m.Start)));
    }

    [Fact]
    public void plan_lists_each_clash_and_what_the_rules_would_do()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp(), Ert(), Ev("x", "Board prep", "2026-11-19", "12:00", "13:00", response: "accepted",
                                                                         attendees: [Alex, ("Sam Jones", "sam@example.com", "required")])], RichBusy());
        var found = DoubleBookingPlanner.Plan(config, calendar, now);
        Assert.Equal(["move", "queue", "decline"], found.Select(r => r.Action));
        var lines = found.Select(DoubleBookingPlanner.Describe).ToList();
        Assert.Contains("MOVE \"GJ and RE catchup\" to Thu 19 Nov 09:30–10:00 [In-person SLT beats a first-team catch-up]", lines[0]);
        Assert.EndsWith("QUEUE (no rule)", lines[1]);
        Assert.Contains("DECLINE \"Regular ERT/Sys Dev Catchup\" (note: \"Sorry, there's an in-person SysDev & PMO SLT that day\")", lines[2]);
        Assert.Empty(calendar.Moves);
        Assert.Empty(calendar.Answers);
    }

    [Fact]
    public void through_a_run_it_is_recorded_for_deleted()
    {
        var calendar = new FakeCalendar([Slt(), CatchUp(), Ert()], RichBusy());
        using (Clock.Use(new FixedTime(now)))
            Triage.Run(config, new Mailbox(new FakeGraph([]), WriteDoor.Refusing().Mail), calendar, false, Quiet);
        var gone = Triage.Deleted(config);
        Assert.Equal(["Double booking: moved", "Double booking: declined"], gone.Select(g => g.Req("rule")));
        Assert.Contains("moved \"GJ and RE catchup\"", gone[0].Req("did"));
        Assert.True(File.Exists(Path.Combine(dir.Path, "double-bookings.json")));
        Assert.Empty(Triage.Queue(config));
    }

    [Theory]
    [InlineData("action = \"move\"", "action = \"cancel\"", "use move or decline")]
    [InlineData("other_with = 'first_team'", "other_with = 'leads'", "isn't a group in [groups] (one of first_team)")]
    [InlineData("action = \"move\"", "action = \"move\"\nnote = \"Moving\"", "note is for decline only")]
    [InlineData("other_with = 'first_team'", "other_wiht = 'first_team'", "'other_wiht' isn't a setting")]
    [InlineData("keep = '^In Person Monthly Catchup- SysDev & PMO SLT'\nother = '(?i)catch-?up'", "other = '(?i)catch-?up'", "keep, a pattern")]
    [InlineData("other = '(?i)catch-?up'", "other = '(catch'", "other isn't a valid regular expression")]
    [InlineData("act = true", "act = \"yes\"", "should be true or false")]
    [InlineData("act = true", "act = true\nmax_actions = 0", "at least 1")]
    [InlineData("act = true", "act = true\nacts = true", "[double_bookings] in")]
    [InlineData("first_team = ['Rich@Example.com', 'kurt@example.com']", "first_team = ['rich']", "should be a list of email addresses")]
    public void settings_problems_are_refused(string from, string to, string message)
    {
        var text = Text(Rules.Replace(from, to));
        Assert.NotEqual(Text(), text);
        Assert.Contains(message, Assert.Throws<SettingsException>(() => dir.Settings(text, "bad.toml")).Message);
    }

    [Fact]
    public void rules_need_double_bookings_switched_on()
    {
        var text = "double_bookings_file = \"x.json\"\n" + InviteSettings + Rules;
        Assert.Contains("double_bookings = true", Assert.Throws<SettingsException>(() => dir.Settings(text, "off.toml")).Message);
    }

    [Fact]
    public void rules_split_by_another_table_are_refused()
    {
        var text = Text(Rules.Replace("[[double_booking_rule]]\nname = \"In-person SLT beats ERT\"",
                                      "[[movable_block]]\nsubject = '^Something'\n\n[[double_booking_rule]]\nname = \"In-person SLT beats ERT\""));
        Assert.Contains("keep each kind together", Assert.Throws<SettingsException>(() => dir.Settings(text, "split.toml")).Message);
    }

    [Fact]
    public void without_rules_double_bookings_are_queued_as_in_1_11()
    {
        var plain = dir.Settings("double_bookings_file = \"x.json\"\n" + InviteSettings.Replace("[invites]\n", "[invites]\ndouble_bookings = true\n"), "plain.toml");
        Assert.Null(plain.DoubleBookings);
        var calendar = new FakeCalendar([Slt(), CatchUp(), Ert()], RichBusy());
        var (items, acted, _) = Run(calendar, settings: plain);
        Assert.Equal((2, 0, 0), (items.Count, acted.Count, calendar.Moves.Count));
    }
}
