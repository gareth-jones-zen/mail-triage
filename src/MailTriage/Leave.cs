using System.Text.Json.Nodes;
using CalendarDays;
using PyCompat;

namespace MailTriage;

/// <summary>Your days off over a stretch of calendar, from background-tools-lib's shared rules (1.17.0): each day
/// classified by CalendarDays.DayOffRules with [leave]'s settings, so mail-triage, calendar-out-of-office,
/// calendar-work-location and meeting-preps agree on what's leave, what's a half day and what's a bank holiday.
///
/// mail-triage keeps its stricter rule on top: a half day counts only for what it overlaps. A half day whose half is
/// known (its title says AM or PM, or it's a timed entry before or after midday) is off for that half; one made of
/// short timed entries is off for those hours; one whose half can't be told (People First's "Holiday (half day)
/// (job title)") is neither: nothing is declined or cancelled for it, and the invite or 1:1 is raised instead.</summary>
public sealed class DaysOffView
{
    /// <summary>Whole days off: leave, or a bank holiday.</summary>
    public HashSet<DateOnly> Away { get; } = [];

    /// <summary>Bank holidays that aren't also leave (their own note).</summary>
    public HashSet<DateOnly> BankOnly { get; } = [];

    /// <summary>Half days, as classified.</summary>
    public Dictionary<DateOnly, DayOff> Halves { get; } = [];

    /// <summary>Every day with any leave or a bank holiday: whole days and half days.</summary>
    public HashSet<DateOnly> AnyLeave { get; } = [];

    /// <summary>The ids of your leave and bank holiday entries (never meetings, never clashes).</summary>
    public HashSet<string> EntryIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Each classified day (those with leave or a bank holiday).</summary>
    public Dictionary<DateOnly, DayOff> Days { get; } = [];

    public TimeOnly Midday { get; init; } = new(13, 0);

    /// <summary>Whether a meeting from start to end falls in your time off: true, false, or null when it's on a half day
    /// whose half isn't known.</summary>
    public bool? OffAt(DateTime start, DateTime end)
    {
        var day = DateOnly.FromDateTime(start);
        if (Away.Contains(day))
            return true;
        if (!Halves.TryGetValue(day, out var half))
            return false;
        // Short timed entries (a few hours marked as leave) are off for their own hours, whichever half they're in.
        var entries = half.Leave?.Entries ?? [];
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        bool Short(DayEvent e) => !e.AllDay && DayRules.Hours([e], dayStart, dayStart.AddDays(1)) < 20;
        if (entries.Count > 0 && entries.All(Short))
            return entries.Any(e => e.Start < end && e.End > start);
        // A whole-day entry that says which half: that half. One that only says "half day" can't be placed: yours to decide.
        var noon = day.ToDateTime(Midday);
        return half.Half switch { HalfOfDay.Am => start < noon, HalfOfDay.Pm => end > noon, _ => null };
    }
}

public static class Leave
{
    /// <summary>A calendar event as CalendarDays sees it: local times, categories, yours or not, people invited (you
    /// and rooms not counted).</summary>
    public static DayEvent ToDayEvent(Settings settings, JsonObject e)
    {
        var (start, end) = InviteTriage.Times(e);
        var mine = InviteTriage.Organiser(e).Address == settings.Email && !e.Truthy("isCancelled");
        var others = InviteTriage.People(e).Select(InviteTriage.Address).Count(a => a.Length > 0 && a != settings.Email);
        return new DayEvent(e.StrOr("subject"), start, end, e.Truthy("isAllDay"), e.Str("showAs") ?? "busy",
            e.Get("categories").Strings().ToList(), mine, others);
    }

    /// <summary>The midday that splits a half day: [one_to_ones]' midday, else 13:00 (mail-triage's own default).</summary>
    public static TimeOnly Midday(Settings settings) =>
        TimeOnly.FromTimeSpan(PyTime.TimeFromIso(settings.OneToOnes?.Midday ?? "13:00"));

    /// <summary>Whether an entry of yours is leave (any length) or marks a bank holiday.</summary>
    public static bool IsLeaveEntry(Settings settings, DayEvent d) =>
        settings.Leave.IsBankHoliday(d)
        || (d.Mine && d.Attendees == 0 && (settings.Leave.Subjects.IsMatch(d.Subject)
                                          || d.Categories.Any(c => settings.Leave.Categories.Contains(c, StringComparer.OrdinalIgnoreCase))));

    /// <summary>Your days off across the days the events cover.</summary>
    public static DaysOffView View(Settings settings, List<JsonObject> events)
    {
        var view = new DaysOffView { Midday = Midday(settings) };
        var pairs = events.Where(e => !e.Truthy("isCancelled")).Select(e => (Json: e, Day: ToDayEvent(settings, e))).ToList();
        var mine = pairs.Where(p => IsLeaveEntry(settings, p.Day)).ToList();
        foreach (var (json, _) in mine)
            view.EntryIds.Add(json.Req("id"));
        var all = pairs.Select(p => p.Day).ToList();
        var days = mine.SelectMany(p => InviteTriage.SpanDays(p.Json)).Distinct().Order();
        foreach (var day in days)
        {
            var d = DayOffRules.Classify(settings.Leave, all, day, view.Midday);
            if (d.Kind == DayOffKind.None)
                continue;
            view.Days[day] = d;
            view.AnyLeave.Add(day);
            switch (d.Kind)
            {
                case DayOffKind.BankHoliday:
                    view.Away.Add(day);
                    if (!DayRules.Leave(settings.Leave.Rules, all.Where(e => !settings.Leave.IsBankHoliday(e)), day).DayOff)
                        view.BankOnly.Add(day);
                    break;
                case DayOffKind.Leave:
                    view.Away.Add(day);
                    break;
                case DayOffKind.HalfDay:
                    view.Halves[day] = d;
                    break;
            }
        }
        return view;
    }

    /// <summary>One day's verdict, in words: working, leave, half AM / half PM / half ?, or bank holiday.</summary>
    public static string Verdict(DayOff? d) => d?.Kind switch
    {
        DayOffKind.Leave => "leave",
        DayOffKind.BankHoliday => "bank holiday",
        DayOffKind.HalfDay => d.Half switch { HalfOfDay.Am => "half AM", HalfOfDay.Pm => "half PM", _ => "half ?" },
        _ => "working",
    };
}
