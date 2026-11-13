using System.Globalization;
using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>One decision about one 1:1 occurrence (one_to_ones.plan): cancel, move or raise.</summary>
public sealed class Decision
{
    public required JsonObject Meeting { get; init; }
    public required OneToOne Report { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required string Action { get; init; }
    public required string Why { get; init; }
    public string? Note { get; init; }
    public (DateTime Start, DateTime End)? To { get; init; }
    public string? Suggest { get; init; }
}

/// <summary>Your weekly 1:1s with your reports, cancelled or moved when either of you is on leave (one_to_ones.py).
/// Nobody off at the meeting's time: nothing (a half day counts only if it overlaps). Someone off for at least
/// cancel_days of the week: cancelled, with a note. Otherwise moved, unasked, to the same time on the nearest day
/// you're both free, or the nearest slot in time. No slot: queued once with a suggestion. Never the series, never
/// anything protected. act = false only records what it would do. At most max_actions changes a run.</summary>
public static class OneToOnePlanner
{
    public const string Full = "full", Am = "AM", Pm = "PM", Half = "half";   // Half: a half day, but which half isn't known
    public const int Weekdays = 5;

    /// <summary>(each day, first day, last day) halves in a leave entry's subject.</summary>
    internal static (string? Each, string? First, string? Last) Markers(string text)
    {
        var first = PyRegex.Search(@"\bfrom\s+(am|pm)\b", text, true);
        var last = PyRegex.Search(@"\bto\s+(am|pm)\b", text, true);
        var rest = PyRegex.Get(@"\b(from|to)\s+(am|pm)\b", true).Sub(" ", text);
        var each = PyRegex.Search(@"\b(am|pm)\b", rest, true);
        var eachKind = each is not null ? each.Groups[1].Value.ToUpperInvariant() : PyRegex.IsMatch(@"\bhalf\b", rest, true) ? Half : null;
        return (eachKind, first?.Groups[1].Value.ToUpperInvariant(), last?.Groups[1].Value.ToUpperInvariant());
    }

    /// <summary>Two entries on one day: the morning and the afternoon make a full day; anything with a full day is one.</summary>
    private static void Merge(Dictionary<DateOnly, string> days, DateOnly day, string kind)
    {
        var old = days.GetValueOrDefault(day);
        days[day] = old is null || old == kind ? kind : (old == Full || kind == Full ? old : Full);   // as Python: a full day after a half keeps the half (a quirk kept for parity)
    }

    private static void Spread(Dictionary<DateOnly, string> days, JsonObject e, string? each, string? first, string? last)
    {
        var span = InviteTriage.SpanDays(e).ToList();
        for (var i = 0; i < span.Count; i++)
        {
            var kind = each ?? Full;
            if (i == 0 && first is not null)
                kind = first;
            if (i == span.Count - 1 && last is not null)
                kind = last;
            Merge(days, span[i], kind);
        }
    }

    private static bool Mine(Settings settings, JsonObject e) => InviteTriage.Organiser(e).Address == settings.Email && !e.Truthy("isCancelled");

    public static HashSet<DateOnly> BankHolidays(Settings settings, List<JsonObject> events)
    {
        var cats = settings.OneToOnes!.BankHolidayCategories.Select(c => c.ToLowerInvariant()).ToHashSet();
        var days = new Dictionary<DateOnly, string>();
        foreach (var e in events)
            if (Mine(settings, e) && ((e.Truthy("isAllDay") && e.Get("categories").Strings().Any(c => cats.Contains(c.ToLowerInvariant())))
                                      || PyRegex.Get(@"bank holiday\b", true).MatchStart(e.StrOr("subject")) is not null))
                Spread(days, e, null, null, null);
        return days.Keys.ToHashSet();
    }

    /// <summary>Your leave, {day: full | AM | PM | half}. Only "half" makes a half day.</summary>
    public static Dictionary<DateOnly, string> MyLeave(Settings settings, List<JsonObject> events)
    {
        var pattern = PyRegex.Get(settings.Invites!.LeavePattern, true);
        var days = new Dictionary<DateOnly, string>();
        foreach (var e in events)
        {
            var subject = e.StrOr("subject");
            if (!Mine(settings, e) || pattern.MatchStart(subject) is null || PyRegex.Get(@"bank holiday\b", true).MatchStart(subject) is not null)
                continue;
            var (each, first, last) = PyRegex.IsMatch(@"\bhalf\b", subject, true) ? Markers(subject) : (null, null, null);
            Spread(days, e, each, first, last);
        }
        return days;
    }

    /// <summary>A report's leave, from your own entries named "&lt;Full Name&gt; annual leave" / "... on leave".</summary>
    public static Dictionary<DateOnly, string> ReportLeave(Settings settings, OneToOne report, List<JsonObject> events)
    {
        var pattern = PyRegex.Get(settings.OneToOnes!.LeavePattern.Replace("{name}", PyRegex.Escape(report.Name)), true);
        var days = new Dictionary<DateOnly, string>();
        foreach (var e in events)
        {
            var subject = e.StrOr("subject");
            var found = Mine(settings, e) ? pattern.MatchStart(subject) : null;
            if (found is not null)
            {
                var (each, first, last) = Markers(subject[(found.Index + found.Length)..]);
                Spread(days, e, each, first, last);
            }
        }
        return days;
    }

    private static string? Off(Dictionary<DateOnly, string> away, HashSet<DateOnly> bank, DateOnly day) =>
        bank.Contains(day) ? Full : away.GetValueOrDefault(day);

    /// <summary>Whether time off of this kind overlaps the meeting: null if it can't be told (which half isn't known).</summary>
    private static bool? Hits(string? kind, DateTime start, DateTime end, TimeSpan midday)
    {
        if (kind is null)
            return false;
        if (kind == Half)
            return null;
        return kind == Full || (kind == Am && start.TimeOfDay < midday) || (kind == Pm && end.TimeOfDay > midday);
    }

    public static double DaysOff(Dictionary<DateOnly, string> away, HashSet<DateOnly> bank, DateOnly monday)
    {
        var total = 0.0;
        for (var i = 0; i < Weekdays; i++)
        {
            var kind = Off(away, bank, monday.AddDays(i));
            total += kind is null ? 0 : kind == Full ? 1 : 0.5;
        }
        return total;
    }

    private static DateOnly Monday(DateOnly day) => day.AddDays(-PyTime.Weekday(day));

    /// <summary>Your time that's taken: anything busy, out of office or tentative, other than the 1:1 itself.</summary>
    private static List<(DateTime Start, DateTime End)> Busy(List<JsonObject> events, JsonObject meeting) =>
        events.Where(e => e.Req("id") != meeting.Req("id") && !e.Truthy("isCancelled") && e.Str("showAs") is { } s && Proposals.HoldsTime.Contains(s)
                          && e.Get("responseStatus").Str("response") != "declined")
            .Select(InviteTriage.Times).ToList();

    /// <summary>The free slot to move a meeting to: the same time of day on the nearest day, and only if that time isn't
    /// free on any of these days, the slot nearest in time. Within working hours; on a tie, the later one.</summary>
    public static (DateTime Start, DateTime End)? Nearest(Settings settings, DateTime start, TimeSpan length, List<DateOnly> days,
        List<(DateTime Start, DateTime End)> busy, DateTime now)
    {
        var hours = settings.Invites!.WorkingHours;
        var options = new List<DateTime>();
        foreach (var day in days)
        {
            var t = day.ToDateTime(TimeOnly.MinValue) + PyTime.TimeFromIso(hours.Start);
            var close = day.ToDateTime(TimeOnly.MinValue) + (hours.End != "24:00" ? PyTime.TimeFromIso(hours.End) : PyTime.TimeMax);
            for (; t + length <= close; t += InviteTriage.Step)
                if (t >= now && t != start && !busy.Any(b => b.Start < t + length && b.End > t))
                    options.Add(t);
        }
        if (options.Count == 0)
            return null;
        var same = options.Where(t => t.TimeOfDay == start.TimeOfDay).ToList();
        var best = same.Count > 0
            ? same.MinBy(t => (Math.Abs(DateOnly.FromDateTime(t).DayNumber - DateOnly.FromDateTime(start).DayNumber), -t.Ticks))
            : options.MinBy(t => (Math.Abs((t - start).Ticks), -t.Ticks));
        return (best, best + length);
    }

    /// <summary>Each report's free/busy, a week at a time, fetched once a run. Null: it couldn't be read.</summary>
    private sealed class Schedules(ICalendar calendar)
    {
        private readonly Dictionary<(string, DateOnly), List<(DateTime Start, DateTime End, string? Status)>?> cache = new();

        public List<(DateTime Start, DateTime End)>? Week(string address, DateOnly monday, (DateTime Start, DateTime End) meeting)
        {
            if (!cache.TryGetValue((address, monday), out var items))
            {
                var start = monday.ToDateTime(TimeOnly.MinValue);
                items = calendar.Schedule(address, start, start.AddDays(Weekdays));
                cache[(address, monday)] = items;
            }
            if (items is null)
                return null;
            return items.Where(i => i.Status is { } s && Proposals.HoldsTime.Contains(s) && (i.Start, i.End) != meeting).Select(i => (i.Start, i.End)).ToList();
        }
    }

    /// <summary>The weekdays neither of you is off at all (a half day rules out the day).</summary>
    private static List<DateOnly> FreeDays(DateOnly monday, Dictionary<DateOnly, string> mine, Dictionary<DateOnly, string> theirs, HashSet<DateOnly> bank) =>
        Enumerable.Range(0, Weekdays).Select(monday.AddDays).Where(d => !bank.Contains(d) && !mine.ContainsKey(d) && !theirs.ContainsKey(d)).ToList();

    private sealed record Context(Settings Settings, OneToOne Report, Dictionary<DateOnly, string> Mine, Dictionary<DateOnly, string> Theirs,
        HashSet<DateOnly> Bank, List<JsonObject> Events, Schedules Schedules, DateTime Now);

    private static ((DateTime Start, DateTime End)? Slot, string? Problem) Slot(Context x, JsonObject meeting, DateOnly monday)
    {
        var (start, end) = InviteTriage.Times(meeting);
        var days = FreeDays(monday, x.Mine, x.Theirs, x.Bank);
        if (days.Count == 0)
            return (null, "no day that week when neither of you is off");
        var theirBusy = x.Schedules.Week(x.Report.Email, monday, (start, end));
        if (theirBusy is null)
            return (null, $"couldn't read {x.Report.Name}'s calendar");
        var slot = Nearest(x.Settings, start, end - start, days, [.. Busy(x.Events, meeting), .. theirBusy], x.Now);
        return (slot, slot is not null ? null : "no time that week when you're both free");
    }

    private static string G(double x) => Py.G(x);

    /// <summary>What to do with each 1:1 occurrence someone is off for. Reads only; changes nothing.</summary>
    public static List<Decision> Plan(Settings settings, ICalendar calendar, DateTime? now = null, long? daysAhead = null)
    {
        var at = now ?? Clock.Now;
        var config = settings.OneToOnes!;
        var ahead = daysAhead is { } d and not 0 ? d : config.DaysAhead is { } c and not 0 ? c : settings.Invites!.DaysAhead;
        var today = DateOnly.FromDateTime(at);
        var first = Monday(today);
        var last = Monday(today.AddDays((int)ahead)).AddDays(14);   // a week either side, to suggest
        var events = calendar.Events(first, last.DayNumber - first.DayNumber);
        var bank = BankHolidays(settings, events);
        var mine = MyLeave(settings, events);
        var midday = PyTime.TimeFromIso(config.Midday);
        var schedules = new Schedules(calendar);
        var output = new List<Decision>();
        foreach (var report in config.Reports)
        {
            var theirs = ReportLeave(settings, report, events);
            var x = new Context(settings, report, mine, theirs, bank, events, schedules, at);
            foreach (var meeting in events)
            {
                var (start, end) = InviteTriage.Times(meeting);
                var subject = meeting.StrOr("subject");
                if (!meeting.Truthy("isOrganizer") || meeting.Truthy("isCancelled") || meeting.Truthy("isAllDay")
                    || !PyRegex.Get(PyRegex.Escape(report.Subject), true).FullMatch(PyStr.Strip(subject))
                    || settings.Invites!.Protected.Any(p => PyRegex.IsMatch(p, subject, true))
                    || !InviteTriage.People(meeting).Select(InviteTriage.Address).Contains(report.Email)
                    || start <= at || DateOnly.FromDateTime(start) >= today.AddDays((int)ahead))
                    continue;
                var day = DateOnly.FromDateTime(start);
                var monday = Monday(day);
                var meHit = Hits(Off(mine, bank, day), start, end, midday);
                var themHit = Hits(Off(theirs, bank, day), start, end, midday);
                if (meHit != true && themHit != true)
                {
                    if (meHit is null || themHit is null)
                    {
                        var who = meHit is null ? "you have" : $"{report.Name} has";
                        output.Add(new Decision
                        {
                            Meeting = meeting, Report = report, Start = start, End = end, Action = "raise",
                            Why = $"{who} a half day {PyTime.Strftime(day, "%a %d %b")} and which half isn't known",
                            Suggest = Suggest(x, meeting, monday),
                        });
                    }
                    continue;
                }
                var counts = new Dictionary<string, double> { ["me"] = DaysOff(mine, bank, monday), ["them"] = DaysOff(theirs, bank, monday) };
                var absent = new[] { ("me", meHit), ("them", themHit) }.Where(p => p.Item2 == true).Select(p => p.Item1).ToList();
                var reason = bank.Contains(day) ? "the bank holiday"
                    : string.Join("; ", absent.Select(p => p == "me" ? "you're on leave" : $"{report.Name} is on leave"));
                var week = string.Join(", ", absent.Select(p => $"{(p == "me" ? "you" : PyStr.Split(report.Name)[0])} off {G(counts[p])} of {Weekdays} days that week"));
                var why = $"{reason} {PyTime.Strftime(day, "%a %d %b")} ({week})";
                var heavy = absent.Where(p => counts[p] >= config.CancelDays).ToList();
                if (heavy.Count > 0)
                {
                    var note = bank.Contains(day) ? config.BankHolidayNote : heavy.Contains("me") ? config.MyNote : config.ReportNote;
                    output.Add(new Decision { Meeting = meeting, Report = report, Start = start, End = end, Action = "cancel", Why = why, Note = note });
                    continue;
                }
                var (slot, problem) = Slot(x, meeting, monday);
                if (slot is not null)
                    output.Add(new Decision { Meeting = meeting, Report = report, Start = start, End = end, Action = "move", Why = why, To = slot });
                else
                    output.Add(new Decision
                    {
                        Meeting = meeting, Report = report, Start = start, End = end, Action = "raise", Why = $"{why}: {problem}",
                        Suggest = Suggest(x, meeting, monday),
                    });
            }
        }
        return output.OrderBy(d => d.Start).ToList();
    }

    /// <summary>For one that can't be settled within the rules: cancel, or the nearest free slot a week either side.</summary>
    private static string Suggest(Context x, JsonObject meeting, DateOnly monday)
    {
        var (start, _) = InviteTriage.Times(meeting);
        var found = new List<(DateTime Start, DateTime End)>();
        foreach (var other in new[] { monday.AddDays(-7), monday.AddDays(7) })
            if (Slot(x, meeting, other).Slot is { } slot)
                found.Add(slot);
        if (found.Count == 0)
            return "cancel it (no free slot in the week either side either)";
        var best = found.MinBy(s => (Math.Abs((s.Start - start).Ticks), -s.Start.Ticks));
        return $"cancel it, or move it to {InviteTriage.Day(best.Start)}–{InviteTriage.Hm(best.End)} (outside its week)";
    }

    public static string Describe(Decision d)
    {
        var when = $"{InviteTriage.Day(d.Start)}–{InviteTriage.Hm(d.End)}";
        return d.Action switch
        {
            "cancel" => $"{when}  {d.Report.Name}: CANCEL ({d.Why}) note: \"{d.Note}\"",
            "move" => $"{when}  {d.Report.Name}: MOVE to {InviteTriage.Day(d.To!.Value.Start)}–{InviteTriage.Hm(d.To.Value.End)} ({d.Why})",
            _ => $"{when}  {d.Report.Name}: ASK ({d.Why}). Suggest: {d.Suggest}",
        };
    }

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Act on Plan() (or, with act = false, record what it would do). (queue items, acted, keys seen).</summary>
    public static (List<JsonObject> Items, List<JsonObject> Acted, HashSet<string> Seen) Run(Settings settings, ICalendar calendar, JsonObject raised,
        Action<string> log, bool dryRun = false, DateTime? now = null)
    {
        var config = settings.OneToOnes!;
        var items = new List<JsonObject>();
        var acted = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changes = 0;
        foreach (var d in Plan(settings, calendar, now))
        {
            var meeting = d.Meeting;
            var subject = $"{Py.Str(meeting.Get("subject"))}: {InviteTriage.Day(d.Start)}–{InviteTriage.Hm(d.End)}";
            if (d.Action == "raise")
            {
                var k = $"one-to-one:{meeting.Req("id")}@{Stamp(d.Start)}";
                seen.Add(k);
                if (!raised.ContainsKey(k))
                {
                    items.Add(new JsonObject
                    {
                        ["folder"] = "Calendar", ["type"] = "one-to-one", ["id"] = meeting.Req("id"), ["key"] = k,
                        ["received"] = PyTime.Iso(d.Start, "minutes"), ["from"] = d.Report.Name, ["subject"] = subject, ["preview"] = "",
                        ["rule"] = "1:1 and leave", ["check"] = $"DECIDE: {d.Why}. Suggest: {d.Suggest}", ["webLink"] = meeting.Get("webLink").Copy(),
                    });
                    raised[k] = PyTime.Iso(Clock.Now, "seconds");
                    log($"{(dryRun ? "would queue" : "queued")}  [1:1 and leave] {Describe(d)}");
                }
                continue;
            }
            var did = d.Action == "cancel" ? $"{d.Why}; note: \"{d.Note}\""
                : $"{d.Why}; moved to {InviteTriage.Day(d.To!.Value.Start)}–{InviteTriage.Hm(d.To.Value.End)}";
            var would = $"one-to-one-would:{meeting.Req("id")}@{Stamp(d.Start)}:{d.Action}" + (d.Action == "move" ? $"@{Stamp(d.To!.Value.Start)}" : "");
            if (!config.Act && raised.ContainsKey(would))
                continue;   // already recorded as a would-do
            if (changes >= config.MaxActions)
            {
                log($"one-to-ones: max_actions ({config.MaxActions}) reached; the rest wait for the next run");
                break;
            }
            changes++;
            var verb = d.Action == "cancel" ? "cancelled" : "moved";
            string rule;
            if (config.Act && !dryRun)
            {
                if (d.Action == "cancel")
                    calendar.Cancel(meeting.Req("id"), d.Note!);     // the occurrence: the series is untouched
                else
                    calendar.Move(meeting.Req("id"), d.To!.Value.Start, d.To.Value.End);
                rule = $"1:1 {verb}: leave";
            }
            else
            {
                rule = !config.Act ? $"1:1 would be {verb}: leave (act = false)" : $"1:1 {verb}: leave";
                if (!config.Act && !dryRun)
                    raised[would] = PyTime.Iso(Clock.Now, "seconds");
            }
            acted.Add(new JsonObject
            {
                ["received"] = PyTime.Iso(d.Start, "minutes"), ["from"] = d.Report.Name, ["subject"] = subject, ["rule"] = rule, ["why"] = did,
            });
            log($"{(dryRun || !config.Act ? "would be " + verb : verb)}  [1:1 and leave] {Describe(d)}");
        }
        return (items, acted, seen);
    }
}
