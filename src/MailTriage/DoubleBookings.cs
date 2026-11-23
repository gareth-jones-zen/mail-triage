using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>Two overlapping meetings (or a meeting and a fixed block), as InviteTriage.DoubleBooked finds them.</summary>
public sealed record Clash(Held A, Held B, string Key, DateTime When);

/// <summary>What the double-booking rules make of one clash: move or decline the other meeting, or queue the clash
/// (no rule, or a rule that can't act: Problem says why). "settled": its other meeting was already moved or declined
/// for another clash in the same run.</summary>
public sealed class Resolution
{
    public required Clash Clash { get; init; }
    public required string Action { get; init; }
    public DoubleBookingRule? Rule { get; init; }
    public Held? Keep { get; init; }
    public Held? Other { get; init; }
    public JsonObject? Meeting { get; init; }
    public (DateTime Start, DateTime End)? To { get; init; }
    public string? Problem { get; init; }
}

/// <summary>Double bookings settled by standing rules (1.13.0). Before a clash is queued, the first
/// [[double_booking_rule]] that fits it (one meeting matches keep; the other matches other and other_with) decides:
/// move the other, if you organise it, to the nearest slot you and everyone in it are free; or decline it, if someone
/// else organises it, with the rule's note. That occurrence only, never the series; each once (the
/// double_bookings_file); at most max_actions a run. act = false only records what it would do, and queues the clash
/// as before. Anything a rule can't do is queued, saying why.</summary>
public static class DoubleBookingPlanner
{
    private static string Stamp(DateTime t) => PyTime.Iso(t, "minutes");

    /// <summary>Who's in a meeting besides you: attendees (not rooms) and the organiser, each once, by address.</summary>
    internal static List<(string Name, string Address)> Others(Settings settings, JsonObject e)
    {
        var found = new List<(string Name, string Address)>();
        void Add(string name, string address)
        {
            if (address.Length > 0 && address != settings.Email && !found.Any(f => f.Address == address))
                found.Add((name, address));
        }
        foreach (var a in InviteTriage.People(e))
            Add(a.Get("emailAddress").StrOr("name"), InviteTriage.Address(a));
        var (name, address) = InviteTriage.Organiser(e);
        Add(name, address);
        return found;
    }

    private static bool Fits(Settings settings, DoubleBookingRule rule, Held keep, Held other, JsonObject? otherEvent)
    {
        if (!PyRegex.IsMatch(rule.Keep, keep.Subject, true) || (rule.Other is not null && !PyRegex.IsMatch(rule.Other, other.Subject, true)))
            return false;
        if (rule.OtherWith is null)
            return true;
        if (otherEvent is null)
            return false;
        var others = Others(settings, otherEvent);
        return others.Count == 1 && settings.Groups[rule.OtherWith].Contains(others[0].Address);
    }

    /// <summary>The first rule that fits the clash either way round: (rule, keep, other); keep and other are null when
    /// the rule fits it both ways round, so which stays isn't clear.</summary>
    internal static (DoubleBookingRule Rule, Held? Keep, Held? Other)? Match(Settings settings, Clash c, IReadOnlyDictionary<string, JsonObject> byId)
    {
        foreach (var rule in settings.DoubleBookings?.Rules ?? [])
        {
            var ab = Fits(settings, rule, c.A, c.B, byId.GetValueOrDefault(c.B.Id));
            var ba = Fits(settings, rule, c.B, c.A, byId.GetValueOrDefault(c.A.Id));
            if (ab && ba)
                return (rule, null, null);
            if (ab)
                return (rule, c.A, c.B);
            if (ba)
                return (rule, c.B, c.A);
        }
        return null;
    }

    /// <summary>One run's view: the calendar, days off, free/busy fetched once, and what this run has already moved.</summary>
    private sealed class Context
    {
        public Context(Settings settings, ICalendar calendar, List<JsonObject> events, DateTime now, JsonObject done)
        {
            Settings = settings;
            Events = events;
            Now = now;
            Done = done;
            foreach (var e in events)
                ById.TryAdd(e.Req("id"), e);
            Away = InviteTriage.DaysOff(settings, events).Away;
            Schedules = new OneToOnePlanner.Schedules(calendar);
            WindowEnd = DateOnly.FromDateTime(now).AddDays((int)settings.Invites!.Days(now));
        }

        public Settings Settings { get; }
        public List<JsonObject> Events { get; }
        public DateTime Now { get; }
        public JsonObject Done { get; }
        public Dictionary<string, JsonObject> ById { get; } = new(StringComparer.Ordinal);
        public HashSet<DateOnly> Away { get; }
        public OneToOnePlanner.Schedules Schedules { get; }

        /// <summary>The calendar read only reaches this far, so a move never lands beyond it.</summary>
        public DateOnly WindowEnd { get; }

        /// <summary>Slots taken by this run's moves, and the meetings moved or declined (their old times are free).</summary>
        public List<(DateTime Start, DateTime End)> Claimed { get; } = [];
        public Dictionary<string, Resolution> Settled { get; } = new(StringComparer.Ordinal);

        public void Settle(Resolution r)
        {
            Settled[r.Other!.Id] = r;
            if (r.To is { } to)
                Claimed.Add(to);
        }
    }

    /// <summary>The slot to move a meeting to, that week: the nearest in time on the same day; else the same time on
    /// the nearest other day; else the nearest in time. You and everyone in it free (busy, out of office and tentative
    /// hold time), within working_hours, not on your days off, from now on, the same length. On a tie, the later.</summary>
    private static ((DateTime Start, DateTime End)? Slot, string? Problem) Slot(Context x, JsonObject meeting)
    {
        var (start, end) = InviteTriage.Times(meeting);
        var day = DateOnly.FromDateTime(start);
        var monday = OneToOnePlanner.Monday(day);
        var days = Enumerable.Range(0, OneToOnePlanner.Weekdays).Select(monday.AddDays).Where(d => !x.Away.Contains(d) && d < x.WindowEnd).ToList();
        var mine = x.Events.Where(e => !x.Settled.ContainsKey(e.Req("id"))).ToList();
        var busy = OneToOnePlanner.Busy(mine, meeting);
        busy.AddRange(x.Claimed);
        foreach (var (name, address) in Others(x.Settings, meeting))
        {
            var theirs = x.Schedules.Week(address, monday, (start, end));
            if (theirs is null)
                return (null, $"couldn't read {(name.Length > 0 ? name : address)}'s calendar");
            busy.AddRange(theirs);
        }
        var options = OneToOnePlanner.FreeStarts(x.Settings, start, end - start, days, busy, x.Now);
        if (options.Count == 0)
            return (null, "no free slot that week");
        var sameDay = options.Where(t => DateOnly.FromDateTime(t) == day).ToList();
        var sameTime = options.Where(t => t.TimeOfDay == start.TimeOfDay).ToList();
        var pick = sameDay.Count > 0 ? sameDay.MinBy(t => (Math.Abs((t - start).Ticks), -t.Ticks))
            : sameTime.Count > 0 ? sameTime.MinBy(t => (Math.Abs(DateOnly.FromDateTime(t).DayNumber - day.DayNumber), -t.Ticks))
            : options.MinBy(t => (Math.Abs((t - start).Ticks), -t.Ticks));
        return ((pick, pick + (end - start)), null);
    }

    private static Resolution Decide(Context x, Clash c)
    {
        var match = Match(x.Settings, c, x.ById);
        if (match is null)
            return new Resolution { Clash = c, Action = "queue" };
        var (rule, keep, other) = match.Value;
        if (keep is null || other is null)
            return new Resolution { Clash = c, Rule = rule, Action = "queue", Problem = "it fits both meetings, so which one stays isn't clear" };
        var e = x.ById[other.Id];
        Resolution Queue(string why) => new() { Clash = c, Rule = rule, Keep = keep, Other = other, Meeting = e, Action = "queue", Problem = why };
        if (x.Settled.ContainsKey(other.Id))
            return new Resolution { Clash = c, Rule = rule, Keep = keep, Other = other, Meeting = e, Action = "settled" };
        if (x.Done.Get(other.Id) is JsonObject before)
            return Queue($"\"{other.Subject}\" was already {(before.Str("action") == "move" ? "moved" : "declined")} once by a rule "
                         + $"({before.StrOr("rule")}), so not again");
        if (rule.Action == "move")
        {
            if (!e.Truthy("isOrganizer"))
                return Queue($"\"{other.Subject}\" is someone else's meeting: only meetings you organise are moved");
            if (other.Kind != "your meeting")
                return Queue($"\"{other.Subject}\" is {(other.Kind == "protected" ? "protected" : $"a {other.Kind}")}: never moved by a rule");
            var (slot, problem) = Slot(x, e);
            return slot is { } s ? new Resolution { Clash = c, Rule = rule, Keep = keep, Other = other, Meeting = e, Action = "move", To = s } : Queue(problem!);
        }
        if (e.Truthy("isOrganizer"))
            return Queue($"you organise \"{other.Subject}\": only someone else's meeting is declined");
        if (other.Kind == "protected")
            return Queue($"\"{other.Subject}\" is protected: never declined by a rule");
        return new Resolution { Clash = c, Rule = rule, Keep = keep, Other = other, Meeting = e, Action = "decline" };
    }

    /// <summary>What a rule does (or would do), in a line.</summary>
    private static string Did(Resolution r, bool would) => r.Action == "move"
        ? $"{(would ? "would move" : "moved")} \"{r.Other!.Subject}\" to {InviteTriage.Day(r.To!.Value.Start)}–{InviteTriage.Hm(r.To.Value.End)}"
        : $"{(would ? "would decline" : "declined")} \"{r.Other!.Subject}\"" + (r.Rule!.Note.Length > 0 ? $" (note: \"{r.Rule.Note}\")" : "");

    /// <summary>One clash and what the rules make of it, for `mail-triage double-bookings`.</summary>
    public static string Describe(Resolution r)
    {
        var c = r.Clash;
        var head = $"{InviteTriage.Day(c.When)}  {c.A.Subject} / {c.B.Subject}: ";
        return r.Action switch
        {
            "move" => head + $"MOVE \"{r.Other!.Subject}\" to {InviteTriage.Day(r.To!.Value.Start)}–{InviteTriage.Hm(r.To.Value.End)} [{r.Rule!.Name}]",
            "decline" => head + $"DECLINE \"{r.Other!.Subject}\"" + (r.Rule!.Note.Length > 0 ? $" (note: \"{r.Rule.Note}\")" : "") + $" [{r.Rule.Name}]",
            "settled" => head + $"settled with another clash (\"{r.Other!.Subject}\" goes) [{r.Rule!.Name}]",
            _ when r.Rule is not null => head + $"QUEUE [{r.Rule.Name}]: {r.Problem}",
            _ => head + "QUEUE (no rule)",
        };
    }

    /// <summary>What each upcoming clash would come to with the rules. Reads only; changes nothing.</summary>
    public static List<Resolution> Plan(Settings settings, ICalendar calendar, DateTime? now = null)
    {
        var at = now ?? Clock.Now;
        var events = calendar.Events(DateOnly.FromDateTime(at), settings.Invites!.Days(at));
        var x = new Context(settings, calendar, events, at, StateFiles.LoadObject(settings.DoubleBookingsFile, () => []));
        var output = new List<Resolution>();
        foreach (var c in InviteTriage.DoubleBooked(settings, events, at))
        {
            var r = Decide(x, c);
            if (r.Action is "move" or "decline")
                x.Settle(r);
            output.Add(r);
        }
        return output;
    }

    /// <summary>Past occurrences are forgotten.</summary>
    private static void Forget(JsonObject done, DateTime at)
    {
        var today = PyTime.Iso(at.Date, "minutes");
        foreach (var (key, value) in done.ToList())
            if (PyStr.CompareCodePoints(value.Str("start") ?? "", today) < 0 && PyStr.CompareCodePoints(value.Str("to") ?? "", today) < 0)
                done.Remove(key);
    }

    /// <summary>Queue each double booking once, unless a rule settles it (or, with act = false, record once what the
    /// rule would do, and queue it as before). calendar null, or no rules: everything is queued, as in 1.11.0.
    /// done: the double_bookings_file, updated in place (not in a dry run). (queue items, what was done, keys seen.)</summary>
    public static (List<JsonObject> Items, List<JsonObject> Acted, HashSet<string> Seen) Run(Settings settings, ICalendar? calendar,
        List<JsonObject> events, JsonObject raised, Action<string> log, DateTime at, JsonObject? done = null, bool dryRun = false)
    {
        var items = new List<JsonObject>();
        var acted = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var config = settings.DoubleBookings;
        done ??= [];
        if (!dryRun)
            Forget(done, at);
        var x = calendar is not null && config is { Rules.Count: > 0 } ? new Context(settings, calendar, events, at, done) : null;
        var changes = 0;
        void Queue(Clash c, string? note)
        {
            seen.Add(c.Key);
            if (raised.ContainsKey(c.Key))
                return;
            items.Add(InviteTriage.DoubleBookingItem(c, note));
            raised[c.Key] = PyTime.Iso(Clock.Now, "seconds");
            log($"double booking queued  {c.A.Subject} / {c.B.Subject} ({InviteTriage.Day(c.When)})");
        }
        foreach (var c in InviteTriage.DoubleBooked(settings, events, at))
        {
            var r = x is null ? null : Decide(x, c);
            if (r is null || r.Action == "queue")
            {
                Queue(c, r?.Rule is { } rule ? $"Rule \"{rule.Name}\" didn't act: {r.Problem}" : null);
                continue;
            }
            if (r.Action == "settled")
            {
                if (!config!.Act)   // the preview of the other clash doesn't settle this one
                    Queue(c, $"Rule \"{r.Rule!.Name}\" {Did(x!.Settled[r.Other!.Id], true)} (act = false)");
                continue;
            }
            var other = r.Other!;
            var would = $"would:{other.Id}@{Stamp(other.Start)}:{r.Action}" + (r.To is { } t ? $"@{Stamp(t.Start)}" : "");
            if (!config!.Act && done.ContainsKey(would))
            {
                x!.Settle(r);
                Queue(c, $"Rule \"{r.Rule!.Name}\" {Did(r, true)} (act = false)");
                continue;
            }
            if (changes >= config.MaxActions)
            {
                if (changes++ == config.MaxActions)
                    log($"double bookings: max_actions ({config.MaxActions}) reached; the rest wait for the next run");
                seen.Add(c.Key);
                continue;
            }
            changes++;
            x!.Settle(r);
            var record = new JsonObject
            {
                ["action"] = r.Action, ["rule"] = r.Rule!.Name, ["subject"] = other.Subject, ["start"] = Stamp(other.Start),
                ["to"] = r.To is { } to ? Stamp(to.Start) : null, ["at"] = PyTime.Iso(Clock.Now, "seconds"),
            };
            string? unknown = null;
            if (config.Act)
            {
                if (!dryRun)
                {
                    unknown = Unconfirmed.Try(() =>
                    {
                        if (r.Action == "move")
                            calendar!.Move(other.Id, r.To!.Value.Start, r.To.Value.End);   // the occurrence: the series is untouched
                        else
                        {
                            var send = !r.Meeting!.Has("responseRequested") || Py.Truthy(r.Meeting.Get("responseRequested"));
                            calendar!.Respond(other.Id, "decline", r.Rule.Note, send);   // the occurrence: the series is untouched
                        }
                    });
                    if (unknown is not null)
                        record["unconfirmed"] = unknown;
                    done[other.Id] = record;   // even when the answer was lost: never made twice
                }
                raised[c.Key] = PyTime.Iso(Clock.Now, "seconds");   // settled: off the queue, as nothing adds it to seen
            }
            else
            {
                if (!dryRun)
                    done[would] = record;
                Queue(c, $"Rule \"{r.Rule.Name}\" {Did(r, true)} (act = false)");
            }
            var (oname, oaddress) = InviteTriage.Organiser(r.Meeting!);
            var label = config.Act ? (r.Action == "move" ? "Double booking: moved" : "Double booking: declined")
                : (r.Action == "move" ? "Double booking (preview): would move" : "Double booking (preview): would decline");
            var entry = new JsonObject
            {
                ["received"] = Stamp(other.Start),
                ["from"] = r.Action == "move" ? string.Join(", ", Others(settings, r.Meeting!).Select(o => o.Name.Length > 0 ? o.Name : o.Address))
                    : oname.Length > 0 ? oname : oaddress,
                ["subject"] = $"{other.Subject}: {InviteTriage.Day(other.Start)}–{InviteTriage.Hm(other.End)}",
                ["rule"] = label,
                ["why"] = $"[{r.Rule.Name}] {Did(r, !config.Act)}, as it clashed with \"{r.Keep!.Subject}\"",
            };
            if (unknown is not null)
                Unconfirmed.Mark(entry, unknown, label);
            acted.Add(entry);
            log(unknown is not null ? $"double booking: {Did(r, true)} may have gone, not resent  [{r.Rule.Name}]: {unknown}"
                : $"{(dryRun || !config.Act ? "double booking: " + Did(r, true) : "double booking: " + Did(r, false))}  [{r.Rule.Name}]");
        }
        return (items, acted, seen);
    }
}
