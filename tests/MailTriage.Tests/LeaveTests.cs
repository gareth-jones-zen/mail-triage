using System.Text.Json.Nodes;
using PyCompat;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

// 1.17.0: your days off from [leave], background-tools-lib's shared rules (CalendarDays), with mail-triage's own rule on
// top: a half day counts only for what it overlaps, and one whose half isn't known is never acted on.
public sealed class SharedLeave : IDisposable
{
    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly DateTime now = new(2026, 12, 4, 9, 0, 0);   // Fri 4 Dec 2026
    private static readonly (string, string, string)[] Two = [("Alex Smith", Me, "required"), ("Sam Jones", "sam@example.com", "required")];

    public SharedLeave()
    {
        config = dir.Settings(InviteSettings + "\n[leave]\n");
        config.Invites!.DeclineDaysOff = true;
        config.Invites.AutoAccept = true;
    }

    public void Dispose() => dir.Dispose();

    /// <summary>A week and a half of every kind of entry the rules have to judge.</summary>
    private static List<JsonObject> Fixtures() =>
    [
        Self("full", "Holiday (Principal Architect)", "2026-12-07", "00:00", "23:59", showAs: "oof"),            // Mon: People First, a day
        Self("short", "Holiday", "2026-12-08", "10:00", "12:00", showAs: "oof"),                                // Tue: two hours
        Self("pf-half", "Holiday (half day) (Principal Architect)", "2026-12-09", "00:00", "23:59", showAs: "oof"),  // Wed: which half?
        Self("pm", "On leave PM", "2026-12-10", "00:00", "00:00", endDay: "2026-12-11", isAllDay: true, showAs: "free"),  // Thu
        Self("bh-cat", "Christmas closure", "2026-12-11", "00:00", "00:00", endDay: "2026-12-12", isAllDay: true, showAs: "free",
             categories: ["Bank holiday"]),                                                                    // Fri: by category
        Self("bh-subj", "Bank holiday", "2026-12-14", "00:00", "00:00", endDay: "2026-12-15", isAllDay: true, showAs: "free"),  // Mon: by subject
        Self("multi", "Annual leave", "2026-12-15", "00:00", "00:00", endDay: "2026-12-18", isAllDay: true, showAs: "oof"),     // Tue-Thu
        Self("xmas", "Christmas holidays", "2026-12-18", "00:00", "00:00", endDay: "2026-12-19", isAllDay: true, showAs: "oof"), // Fri
    ];

    [Fact]
    public void days_off_shows_each_weekday_with_the_old_verdict_and_marks_the_differences()
    {
        var lines = Cli.DaysOffLines(config, Fixtures(), new DateOnly(2026, 12, 7), 12);
        string Verdict(int i) => lines[i].New;
        Assert.Equal(10, lines.Count);   // weekdays only
        Assert.Equal(["leave", "half AM", "half ?", "leave", "bank holiday", "bank holiday", "leave", "leave", "leave", "working"],
            Enumerable.Range(0, 10).Select(Verdict));
        // Where the old rules (leave_pattern, any length; a half day only by its title) said otherwise:
        // the two hours on Tue (a whole day, then), "On leave PM" on Thu (a half day, then: the shared half_day is
        // "(half day)" only), and "Christmas holidays" on Fri 18th (leave, then: not one of the shared subjects).
        Assert.Equal(["Tue 08 Dec", "Thu 10 Dec", "Fri 18 Dec"],
            lines.Where(l => l.Differs).Select(l => l.Text[2..12]));
        Assert.Contains("(old: leave)", lines[1].Text);
        Assert.Contains("(old: half (its hours))", lines[2].Text);   // same kind: not marked
        Assert.False(lines[2].Differs);
    }

    [Fact]
    public void a_half_day_counts_only_for_what_it_overlaps_and_one_of_unknown_half_not_at_all()
    {
        var view = Leave.View(config, Fixtures());
        Assert.True(view.OffAt(At("2026-12-08", "10:30"), At("2026-12-08", "11:00")));    // inside the two hours
        Assert.False(view.OffAt(At("2026-12-08", "14:00"), At("2026-12-08", "15:00")));   // after them
        Assert.Null(view.OffAt(At("2026-12-09", "10:00"), At("2026-12-09", "11:00")));    // which half isn't known
        Assert.True(view.OffAt(At("2026-12-15", "10:00"), At("2026-12-15", "11:00")));    // a day of leave
        Assert.Contains(new DateOnly(2026, 12, 11), view.BankOnly);
        Assert.Contains("full", view.EntryIds);
    }

    [Fact]
    public void accepted_meetings_are_declined_only_where_you_are_off()
    {
        var calendar = new FakeCalendar([
            .. Fixtures(),
            Ev("in-short", "Project sync", "2026-12-08", "10:30", "11:00", response: "accepted", attendees: Two),
            Ev("after-short", "Design review", "2026-12-08", "14:00", "15:00", response: "accepted", attendees: Two),
            Ev("unknown-half", "Steering group", "2026-12-09", "10:00", "11:00", response: "accepted", attendees: Two),
            Ev("leave-day", "Architecture forum", "2026-12-16", "10:00", "11:00", response: "accepted", attendees: Two),
        ]);
        InviteTriage.DeclineDaysOff(config, calendar, calendar.Items, [], Quiet, now);
        Assert.Equal(["in-short", "leave-day"], calendar.Answers.Select(a => a.Id).Order());
    }

    [Fact]
    public void an_invite_on_a_half_day_of_unknown_half_is_asked_not_answered()
    {
        var calendar = new FakeCalendar([
            .. Fixtures(),
            Ev("i-half", "Supplier call", "2026-12-09", "10:00", "11:00", attendees: Two),
            Ev("i-free", "Team lunch", "2026-12-21", "12:00", "13:00", attendees: Two),
        ]);
        var acted = new List<JsonObject>();
        var (items, _, _) = InviteTriage.Triage(config, calendar, [], Quiet, now, acted: acted);
        Assert.Equal(["Supplier call"], items.Select(i => i.Req("subject").Split(':')[0]));
        Assert.Contains("which half isn't known", items[0].Req("check"));
        Assert.Equal(["Team lunch"], acted.Select(a => a.Req("subject").Split(':')[0]));
    }

    [Fact]
    public void the_old_settings_still_read_as_aliases_and_are_logged()
    {
        var old = dir.Settings(InviteSettings.Replace("[invites]\n", "[invites]\nleave_pattern = '^(holiday|annual leave)'\nbank_holiday_categories = [\"BH\"]\n"),
            "old.toml");
        Assert.Equal(["[invites] leave_pattern", "[invites] bank_holiday_categories"], old.LeaveOldKeys);
        Assert.Matches(old.Leave.Subjects, "Annual leave");
        Assert.Equal(["BH"], old.Leave.BankHolidayCategories);
        Assert.Empty(config.LeaveOldKeys);
    }

    [Fact]
    public void both_forms_or_disagreeing_old_lists_are_refused()
    {
        var both = Assert.Throws<SettingsException>(() => dir.Settings(
            InviteSettings.Replace("[invites]\n", "[invites]\nleave_pattern = '^holiday'\n") + "\n[leave]\n", "both.toml"));
        Assert.Contains("both [leave] and the old [invites] leave_pattern", both.Message);
        var differ = Assert.Throws<SettingsException>(() => dir.Settings(
            InviteSettings.Replace("[invites]\n", "[invites]\nbank_holiday_categories = [\"A\"]\n")
            + "\n[one_to_ones]\nbank_holiday_categories = [\"B\"]\n", "differ.toml"));
        Assert.Contains("differ", differ.Message);
        var unknown = Assert.Throws<SettingsException>(() => dir.Settings(InviteSettings + "\n[leave]\nhalf_days = 'x'\n", "unknown.toml"));
        Assert.Contains("half_days", unknown.Message);
    }

    [Fact]
    public void reports_leave_titles_read_as_before()
    {
        var oneToOnes = dir.Settings(InviteSettings + OneToOneBase.OneToOnesText, "one.toml");
        List<JsonObject> events =
        [
            OneToOneBase.Away("a", "Jordan Lee annual leave (from PM)", "2026-12-07", "2026-12-09"),
            OneToOneBase.Away("b", "Jordan Lee on leave PM", "2026-12-10", "2026-12-10"),
            OneToOneBase.Away("c", "Jordan Lee annual leave half day", "2026-12-11", "2026-12-11"),
            OneToOneBase.Away("d", "Robin Hall annual leave", "2026-12-11", "2026-12-11"),
        ];
        var days = OneToOnePlanner.ReportLeave(oneToOnes, oneToOnes.OneToOnes!.Reports[0], events);
        Assert.Equal(new Dictionary<DateOnly, string>
        {
            [new(2026, 12, 7)] = "PM", [new(2026, 12, 8)] = "full", [new(2026, 12, 9)] = "full", [new(2026, 12, 10)] = "PM",
            [new(2026, 12, 11)] = "half",
        }.OrderBy(k => k.Key), days.OrderBy(k => k.Key));
    }
}
