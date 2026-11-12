using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage;

/// <summary>Something already holding time in the calendar (invites.commitments), or a clash taken from it.</summary>
public sealed class Held
{
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required string Kind { get; set; }
    /// <summary>"start" or "end" for travel at either end of the day.</summary>
    public string? Edge { get; set; }
    /// <summary>Where it could move to (a clash only).</summary>
    public (DateTime Start, DateTime End)? To { get; set; }

    public Held Copy() => new() { Id = Id, Subject = Subject, Start = Start, End = End, Kind = Kind, Edge = Edge, To = To };
}

/// <summary>The recommendation for one invite (invites.assess).</summary>
public sealed record Verdict(string Recommendation, string Why, List<string> Decline, string Note, JsonArray Clashes);

/// <summary>Meeting invites: raised with a recommendation, answered on your say-so (invites.py). Judged on time and
/// context, not sender: leave, a clash with something fixed (decline), clashes only with what can move (accept and
/// move), markers that hold no time (accept), otherwise accept with notes. accept_shortened answers on its own a
/// meeting you'd accepted that was only shortened. A decline is for the instance, never the series.</summary>
public static class InviteTriage
{
    public static readonly string[] Pending = ["none", "notResponded"];
    public static readonly string[] Committed = ["accepted", "tentativelyAccepted", "organizer"];
    public static readonly string[] MovableKinds = ["your block", "your meeting", "travel"];
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    internal static string Hm(DateTime t) => PyTime.Strftime(t, "%H:%M");
    internal static string Day(DateTime t) => PyTime.Strftime(t, "%a %d %b %H:%M");

    public static (DateTime Start, DateTime End) Times(JsonObject e) =>
        (PyTime.FromIso(e.Get("start").Req("dateTime")[..Math.Min(19, e.Get("start").Req("dateTime").Length)]),
         PyTime.FromIso(e.Get("end").Req("dateTime")[..Math.Min(19, e.Get("end").Req("dateTime").Length)]));

    public static List<JsonObject> People(JsonObject e) =>
        e.Get("attendees").Objects().Where(a => a.Str("type") != "resource").ToList();

    internal static string Address(JsonObject attendee) => (attendee.Get("emailAddress").Str("address") ?? "").ToLowerInvariant();

    public static (string Name, string Address) Organiser(JsonObject e)
    {
        var who = e.Get("organizer").Get("emailAddress");
        return (who.StrOr("name"), (who.Str("address") ?? "").ToLowerInvariant());
    }

    private static bool Any(IEnumerable<string> patterns, string text) => patterns.Any(p => PyRegex.IsMatch(p, text, true));

    /// <summary>required, optional, or group (you're not on the list by name: a distribution list).</summary>
    public static string MyPlace(JsonObject e, string? email)
    {
        var mine = People(e).FirstOrDefault(a => Address(a) == email);
        return mine is null ? "group" : mine.Has("type") ? Py.Str(mine.Get("type")) : "required";
    }

    private static IEnumerable<DateOnly> DaysOf(DateTime start, DateTime end)
    {
        var last = DateOnly.FromDateTime(end - TimeSpan.FromMinutes(1));
        for (var day = DateOnly.FromDateTime(start); day <= last; day = day.AddDays(1))
            yield return day;
    }

    internal static IEnumerable<DateOnly> SpanDays(JsonObject e)
    {
        var (start, end) = Times(e);
        return DaysOf(start, end);
    }

    public static HashSet<DateOnly> LeaveDays(Settings settings, List<JsonObject> events)
    {
        var pattern = PyRegex.Get(settings.Invites!.LeavePattern, true);
        var days = new HashSet<DateOnly>();
        foreach (var e in events)
        {
            var (_, address) = Organiser(e);
            if (address != settings.Email || e.Truthy("isCancelled") || pattern.MatchStart(e.StrOr("subject")) is null)
                continue;
            days.UnionWith(SpanDays(e));
        }
        return days;
    }

    /// <summary>"On annual leave", or "On annual leave 16/12 to 31/12" for leave longer than a day. A weekend inside
    /// the leave doesn't break it; one at either end isn't counted.</summary>
    public static string LeaveNote(Settings settings, DateOnly day, HashSet<DateOnly> away)
    {
        bool Inside(DateOnly d) => away.Contains(d) || (PyTime.Weekday(d) >= 5
            && (away.Contains(d.AddDays(-2)) || away.Contains(d.AddDays(-1))) && (away.Contains(d.AddDays(1)) || away.Contains(d.AddDays(2))));
        var first = day;
        var last = day;
        while (Inside(first.AddDays(-1)))
            first = first.AddDays(-1);
        while (Inside(last.AddDays(1)))
            last = last.AddDays(1);
        var note = settings.Invites!.LeaveNote;
        var format = settings.Invites.DateFormat;
        return first == last ? note : $"{note} {PyTime.Strftime(first, format)} to {PyTime.Strftime(last, format)}";
    }

    public static bool Matches(InviteRule rule, JsonObject e, string? email)
    {
        var (name, address) = Organiser(e);
        return (rule.Subject is null || PyRegex.IsMatch(rule.Subject, e.StrOr("subject"), true))
               && (rule.Organiser is null || PyRegex.IsMatch(rule.Organiser, $"{name} {address}", true))
               && (rule.AllDay is null || e.Truthy("isAllDay") == rule.AllDay)
               && (rule.ShowAs is null || (e.Str("showAs") is { } showAs && rule.ShowAs.Contains(showAs)))
               && (rule.ViaGroup is null || (MyPlace(e, email) == "group") == rule.ViaGroup)
               && (rule.Optional is null || (MyPlace(e, email) == "optional") == rule.Optional)
               && (rule.MinPeople is null || People(e).Count >= rule.MinPeople);
    }

    /// <summary>What you've already given time to: meetings you organise or have accepted (tentative counts), and your
    /// own blocks. All-day markers and anything shown as free don't hold time.</summary>
    public static List<Held> Commitments(Settings settings, List<JsonObject> events)
    {
        var invites = settings.Invites!;
        var output = new List<Held>();
        foreach (var e in events)
        {
            var response = e.Get("responseStatus").Str("response");
            if (e.Truthy("isCancelled") || e.Truthy("isAllDay") || e.Str("showAs") == "free" || response is null || !Committed.Contains(response))
                continue;
            var subject = e.StrOr("subject");
            var own = e.Truthy("isOrganizer");
            var block = own && !People(e).Any(a => Address(a) != settings.Email);
            string kind;
            if (Any(invites.Protected, subject))
                kind = "protected";
            else if (block && Any(invites.CommuteBlocks, subject))
                kind = "travel";   // decided below: movable only at either end of the day
            else if (block && (Any(invites.FixedBlocks, subject) || e.Str("showAs") == "oof"))
                kind = "fixed block";
            else if (block)
                kind = "your block";
            else if (own)
                kind = "your meeting";
            else
                kind = response == "tentativelyAccepted" ? "tentative" : "accepted";
            var (start, end) = Times(e);
            output.Add(new Held { Id = e.Req("id"), Subject = subject, Start = start, End = end, Kind = kind });
        }
        // Travel at the start or end of the day can move (come in earlier, leave later); in the middle, it can't.
        foreach (var c in output)
        {
            if (c.Kind != "travel")
                continue;
            var date = c.Start.Date;
            var day = output.Where(o => o.Start.Date == date && o.End.Date == date).ToList();
            if (day.Count == 0)
                throw new InvalidOperationException("min() arg is an empty sequence");   // as Python fails on travel past midnight
            if (c.Start <= day.Min(o => o.Start))
                c.Edge = "start";
            else if (c.End >= day.Max(o => o.End))
                c.Edge = "end";
            else
                c.Kind = "travel mid-day";
        }
        return output;
    }

    public static List<Held> Clashes(JsonObject e, List<Held> held)
    {
        var (start, end) = Times(e);
        var id = e.Str("id");
        return held.Where(c => c.Id != id && c.Start < end && c.End > start).ToList();
    }

    private static DateTime At(DateOnly day, string time) => day.ToDateTime(TimeOnly.MinValue) + PyTime.TimeFromIso(time);

    /// <summary>Where a movable clash could go: the nearest free slot of the same length, within its limits.</summary>
    public static (DateTime Start, DateTime End)? FreeSlot(Settings settings, Held c, List<Held> held, (DateTime Start, DateTime End) avoid,
        HashSet<DateOnly> away, DateTime now)
    {
        var invites = settings.Invites!;
        var length = c.End - c.Start;
        var limit = invites.Movable.FirstOrDefault(m => PyRegex.IsMatch(m.Subject, c.Subject, true));
        DateTime? deadline = null;
        var startDay = DateOnly.FromDateTime(c.Start);
        var days = new List<DateOnly> { startDay };
        var hours = c.Kind == "travel" ? invites.TravelHours : invites.WorkingHours;
        if (c.Kind != "travel" && !(limit is not null && limit.Within == "day"))
        {
            if (limit is not null && limit.Before is { Length: > 0 } before)
            {
                var later = held.Where(h => h.Start > c.Start && PyRegex.IsMatch(before, h.Subject, true)).Select(h => h.Start).ToList();
                deadline = later.Count > 0 ? later.Min() : null;
                if (deadline is { } d)
                    days = Enumerable.Range(0, DateOnly.FromDateTime(d).DayNumber - startDay.DayNumber + 1).Select(startDay.AddDays).ToList();
            }
            else
                days.AddRange(Enumerable.Range(1, 7).Select(startDay.AddDays));
        }
        var busy = held.Where(h => h.Id != c.Id).Select(h => (h.Start, h.End)).Append(avoid).ToList();
        foreach (var day in days)
        {
            if (PyTime.Weekday(day) >= 5 || away.Contains(day))
                continue;
            var openAt = At(day, hours.Start);
            var closeAt = At(day, hours.End);   // "24:00" fails, as it does in Python (a quirk kept for parity)
            var options = new List<DateTime>();
            for (var t = openAt; t + length <= closeAt; t += Step)
            {
                // Travel in the morning only moves earlier (come in earlier); in the evening, only later (leave later).
                var direction = c.Edge switch { "start" => t < c.Start, "end" => t > c.Start, _ => true };
                if (t >= now && direction && (deadline is null || t + length <= deadline) && t != c.Start
                    && !busy.Any(b => b.Start < t + length && b.End > t))
                    options.Add(t);
            }
            if (options.Count > 0)
            {
                var best = options.MinBy(t => Math.Abs((t - c.Start).Ticks));
                return (best, best + length);
            }
        }
        return null;
    }

    private static string Label(Held c)
    {
        var moved = c.To is { } to ? $" -> {Day(to.Start)}–{Hm(to.End)}" : "";
        return $"{c.Subject} ({c.Kind}, {Day(c.Start)}–{Hm(c.End)}{moved})";
    }

    private static string Sorted(IEnumerable<string> labels) => string.Join("; ", labels.Distinct().Order(PyText.Order));

    /// <summary>The recommendation for one invite (for a series, across its occurrences in the window).</summary>
    public static Verdict Assess(Settings settings, JsonObject e, List<Held> held, HashSet<DateOnly> away, List<JsonObject> seriesEvents,
        DateTime? now = null)
    {
        var at = now ?? Clock.Now;
        var occurrences = seriesEvents.Count > 0 ? seriesEvents : [e];
        var onLeave = occurrences.Where(o => away.Contains(DateOnly.FromDateTime(Times(o).Start))).ToList();
        var hits = new List<(JsonObject O, Held C)>();
        foreach (var o in occurrences)
        {
            foreach (var original in Clashes(o, held))
            {
                var c = original.Copy();
                if (MovableKinds.Contains(c.Kind))
                {
                    if (FreeSlot(settings, c, held, Times(o), away, at) is { } slot)
                        c.To = slot;
                    else
                        c.Kind += ", no free slot to move it to";
                }
                hits.Add((o, c));
            }
        }
        var fixedHits = hits.Where(h => h.C.To is null).ToList();
        var movable = hits.Where(h => h.C.To is not null).ToList();
        var notes = new List<string>();
        var place = MyPlace(e, settings.Email);
        if (place != "required")
            notes.Add(place == "optional" ? "you're optional" : "you're invited through a group");
        if (e.Str("type") is "occurrence" or "exception" or "seriesMaster")
            notes.Add($"a recurring series ({occurrences.Count} in the next {settings.Invites!.DaysAhead} days)");
        if (People(e).Count > 50)
            notes.Add($"a broadcast ({People(e).Count} people)");
        var (_, address) = Organiser(e);
        if (settings.Email is { Length: > 0 } email && address.Length > 0 && address.Split('@')[^1] != email.Split('@')[^1])
            notes.Add("an outside organiser");

        var decline = new List<string>();
        var note = "";
        string rec, why;
        int Distinct(IEnumerable<(JsonObject O, Held C)> list) => list.Select(h => h.O).Distinct(ReferenceEqualityComparer.Instance).Count();
        if (e.Truthy("isAllDay") || e.Str("showAs") == "free")
        {
            // A marker or a notice (a deadline, someone's leave): it holds no time, so nothing clashes with it.
            (rec, why) = ("accept", "holds no time (all-day or shown as free): a marker, not a meeting");
            hits = [];
        }
        else if (onLeave.Count > 0 && onLeave.Count == occurrences.Count)
        {
            (rec, why) = ("decline", "you're on leave");
            decline = occurrences.Select(o => o.Req("id")).ToList();
            note = LeaveNote(settings, DateOnly.FromDateTime(Times(onLeave[0]).Start), away);
        }
        else if (fixedHits.Count > 0 && Distinct(fixedHits) == occurrences.Count)
        {
            (rec, why) = ("decline", "clashes with " + Sorted(fixedHits.Select(h => Label(h.C))));
            decline = occurrences.Select(o => o.Req("id")).ToList();
        }
        else if (movable.Count > 0 && fixedHits.Count == 0 && onLeave.Count == 0)
            (rec, why) = ("accept and move", "clashes only with things you can move: " + Sorted(movable.Select(h => Label(h.C))));
        else if (fixedHits.Count > 0 || onLeave.Count > 0)
        {
            var part = new List<string>();
            if (onLeave.Count > 0)
                part.Add($"{onLeave.Count} on leave");
            if (fixedHits.Count > 0)
                part.Add($"{Distinct(fixedHits)} clash: " + Sorted(fixedHits.Select(h => Label(h.C))));
            (rec, why) = ("accept, and decline the clashing ones", $"of {occurrences.Count} occurrences, " + string.Join("; ", part));
            decline = onLeave.Select(o => o.Req("id")).Concat(fixedHits.Select(h => h.O.Req("id"))).Distinct().Order(PyText.Order).ToList();
            note = onLeave.Count > 0 && fixedHits.Count == 0 ? LeaveNote(settings, DateOnly.FromDateTime(Times(onLeave[0]).Start), away) : "";
        }
        else
            (rec, why) = ("accept", "no clash");
        var clashes = new JsonArray();
        foreach (var (_, c) in hits)
        {
            var o = new JsonObject
            {
                ["id"] = c.Id, ["subject"] = c.Subject, ["start"] = PyTime.Iso(c.Start, "minutes"), ["end"] = PyTime.Iso(c.End, "minutes"), ["kind"] = c.Kind,
            };
            if (c.To is { } to)
            {
                o["to"] = PyTime.Iso(to.Start, "minutes");
                o["to_end"] = PyTime.Iso(to.End, "minutes");
            }
            clashes.Add((JsonNode)o);
        }
        return new Verdict(rec, why + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : ""), decline, note, clashes);
    }

    private static string Start16(JsonObject e) => e.Get("start").Req("dateTime") is var s && s.Length > 16 ? s[..16] : e.Get("start").Req("dateTime");
    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>A series is raised once; a one-off once per start time, so a moved meeting comes back.</summary>
    public static string Key(JsonObject e) =>
        e.Truthy("seriesMasterId") ? $"invite:{Py.Str(e.Get("seriesMasterId"))}" : $"invite:{e.Req("id")}@{Start16(e)}";

    /// <summary>Occurrences of a series the organiser changed on their own.</summary>
    public static List<string> Exceptions(List<JsonObject> group) =>
        group.Where(e => e.Truthy("seriesMasterId") && e.Str("type") == "exception").Select(e => e.Req("id")).ToList();

    /// <summary>Only occurrences changed on their own are unanswered: the rest of their series has been answered.</summary>
    public static bool Lone(List<JsonObject> group) =>
        group.Count > 0 && group.All(e => e.Truthy("seriesMasterId") && e.Str("type") == "exception");

    /// <summary>One meeting, stable when its time changes: an occurrence by its series and day, a one-off by its id.</summary>
    public static string SlotKey(JsonObject e) =>
        e.Truthy("seriesMasterId") ? $"{Py.Str(e.Get("seriesMasterId"))}@{Cut(e.Get("start").Req("dateTime"), 10)}" : e.Req("id");

    private static JsonArray Slot(JsonObject e) =>
        J.Array([Cut(e.Get("start").Req("dateTime"), 16), Cut(e.Get("end").Req("dateTime"), 16)]);

    /// <summary>The meetings others organise that you've accepted, and their times: what accept_shortened compares with.</summary>
    public static JsonObject Remember(List<JsonObject> events)
    {
        var memory = new JsonObject();
        foreach (var e in events)
            if (!e.Truthy("isOrganizer") && !e.Truthy("isCancelled") && e.Get("responseStatus").Str("response") == "accepted")
                memory[SlotKey(e)] = Slot(e);
        return memory;
    }

    /// <summary>Why this unanswered invite is a meeting you'd accepted that has only been shortened, or null.</summary>
    public static string? Shortened(List<JsonObject> group, JsonObject accepted)
    {
        var before = new List<((DateTime Start, DateTime End) Was, (DateTime Start, DateTime End) New)>();
        DateTime? last = null;
        foreach (var e in group)
        {
            if (accepted.Get(SlotKey(e)) is not JsonArray old)
                continue;
            var (start, end) = Times(e);
            var was = (PyTime.FromIso(old[0]!.GetValue<string>()), PyTime.FromIso(old[1]!.GetValue<string>()));
            if (start < was.Item1 || end > was.Item2)
                return null;
            before.Add((was, (start, end)));
            last = last is { } l && l > start ? l : start;
        }
        if (before.Count == 0 || !before.Any(b => b.New.End - b.New.Start < b.Was.End - b.Was.Start))
            return null;
        if (group.Any(e => !accepted.ContainsKey(SlotKey(e)) && Times(e).Start < last))
            return null;   // an occurrence you hadn't accepted, among ones you had: more than a shortening
        var (w, n) = before[0];
        return $"already accepted, only shortened: {Hm(w.Start)}–{Hm(w.End)} to {Hm(n.Start)}–{Hm(n.End)}"
               + (before.Count > 1 ? $" ({before.Count} occurrences)" : "");
    }

    /// <summary>(new queue items, the keys of every pending invite seen, skipped count). accepted: what the last run
    /// remembered, replaced in place with this run's. acted: each invite answered without asking. dryRun answers nothing.</summary>
    public static (List<JsonObject> Items, HashSet<string> Seen, int Skipped) Triage(Settings settings, ICalendar calendar, JsonObject raised,
        Action<string> log, DateTime? now = null, JsonObject? accepted = null, List<JsonObject>? acted = null, bool dryRun = false)
    {
        var invites = settings.Invites!;
        var at = now ?? Clock.Now;
        var events = calendar.Events(DateOnly.FromDateTime(at), invites.DaysAhead);
        var away = LeaveDays(settings, events);
        var held = Commitments(settings, events);
        var pending = events.Where(e => !e.Truthy("isOrganizer") && !e.Truthy("isCancelled")
                                        && e.Get("responseStatus").Str("response") is { } r && Pending.Contains(r) && Times(e).Start > at).ToList();
        var series = new List<(string Key, List<JsonObject> Group)>();
        foreach (var e in pending)
        {
            var k = Key(e);
            var index = series.FindIndex(s => s.Key == k);
            if (index < 0)
                series.Add((k, [e]));
            else
                series[index].Group.Add(e);
        }
        // Where only occurrences changed on their own are unanswered, each is its own invite, raised once per start time.
        foreach (var (k, group) in series.Where(s => Lone(s.Group)).ToList())
        {
            series.RemoveAll(s => s.Key == k);
            foreach (var e in group)
            {
                var key = $"invite:{e.Req("id")}@{Start16(e)}";
                var index = series.FindIndex(s => s.Key == key);
                if (index < 0)
                    series.Add((key, [e]));
                else
                    series[index] = (key, [e]);
            }
        }
        var items = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        var memory = Remember(events);
        foreach (var (k, group) in series)
        {
            var first = group[0];
            var why = accepted is not null && invites.AcceptShortened ? Shortened(group, accepted) : null;
            var (start, end) = Times(first);
            if (why is not null)
            {
                var send = !first.Has("responseRequested") || Py.Truthy(first.Get("responseRequested"));
                if (!dryRun)
                {
                    List<string> targets = Lone(group) ? [first.Req("id")]
                        : [first.Truthy("seriesMasterId") ? Py.Str(first.Get("seriesMasterId")) : first.Req("id"), .. Exceptions(group)];
                    foreach (var target in targets)
                        calendar.Respond(target, "accept", send: send);
                }
                foreach (var e in group)
                    memory[SlotKey(e)] = Slot(e);
                raised[k] = PyTime.Iso(Clock.Now, "seconds");
                if (acted is not null)
                {
                    var (oname, oaddress) = Organiser(first);
                    acted.Add(new JsonObject
                    {
                        ["received"] = PyTime.Iso(start, "minutes"), ["from"] = oname.Length > 0 ? oname : oaddress,
                        ["subject"] = $"{(first.StrOr("subject") is { Length: > 0 } s ? s : "(no subject)")}: {Day(start)}–{Hm(end)}",
                        ["why"] = why,
                    });
                }
                log($"{(dryRun ? "would accept" : "invite accepted")}  [{why}] {Py.Str(first.Get("subject"))}");
                continue;
            }
            seen.Add(k);
            if (raised.ContainsKey(k))
                continue;
            var rule = invites.Rules.FirstOrDefault(r => Matches(r, first, settings.Email));
            if (rule is not null && rule.Action == "skip")
            {
                skipped++;
                raised[k] = PyTime.Iso(Clock.Now, "seconds");
                log($"invite needs no response  [{rule.Name}] {Py.Str(first.Get("subject"))}");
                continue;
            }
            var whole = first.Truthy("seriesMasterId") && !Lone(group);   // a series, answered as a whole
            var verdict = Assess(settings, first, held, away, whole ? group : [], at);
            var (name, address) = Organiser(first);
            var item = new JsonObject
            {
                ["folder"] = "Calendar", ["type"] = "invite",
                ["id"] = whole ? first.Get("seriesMasterId").Copy() : first.Req("id"), ["key"] = k,
                ["series"] = whole ? first.Get("seriesMasterId").Copy() : null, ["occurrence"] = first.Req("id"),
                ["occurrences"] = J.Array(group.Select(e => e.Req("id"))), ["exceptions"] = J.Array(whole ? Exceptions(group) : []),
                ["responseRequested"] = first.Has("responseRequested") ? first.Get("responseRequested").Copy() : true,
                ["received"] = PyTime.Iso(start, "minutes"), ["from"] = name.Length > 0 ? name : address,
                ["subject"] = $"{(first.StrOr("subject") is { Length: > 0 } subj ? subj : "(no subject)")}: {Day(start)}–{Hm(end)}",
                ["preview"] = PyText.Head(first.StrOr("bodyPreview"), 300), ["rule"] = rule is not null ? rule.Name : "invite",
                ["check"] = $"{verdict.Recommendation.ToUpperInvariant()}: {verdict.Why}" + (verdict.Note.Length > 0 ? $" | note: \"{verdict.Note}\"" : ""),
                ["recommendation"] = verdict.Recommendation, ["decline"] = J.Array(verdict.Decline), ["note"] = verdict.Note,
                ["clashes"] = verdict.Clashes, ["webLink"] = first.Get("webLink").Copy(),
            };
            items.Add(item);
            raised[k] = PyTime.Iso(Clock.Now, "seconds");
            log($"invite queued  {Py.Str(first.Get("subject"))} ({Day(start)}): {verdict.Recommendation}");
        }
        if (accepted is not null && !dryRun)
        {
            accepted.Clear();
            foreach (var (key, value) in memory.ToList())
                accepted[key] = value!.DeepClone();
        }
        return (items, seen, skipped);
    }

    /// <summary>Meetings others organise that fall in your leave and that you haven't declined, each with its note.</summary>
    public static List<JsonObject> LeaveSweep(Settings settings, ICalendar calendar, DateOnly start, long days)
    {
        var events = calendar.Events(start, days);
        var away = LeaveDays(settings, events);
        var found = events.Where(e => !e.Truthy("isOrganizer") && !e.Truthy("isCancelled") && !e.Truthy("isAllDay")
                                      && away.Contains(DateOnly.FromDateTime(Times(e).Start))
                                      && e.Get("responseStatus").Str("response") != "declined").ToList();
        foreach (var e in found)
            e["note"] = LeaveNote(settings, DateOnly.FromDateTime(Times(e).Start), away);
        return found;
    }
}
