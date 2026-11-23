using System.Text.Json.Nodes;
using CalendarDays;
using PyCompat;

namespace MailTriage;

/// <summary>Catch-ups ([catch_ups], 1.26.0): which meetings are catch-ups, and the other person's leave, read from their
/// own calendar through free/busy (getSchedule), so it works for a calendar you can see only the titles of, or only
/// free/busy. The planning itself is OneToOnePlanner's, shared with the 1:1s.</summary>
public static class CatchUpRules
{
    /// <summary>getSchedule reads at most 62 days at a time.</summary>
    internal const int ChunkDays = 60;

    /// <summary>The one other person in a catch-up: a recurring meeting (an occurrence or an exception) you organise, timed,
    /// not cancelled, not shown as free, with exactly one other person in it from your organisation (your address's domain;
    /// rooms, as resources or as [hold] rooms, aren't people), that isn't one of your [[one_to_one]]s and no
    /// [[catch_up_skip]] matches. Null for anything else. [invites] protected doesn't exclude one: that keeps a meeting
    /// from being moved or declined to make room for another, while this is about the meeting itself (your manager's 1:1
    /// is a catch-up, if you send it).</summary>
    public static (string Name, string Email)? Other(Settings settings, JsonObject meeting)
    {
        if (settings.CatchUps is not { } config || settings.Invites is null || settings.Email is not { } me)
            return null;
        var subject = meeting.StrOr("subject");
        if (!meeting.Truthy("isOrganizer") || meeting.Truthy("isCancelled") || meeting.Truthy("isAllDay") || meeting.Str("showAs") == "free"
            || meeting.Str("type") is not ("occurrence" or "exception"))
            return null;
        var rooms = settings.HoldRooms.Select(r => r.Address.ToLowerInvariant()).ToHashSet();
        var others = InviteTriage.People(meeting)
            .Select(a => (Name: a.Get("emailAddress").StrOr("name"), Email: InviteTriage.Address(a)))
            .Where(p => p.Email.Length > 0 && p.Email != me && !rooms.Contains(p.Email))
            .DistinctBy(p => p.Email).ToList();
        if (others.Count != 1)
            return null;
        var other = others[0];
        if (!SameDomain(me, other.Email))
            return null;
        if ((settings.OneToOnes?.Reports ?? []).Any(r => OneToOnePlanner.IsOneToOne(settings, r, meeting)))
            return null;
        if (config.Skip.Any(s => (s.Subject is null || PyRegex.IsMatch(s.Subject, subject, true)) && (s.Email is null || s.Email == other.Email)))
            return null;
        return (other.Name is { Length: > 0 } n ? n : other.Email, other.Email);
    }

    /// <summary>Whether a meeting is a catch-up (see Other).</summary>
    public static bool IsCatchUp(Settings settings, JsonObject meeting) => Other(settings, meeting) is not null;

    internal static bool SameDomain(string a, string b) =>
        a.LastIndexOf('@') is var i and >= 0 && b.LastIndexOf('@') is var j and >= 0
        && string.Equals(a[(i + 1)..], b[(j + 1)..], StringComparison.OrdinalIgnoreCase);

    /// <summary>Someone's leave from first to last (exclusive), {day: full | AM | PM | half}, from their own calendar: their
    /// entries classified by the shared [leave] rules (subjects, min_hours, half_day, a "Bank holiday" of theirs), as your
    /// own are; and, where no rule says anything about a day, an entry shown as out of office that covers the whole day
    /// (all day, several days, or People First's 00:00-23:59), which is how leave looks when only free/busy can be seen.
    /// Entries with people in them (meetings) are never leave. Null if their calendar couldn't be read.</summary>
    public static Dictionary<DateOnly, string>? TheirLeave(Settings settings, Diaries diaries, string address, DateOnly first, DateOnly last,
        TimeOnly midday) =>
        diaries.Of(address) is { } items ? LeaveFrom(settings, items, first, last, midday) : null;

    /// <summary>How a catch-up is handled: the first [[catch_up]] that matches it (its subject pattern and/or the other
    /// person's email), whose settings already have [catch_ups]' filled in where it leaves them out; else [catch_ups]'.</summary>
    public static LeaveHandling Handling(Settings settings, JsonObject meeting, string email)
    {
        var config = settings.CatchUps!;
        var subject = meeting.StrOr("subject");
        var entry = config.Entries.FirstOrDefault(e => (e.Subject is null || PyRegex.IsMatch(e.Subject, subject, true)) && (e.Email is null || e.Email == email));
        return entry?.Handling ?? config.Handling;
    }

    /// <summary>People's calendars through free/busy over a plan's window, each read once a run (in chunks getSchedule
    /// allows). Null for a calendar that couldn't be read.</summary>
    public sealed class Diaries(ICalendar calendar, DateOnly first, DateOnly last)
    {
        private readonly Dictionary<string, List<ScheduleItem>?> cache = new(StringComparer.Ordinal);

        public List<ScheduleItem>? Of(string address)
        {
            if (cache.TryGetValue(address, out var known))
                return known;
            var items = new List<ScheduleItem>();
            for (var from = first; from < last; from = from.AddDays(ChunkDays))
            {
                var to = from.AddDays(ChunkDays) < last ? from.AddDays(ChunkDays) : last;
                var chunk = calendar.ScheduleItems(address, from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MinValue));
                if (chunk is null)
                {
                    items = null;
                    break;
                }
                items.AddRange(chunk);
            }
            cache[address] = items?.Distinct().ToList();
            return cache[address];
        }
    }

    /// <summary>A free/busy entry as the shared rules see it: theirs, with people in it only if it's a meeting. An entry
    /// from midnight to midnight is all day.</summary>
    internal static DayEvent ToDayEvent(ScheduleItem i) =>
        new(i.Subject, i.Start, i.End, i.Start.TimeOfDay == TimeSpan.Zero && i.End.TimeOfDay == TimeSpan.Zero && i.End > i.Start,
            i.Status ?? "busy", [], true, i.IsMeeting ? 1 : 0);

    /// <summary>The days off in some free/busy entries (see TheirLeave).</summary>
    internal static Dictionary<DateOnly, string> LeaveFrom(Settings settings, List<ScheduleItem> items, DateOnly first, DateOnly last, TimeOnly midday)
    {
        var events = items.Select(ToDayEvent).ToList();
        var days = new Dictionary<DateOnly, string>();
        for (var day = first; day < last; day = day.AddDays(1))
        {
            if (!events.Any(e => DayRules.Covers(e, day)))
                continue;
            var d = DayOffRules.Classify(settings.Leave, events, day, midday);
            if (d.Kind is DayOffKind.Leave or DayOffKind.BankHoliday)
                days[day] = OneToOnePlanner.Full;
            else if (d.Kind == DayOffKind.HalfDay)
                days[day] = d.Half switch { HalfOfDay.Am => OneToOnePlanner.Am, HalfOfDay.Pm => OneToOnePlanner.Pm, _ => OneToOnePlanner.Half };
            else if (events.Any(e => e.Attendees == 0 && string.Equals(e.ShowAs, "oof", StringComparison.OrdinalIgnoreCase) && WholeDay(e, day)))
                days[day] = OneToOnePlanner.Full;
        }
        return days;
    }

    /// <summary>Whether an entry covers the whole of a day: from its midnight to at least 23:59.</summary>
    private static bool WholeDay(DayEvent e, DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        return e.Start <= start && e.End >= start.AddDays(1).AddMinutes(-1);
    }
}
