using System.Globalization;
using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>One decision about one 1:1 or catch-up occurrence (one_to_ones.plan): cancel, move or raise.</summary>
public sealed class Decision
{
    public required JsonObject Meeting { get; init; }

    /// <summary>The other person: the report, or (a catch-up) whoever the meeting is with.</summary>
    public required OneToOne Report { get; init; }

    /// <summary>A catch-up ([catch_ups], 1.26.0) rather than a 1:1.</summary>
    public bool CatchUp { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required string Action { get; init; }
    public required string Why { get; init; }
    public string? Note { get; init; }
    public (DateTime Start, DateTime End)? To { get; init; }
    public string? Suggest { get; init; }

    /// <summary>Whether a run acts on it (its group's act, or its [[catch_up]]'s), or only records it.</summary>
    public bool Act { get; init; }

    /// <summary>For a clash (when_double_booked, 1.26.0), not leave.</summary>
    public bool DoubleBooked { get; init; }

    /// <summary>max_moves_per_occurrence for a clash move.</summary>
    public long MaxMoves { get; init; } = 2;
}

/// <summary>Your weekly 1:1s with your reports, cancelled or moved when either of you is on leave (one_to_ones.py).
/// Nobody off at the meeting's time: nothing (a half day counts only if it overlaps). Someone off for at least
/// cancel_days of the week: cancelled, with a note. Otherwise moved, unasked, to the same time on the nearest day
/// you're both free, or the nearest slot in time. No slot: queued once with a suggestion. Never the series, never
/// anything protected. act = false only records what it would do. At most max_actions changes a run.
/// 1.26.0: when_shorter = "ask" queues the shorter cases instead of moving them; and catch-ups ([catch_ups]: recurring
/// meetings you organise with one other person in your organisation) are planned the same way, their leave read from
/// their own calendar (CatchUpRules), with their own settings.</summary>
public static class OneToOnePlanner
{
    public const string Full = "full", Am = "AM", Pm = "PM", Half = "half";   // Half: a half day, but which half isn't known
    public const int Weekdays = 5;

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

    /// <summary>Your bank holidays ([leave], the shared rules; 1.17.0).</summary>
    public static HashSet<DateOnly> BankHolidays(Settings settings, List<JsonObject> events) =>
        Leave.View(settings, events).Days.Where(kv => kv.Value.Kind == CalendarDays.DayOffKind.BankHoliday).Select(kv => kv.Key).ToHashSet();

    /// <summary>Your leave, {day: full | AM | PM | half} (1.17.0: [leave], the shared rules): a day of leave is full; a
    /// half day is the half it covers (its title, or its hours), or "half" when that isn't known. Bank holidays aren't
    /// here (BankHolidays).</summary>
    public static Dictionary<DateOnly, string> MyLeave(Settings settings, List<JsonObject> events)
    {
        var days = new Dictionary<DateOnly, string>();
        foreach (var (day, d) in Leave.View(settings, events).Days)
        {
            if (d.Kind == CalendarDays.DayOffKind.Leave)
                days[day] = Full;
            else if (d.Kind == CalendarDays.DayOffKind.HalfDay)
                days[day] = d.Half switch { CalendarDays.HalfOfDay.Am => Am, CalendarDays.HalfOfDay.Pm => Pm, _ => Half };
        }
        return days;
    }

    /// <summary>A report's leave, from your own entries named "&lt;Full Name&gt; annual leave" / "... on leave".</summary>
    public static Dictionary<DateOnly, string> ReportLeave(Settings settings, OneToOne report, List<JsonObject> events)
    {
        // The titles are background-tools-lib's ReportLeaveTitles (1.17.0): the one definition calendar-team-leave-sync writes.
        var days = new Dictionary<DateOnly, string>();
        foreach (var e in events)
        {
            if (Mine(settings, e) && CalendarDays.ReportLeaveTitles.Parse(e.StrOr("subject"), report.Name, settings.OneToOnes!.LeavePattern) is { } t)
                Spread(days, e, t.Each, t.First, t.Last);
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

    internal static DateOnly Monday(DateOnly day) => day.AddDays(-PyTime.Weekday(day));

    /// <summary>Your time that's taken: anything busy, out of office or tentative, other than the meeting itself.</summary>
    internal static List<(DateTime Start, DateTime End)> Busy(List<JsonObject> events, JsonObject meeting) =>
        events.Where(e => e.Req("id") != meeting.Req("id") && !e.Truthy("isCancelled") && e.Str("showAs") is { } s && Proposals.HoldsTime.Contains(s)
                          && e.Get("responseStatus").Str("response") != "declined")
            .Select(InviteTriage.Times).ToList();

    /// <summary>The free slot to move a meeting to: the same time of day on the nearest day, and only if that time isn't
    /// free on any of these days, the slot nearest in time. Within working hours; on a tie, the later one.</summary>
    public static (DateTime Start, DateTime End)? Nearest(Settings settings, DateTime start, TimeSpan length, List<DateOnly> days,
        List<(DateTime Start, DateTime End)> busy, DateTime now)
    {
        var options = FreeStarts(settings, start, length, days, busy, now);
        if (options.Count == 0)
            return null;
        var same = options.Where(t => t.TimeOfDay == start.TimeOfDay).ToList();
        var best = same.Count > 0
            ? same.MinBy(t => (Math.Abs(DateOnly.FromDateTime(t).DayNumber - DateOnly.FromDateTime(start).DayNumber), -t.Ticks))
            : options.MinBy(t => (Math.Abs((t - start).Ticks), -t.Ticks));
        return (best, best + length);
    }

    /// <summary>Every start, a quarter hour apart, where a meeting of this length fits on these days: within working
    /// hours, from now on, clear of everything busy, and not the meeting's own start. Shared with the double-booking
    /// rules (1.13.0), which choose among them in their own order.</summary>
    internal static List<DateTime> FreeStarts(Settings settings, DateTime start, TimeSpan length, IEnumerable<DateOnly> days,
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
        return options;
    }

    /// <summary>Each person's free/busy, a week at a time, fetched once a run. Null: it couldn't be read. Shared with the
    /// double-booking rules (1.13.0).</summary>
    internal sealed class Schedules(ICalendar calendar)
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
        HashSet<DateOnly> Bank, List<JsonObject> Events, Schedules Schedules, DateTime Now, CatchUpRules.Diaries Diaries);

    /// <summary>The free slot that week for both of you; <paramref name="alsoBusy"/> is time of theirs known to be taken
    /// (a clash), whatever free/busy's week view makes of it.</summary>
    private static ((DateTime Start, DateTime End)? Slot, string? Problem) Slot(Context x, JsonObject meeting, DateOnly monday,
        List<(DateTime Start, DateTime End)>? alsoBusy = null)
    {
        var (start, end) = InviteTriage.Times(meeting);
        var days = FreeDays(monday, x.Mine, x.Theirs, x.Bank);
        if (days.Count == 0)
            return (null, "no day that week when neither of you is off");
        var theirBusy = x.Schedules.Week(x.Report.Email, monday, (start, end));
        if (theirBusy is null)
            return (null, $"couldn't read {x.Report.Name}'s calendar");
        var slot = Nearest(x.Settings, start, end - start, days, [.. Busy(x.Events, meeting), .. theirBusy, .. alsoBusy ?? []], x.Now);
        return (slot, slot is not null ? null : "no time that week when you're both free");
    }

    private static string G(double x) => Py.G(x);

    /// <summary>The window a plan reads: from this week's Monday to a week past the last day ahead (to suggest).</summary>
    private static (DateOnly Today, DateOnly First, DateOnly Last) Window(DateTime at, long ahead)
    {
        var today = DateOnly.FromDateTime(at);
        return (today, Monday(today), Monday(today.AddDays((int)ahead)).AddDays(14));
    }

    /// <summary>What to do with each 1:1 occurrence someone is off for (or, with when_double_booked, double-booked at).
    /// Reads only; changes nothing.</summary>
    public static List<Decision> Plan(Settings settings, ICalendar calendar, DateTime? now = null, long? daysAhead = null)
    {
        var at = now ?? Clock.Now;
        var config = settings.OneToOnes!;
        // 1.14.0: with no days_ahead of its own, the invites' window (months_ahead, when set), not their days_ahead
        var ahead = daysAhead is { } d and not 0 ? d : config.DaysAhead is { } c and not 0 ? c : settings.Invites!.Days(at);
        var (today, first, last) = Window(at, ahead);
        var events = calendar.Events(first, last.DayNumber - first.DayNumber);
        var bank = BankHolidays(settings, events);
        var mine = MyLeave(settings, events);
        var midday = PyTime.TimeFromIso(config.Midday);
        var schedules = new Schedules(calendar);
        var diaries = new CatchUpRules.Diaries(calendar, first, last);
        var handling = config.Handling;
        var output = new List<Decision>();
        foreach (var report in config.Reports)
        {
            var theirs = ReportLeave(settings, report, events);
            var x = new Context(settings, report, mine, theirs, bank, events, schedules, at, diaries);
            foreach (var meeting in events)
            {
                var (start, _) = InviteTriage.Times(meeting);
                if (!IsOneToOne(settings, report, meeting) || start <= at || DateOnly.FromDateTime(start) >= today.AddDays((int)ahead))
                    continue;
                if (Decide(x, handling, false, meeting, midday) is { } decision)
                    output.Add(decision);
            }
        }
        return output.OrderBy(d => d.Start).ToList();
    }

    /// <summary>Whether a meeting is your 1:1 with this report: yours, timed, not cancelled, its exact subject, with them in
    /// it, and not protected.</summary>
    internal static bool IsOneToOne(Settings settings, OneToOne report, JsonObject meeting)
    {
        var subject = meeting.StrOr("subject");
        return meeting.Truthy("isOrganizer") && !meeting.Truthy("isCancelled") && !meeting.Truthy("isAllDay")
               && PyRegex.Get(PyRegex.Escape(report.Subject), true).FullMatch(PyStr.Strip(subject))
               && !settings.Invites!.Protected.Any(p => PyRegex.IsMatch(p, subject, true))
               && InviteTriage.People(meeting).Select(InviteTriage.Address).Contains(report.Email);
    }

    /// <summary>What to do with each catch-up occurrence ([catch_ups], 1.26.0) someone is off for or double-booked at, whose
    /// calendars couldn't be read (their leave isn't known), and the catch-ups found ("subject (name)"). Reads only;
    /// changes nothing.</summary>
    public static (List<Decision> Decisions, List<string> Unreadable, List<string> Watched) PlanCatchUps(Settings settings, ICalendar calendar,
        DateTime? now = null, long? daysAhead = null)
    {
        var at = now ?? Clock.Now;
        var config = settings.CatchUps!;
        var ahead = daysAhead is { } d and not 0 ? d
            : config.DaysAhead is { } c and not 0 ? c
            : settings.OneToOnes?.DaysAhead is { } o and not 0 ? o
            : settings.Invites!.Days(at);
        var (today, first, last) = Window(at, ahead);
        var events = calendar.Events(first, last.DayNumber - first.DayNumber);
        var bank = BankHolidays(settings, events);
        var mine = MyLeave(settings, events);
        var midday = PyTime.TimeFromIso(settings.OneToOnes?.Midday ?? "13:00");
        var schedules = new Schedules(calendar);
        var diaries = new CatchUpRules.Diaries(calendar, first, last);
        var leave = new Dictionary<string, Dictionary<DateOnly, string>?>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        var watched = new List<string>();
        var output = new List<Decision>();
        foreach (var meeting in events)
        {
            var (start, _) = InviteTriage.Times(meeting);
            if (start <= at || DateOnly.FromDateTime(start) >= today.AddDays((int)ahead) || CatchUpRules.Other(settings, meeting) is not { } other)
                continue;
            if (!leave.TryGetValue(other.Email, out var theirs))
            {
                theirs = CatchUpRules.TheirLeave(settings, diaries, other.Email, first, last, Leave.Midday(settings));
                leave[other.Email] = theirs;
                if (theirs is null)
                    unreadable.Add(other.Name);
            }
            var label = $"{meeting.StrOr("subject")} ({other.Name})";
            if (!watched.Contains(label))
                watched.Add(label);
            var person = new OneToOne(other.Name, other.Email, meeting.StrOr("subject"));
            var x = new Context(settings, person, mine, theirs ?? [], bank, events, schedules, at, diaries);
            if (Decide(x, CatchUpRules.Handling(settings, meeting, other.Email), true, meeting, midday) is { } decision)
                output.Add(decision);
        }
        return (output.OrderBy(x => x.Start).ToList(), unreadable, watched);
    }

    /// <summary>One occurrence: when someone's off at its time, cancel, move or raise; when nobody is, and the other person
    /// is double-booked (when_double_booked), move or raise; otherwise nothing (null).</summary>
    private static Decision? Decide(Context x, LeaveHandling g, bool catchUp, JsonObject meeting, TimeSpan midday)
    {
        var (start, end) = InviteTriage.Times(meeting);
        var report = x.Report;
        var (mine, theirs, bank) = (x.Mine, x.Theirs, x.Bank);
        var day = DateOnly.FromDateTime(start);
        var monday = Monday(day);
        Decision Make(string action, string why, string? note = null, (DateTime, DateTime)? to = null, string? suggest = null, bool clash = false) => new()
        {
            Meeting = meeting, Report = report, CatchUp = catchUp, Act = g.Act, Start = start, End = end, Action = action, Why = why, Note = note,
            To = to, Suggest = suggest, DoubleBooked = clash, MaxMoves = g.DoubleBooked.MaxMoves,
        };
        var meHit = Hits(Off(mine, bank, day), start, end, midday);
        var themHit = Hits(Off(theirs, bank, day), start, end, midday);
        if (meHit != true && themHit != true)
        {
            if (meHit is null || themHit is null)
            {
                var who = meHit is null ? "you have" : $"{report.Name} has";
                return Make("raise", $"{who} a half day {PyTime.Strftime(day, "%a %d %b")} and which half isn't known", suggest: Suggest(x, meeting, monday));
            }
            return DoubleBooked(x, g, meeting, Make);
        }
        var counts = new Dictionary<string, double> { ["me"] = DaysOff(mine, bank, monday), ["them"] = DaysOff(theirs, bank, monday) };
        var absent = new[] { ("me", meHit), ("them", themHit) }.Where(p => p.Item2 == true).Select(p => p.Item1).ToList();
        var reason = bank.Contains(day) ? "the bank holiday"
            : string.Join("; ", absent.Select(p => p == "me" ? "you're on leave" : $"{report.Name} is on leave"));
        var week = string.Join(", ", absent.Select(p => $"{(p == "me" ? "you" : PyStr.Split(report.Name)[0])} off {G(counts[p])} of {Weekdays} days that week"));
        var why = $"{reason} {PyTime.Strftime(day, "%a %d %b")} ({week})";
        var heavy = absent.Where(p => counts[p] >= g.CancelDays).ToList();
        if (heavy.Count > 0)
            return Make("cancel", why, note: bank.Contains(day) ? g.BankHolidayNote : heavy.Contains("me") ? g.MyNote : g.TheirNote);
        var (slot, problem) = Slot(x, meeting, monday);
        if (g.WhenShorter == "ask")
            return Make("raise", $"{why}: under {G(g.CancelDays)} days, so yours to decide (when_shorter = \"ask\")",
                suggest: slot is { } s ? $"move it to {InviteTriage.Day(s.Start)}–{InviteTriage.Hm(s.End)}, or cancel it" : Suggest(x, meeting, monday));
        return slot is not null ? Make("move", why, to: slot) : Make("raise", $"{why}: {problem}", suggest: Suggest(x, meeting, monday));
    }

    /// <summary>The other person double-booked at the occurrence (when_double_booked, 1.26.0), within double_booked_days of
    /// it: moved as a leave move is (with double_booked_note), or raised ("ask", or no free slot). Null: no clash, or not
    /// looked for.</summary>
    private static Decision? DoubleBooked(Context x, LeaveHandling g, JsonObject meeting,
        Func<string, string, string?, (DateTime, DateTime)?, string?, bool, Decision> make)
    {
        var settings = g.DoubleBooked;
        var (start, _) = InviteTriage.Times(meeting);
        if (settings.When == "ignore" || (start - x.Now).TotalDays > settings.Days)
            return null;
        if (Clash(x, meeting) is not { } clash)
            return null;
        var monday = Monday(DateOnly.FromDateTime(start));
        var why = $"{x.Report.Name} {clash.Reason}";
        var (slot, problem) = Slot(x, meeting, monday, clash.Busy);
        if (settings.When == "move" && slot is not null)
            return make("move", why, settings.Note.Length > 0 ? settings.Note : null, slot, null, true);
        var asked = settings.When == "ask" ? $"{why}: yours to decide (when_double_booked = \"ask\")" : $"{why}: {problem}";
        return make("raise", asked, null, null,
            slot is { } s ? $"move it to {InviteTriage.Day(s.Start)}–{InviteTriage.Hm(s.End)}, or leave it" : Suggest(x, meeting, monday), true);
    }

    /// <summary>Whether the other person is double-booked at the meeting: they've declined it (where their answer shows),
    /// or their calendar has something busy or out of office overlapping it besides the meeting itself (tentative and
    /// free don't count). The reason, and the clashing times. Null: no clash, or their calendar can't be read.</summary>
    private static (string Reason, List<(DateTime Start, DateTime End)> Busy)? Clash(Context x, JsonObject meeting)
    {
        var (start, end) = InviteTriage.Times(meeting);
        var attendee = InviteTriage.People(meeting).FirstOrDefault(a => InviteTriage.Address(a) == x.Report.Email);
        if (attendee?.Get("status").Str("response") == "declined")
            return ("has declined it", []);
        if (x.Diaries.Of(x.Report.Email) is not { } items)
            return null;
        var subject = meeting.StrOr("subject");
        var over = items.Where(i => i.Start < end && i.End > start && i.Status is "busy" or "oof").ToList();
        // The meeting itself, in their calendar: the same times, and the same subject where they can be seen
        var self = over.FirstOrDefault(i => i.Start == start && i.End == end && (i.Subject.Length == 0 || i.Subject == subject));
        if (self is not null)
            over.Remove(self);
        if (over.Count == 0)
            return null;
        var first = over.OrderBy(i => i.Start).First();
        var what = first.Subject.Length > 0 ? $" \"{first.Subject}\"" : "";
        return ($"is double-booked ({first.Status}{what} {InviteTriage.Hm(first.Start)}–{InviteTriage.Hm(first.End)})",
                over.Select(i => (i.Start, i.End)).ToList());
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
        var who = d.CatchUp ? $"{d.Report.Name} (catch-up \"{d.Meeting.StrOr("subject")}\")" : d.Report.Name;
        return d.Action switch
        {
            "cancel" => $"{when}  {who}: CANCEL ({d.Why}) note: \"{d.Note}\"",
            "move" => $"{when}  {who}: MOVE to {InviteTriage.Day(d.To!.Value.Start)}–{InviteTriage.Hm(d.To.Value.End)} ({d.Why})"
                      + (d.Note is { Length: > 0 } n ? $" note: \"{n}\"" : ""),
            _ => $"{when}  {who}: ASK ({d.Why}). Suggest: {d.Suggest}",
        };
    }

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>How a run treats one group's decisions: its cap, its keys and its words (whether it acts is each
    /// decision's own, as a [[catch_up]] may say).</summary>
    private sealed record Mode(long MaxActions, string Key, string Label, string Section);

    /// <summary>Act on Plan() (or, with act = false, record what it would do). (queue items, acted, keys seen).</summary>
    public static (List<JsonObject> Items, List<JsonObject> Acted, HashSet<string> Seen) Run(Settings settings, ICalendar calendar, JsonObject raised,
        Action<string> log, bool dryRun = false, DateTime? now = null)
    {
        var config = settings.OneToOnes!;
        return Apply(Plan(settings, calendar, now), new Mode(config.MaxActions, "one-to-one", "1:1", "one-to-ones"), calendar, raised, log, dryRun);
    }

    /// <summary>Act on PlanCatchUps() ([catch_ups], 1.26.0), with its own act and max_actions; as Run.</summary>
    public static (List<JsonObject> Items, List<JsonObject> Acted, HashSet<string> Seen) RunCatchUps(Settings settings, ICalendar calendar,
        JsonObject raised, Action<string> log, bool dryRun = false, DateTime? now = null)
    {
        var config = settings.CatchUps!;
        var (decisions, _, _) = PlanCatchUps(settings, calendar, now);
        return Apply(decisions, new Mode(config.MaxActions, "catch-up", "catch-up", "catch-ups"), calendar, raised, log, dryRun);
    }

    private static (List<JsonObject> Items, List<JsonObject> Acted, HashSet<string> Seen) Apply(List<Decision> decisions, Mode mode, ICalendar calendar,
        JsonObject raised, Action<string> log, bool dryRun)
    {
        var items = new List<JsonObject>();
        var acted = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changes = 0;
        foreach (var planned in decisions)
        {
            var d = planned;
            var meeting = d.Meeting;
            var subject = $"{Py.Str(meeting.Get("subject"))}: {InviteTriage.Day(d.Start)}–{InviteTriage.Hm(d.End)}";
            // A clash move counts against max_moves_per_occurrence; past it, it's asked instead
            var movesKey = $"{mode.Key}-clash-moves:{meeting.Req("id")}";
            var moves = raised.Get(movesKey) is JsonValue v && v.TryGetValue<long>(out var count) ? count : 0;
            if (d is { DoubleBooked: true, Action: "move" } && moves >= d.MaxMoves)
                d = new Decision
                {
                    Meeting = meeting, Report = d.Report, CatchUp = d.CatchUp, Act = d.Act, Start = d.Start, End = d.End, Action = "raise",
                    Why = $"{d.Why}: moved for a clash {moves} time(s) already (max_moves_per_occurrence)", DoubleBooked = true, MaxMoves = d.MaxMoves,
                    Suggest = $"move it to {InviteTriage.Day(d.To!.Value.Start)}–{InviteTriage.Hm(d.To.Value.End)}, or leave it",
                };
            if (d.Action == "raise")
            {
                var k = $"{mode.Key}:{meeting.Req("id")}@{Stamp(d.Start)}";
                seen.Add(k);
                if (!raised.ContainsKey(k))
                {
                    // A catch-up's item is a "one-to-one" too: a meeting of yours with one person, changed in Outlook, then kept.
                    items.Add(new JsonObject
                    {
                        ["folder"] = "Calendar", ["type"] = "one-to-one", ["id"] = meeting.Req("id"), ["key"] = k,
                        ["received"] = PyTime.Iso(d.Start, "minutes"), ["from"] = d.Report.Name, ["subject"] = subject, ["preview"] = "",
                        ["rule"] = $"{mode.Label} and {(d.DoubleBooked ? "a clash" : "leave")}", ["check"] = $"DECIDE: {d.Why}. Suggest: {d.Suggest}",
                        ["webLink"] = meeting.Get("webLink").Copy(),
                    });
                    raised[k] = PyTime.Iso(Clock.Now, "seconds");
                    log($"{(dryRun ? "would queue" : "queued")}  [{mode.Label} and {(d.DoubleBooked ? "a clash" : "leave")}] {Describe(d)}");
                }
                continue;
            }
            var cause = d.DoubleBooked ? "double-booked" : "leave";
            var did = d.Action == "cancel" ? $"{d.Why}; note: \"{d.Note}\""
                : $"{d.Why}; moved to {InviteTriage.Day(d.To!.Value.Start)}–{InviteTriage.Hm(d.To.Value.End)}" + (d.Note is { Length: > 0 } n ? $"; note: \"{n}\"" : "");
            var would = $"{mode.Key}-would:{meeting.Req("id")}@{Stamp(d.Start)}:{d.Action}" + (d.Action == "move" ? $"@{Stamp(d.To!.Value.Start)}" : "");
            if (!d.Act && raised.ContainsKey(would))
                continue;   // already recorded as a would-do
            var lost = $"{mode.Key}-unconfirmed:{meeting.Req("id")}@{Stamp(d.Start)}";
            if (raised.ContainsKey(lost))
                continue;   // a change whose answer was lost: it may have gone, so it's never made again
            if (changes >= mode.MaxActions)
            {
                log($"{mode.Section}: max_actions ({mode.MaxActions}) reached; the rest wait for the next run");
                break;
            }
            changes++;
            var verb = d.Action == "cancel" ? "cancelled" : "moved";
            string rule;
            string? unknown = null;
            if (d.Act && !dryRun)
            {
                unknown = Unconfirmed.Try(() =>
                {
                    if (d.Action == "cancel")
                        calendar.Cancel(meeting.Req("id"), d.Note!);     // the occurrence: the series is untouched
                    else
                        calendar.Move(meeting.Req("id"), d.To!.Value.Start, d.To.Value.End, d.Note ?? "");
                });
                if (unknown is not null)
                    raised[lost] = PyTime.Iso(Clock.Now, "seconds");
                if (d.DoubleBooked)
                    raised[movesKey] = moves + 1;
                rule = $"{mode.Label} {verb}: {cause}";
            }
            else
            {
                rule = !d.Act ? $"{mode.Label} would be {verb}: {cause} (act = false)" : $"{mode.Label} {verb}: {cause}";
                if (!d.Act && !dryRun)
                    raised[would] = PyTime.Iso(Clock.Now, "seconds");
            }
            var entry = new JsonObject
            {
                ["received"] = PyTime.Iso(d.Start, "minutes"), ["from"] = d.Report.Name, ["subject"] = subject, ["rule"] = rule, ["why"] = did,
            };
            if (unknown is not null)
                Unconfirmed.Mark(entry, unknown, rule);
            acted.Add(entry);
            log(unknown is not null ? $"{verb} may have gone, not resent  [{mode.Label} and {cause}] {Describe(d)}: {unknown}"
                : $"{(dryRun || !d.Act ? "would be " + verb : verb)}  [{mode.Label} and {cause}] {Describe(d)}");
        }
        return (items, acted, seen);
    }
}
