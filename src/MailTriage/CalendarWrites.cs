using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>`respond` and `cancel` (1.22.0): the calendar writes a session needs that `invite`, `move` and `hold` don't
/// cover, so the M365 connector's calendar tools can be denied (assistant design connector-calendar-writes, 23 Nov 2026).
/// Both follow `move` and `hold`: without --yes they show what they would do and write nothing; with --yes they write
/// once, read back, never resend a write that may have reached Microsoft, log it and record it for `mail-triage deleted`.
/// One occurrence by default; --series for the whole series (decline the instance, never the series, unless asked).</summary>
public static class CalendarWrites
{
    /// <summary>Graph's responseStatus.response, in words.</summary>
    internal static string Current(JsonObject e) => e.Get("responseStatus").Str("response") switch
    {
        "accepted" => "accepted",
        "tentativelyAccepted" => "tentative",
        "declined" => "declined",
        "organizer" => "you organise it",
        _ => "not answered yet",
    };

    /// <summary>An attendee as a preview lists them: "Name &lt;address&gt;", or the address alone.</summary>
    internal static string Who(JsonObject a) =>
        a.Get("emailAddress").Str("name") is { Length: > 0 } n ? $"{n} <{InviteTriage.Address(a)}>" : InviteTriage.Address(a);

    /// <summary>"this occurrence only", "the whole series" or "this meeting", for a preview of a write to this event.</summary>
    internal static string Scope(JsonObject e) => (e.Str("type") ?? "singleInstance") switch
    {
        "occurrence" or "exception" => "this occurrence only",
        "seriesMaster" => "the whole series",
        _ => "this meeting",
    };

    private static string Verb(string action) => action switch
    {
        "accept" => "accept",
        "tentative" => "tentative",
        _ => "decline",
    };

    /// <summary>Which event a write goes to: the occurrence given, or with --series its series. Null, with a reason,
    /// when that can't be done safely.</summary>
    internal static (string? Target, string What, string? Refusal) Target(JsonObject e, string eventId, bool series)
    {
        var type = e.Str("type") ?? "singleInstance";
        if (series)
        {
            if (type == "seriesMaster")
                return (eventId, "the whole series", null);
            if (e.Str("seriesMasterId") is { Length: > 0 } master)
                return (master, "the whole series", null);
            return (eventId, "this meeting (it isn't part of a series)", null);
        }
        if (type == "seriesMaster")
            return (null, "", "That id is a whole series. Give one occurrence's id, or add --series to mean the whole series.");
        return (eventId, type is "occurrence" or "exception" ? "this occurrence only" : "this meeting", null);
    }

    public static int Run(string command, List<string> positional, string note, bool series, bool yes, bool again, Settings settings, IGraph graph,
        WriteDoor door, Action<string> log, TextWriter output, TextWriter errors)
    {
        void Out(string text) => output.WriteLine(Py.Lines(text));
        void Err(string text) => errors.WriteLine(Py.Lines(text));
        var calendar = new Calendar(graph, door.Calendar);
        var eventId = positional[0];
        JsonObject e;
        try
        {
            e = calendar.Event(eventId);
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is 404 or 410)
        {
            // 1.30.0: gone, after a cancel or a decline whose answer was lost: that's the change, so it went.
            var went = UnconfirmedWrites.ForEvent(settings, eventId)
                .Select(r => UnconfirmedWrites.Check(settings, r.Key, () => null, r.Entry.Str("kind") == "cancel" ? UnconfirmedWrites.Cancelled : UnconfirmedWrites.Answered, log))
                .Where(l => l.Seen == UnconfirmedWrites.Seen.Went).ToList();
            foreach (var look in went)
                Out(look.Text);
            if (went.Count > 0)
            {
                Out("Nothing to do: it's no longer in your calendar.");
                return 0;
            }
            Err($"No event with that id in your calendar ({(int)error.Status}).");
            return 2;
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is 400)
        {
            Err($"No event with that id in your calendar ({(int)error.Status}).");
            return 2;
        }
        var subject = e.StrOr("subject");
        var (start, end) = InviteTriage.Times(e);
        var when = $"{PyTime.Strftime(start, "%a %d %b %H:%M")}-{PyTime.Strftime(end, "%H:%M")}";
        var (target, scope, refusal) = Target(e, eventId, series);
        if (refusal is not null)
        {
            Err(refusal);
            return 2;
        }
        var write = new Once(settings, calendar, eventId, target!, yes, again, door, log, Out, Err);
        return command == "respond"
            ? Respond(positional[1], e, subject, start, end, when, target!, scope, note, yes, settings, calendar, door, log, Out, Err, write)
            : Cancel(e, subject, start, end, when, target!, scope, note, yes, settings, calendar, door, log, Out, Err, write);
    }

    /// <summary>1.30.0: one `respond` or `cancel` write against unconfirmed_file. Before: an earlier lost write to the same
    /// event is read again (cleared if it went, skipped if it may not have landed, unless --again). After: a lost answer
    /// is recorded; a write sent with --again that's confirmed clears the record.</summary>
    private sealed class Once(Settings settings, Calendar calendar, string asked, string target, bool yes, bool again, WriteDoor door,
        Action<string> log, Action<string> Out, Action<string> Err)
    {
        private string key = "";
        private bool pending;

        /// <summary>Null to go on (preview or write); otherwise the exit code: 0 when the change is already there, 1 when
        /// it's skipped.</summary>
        public int? Before(string kind, Func<JsonObject?, JsonObject, bool> landed, Func<JsonObject?, bool> already)
        {
            key = UnconfirmedWrites.Key(kind, target);
            JsonObject? now = null;
            var look = UnconfirmedWrites.Check(settings, key, () => now = ReadBack(calendar, target), landed, log);
            if (look.Seen == UnconfirmedWrites.Seen.Went)
            {
                Out(look.Text);
                if (already(now))
                {
                    Out("Nothing to do: it's already that way.");
                    return 0;
                }
            }
            pending = look.Seen == UnconfirmedWrites.Seen.Pending;
            if (!pending)
                return null;
            if (!yes)
            {
                Out(look.Text + (again ? " --again: it would be sent again." : " --yes would skip it; once you've checked, --again --yes sends it."));
                return null;
            }
            if (!again)
            {
                log($"unconfirmed: skipped {UnconfirmedWrites.Describe(look.Entry!)}: an earlier one may not have landed");
                Err(look.Text + " Not sent again: once you've checked, `--again --yes` sends it.");
                return 1;
            }
            log($"unconfirmed: sending {UnconfirmedWrites.Describe(look.Entry!)} again (--again)");
            return null;
        }

        public string Tail => pending && !again ? "Nothing sent." : pending ? "Nothing sent. Run again with --again --yes to send it." : "";

        public void Lost(JsonObject entry, string lost)
        {
            entry["asked"] = asked == target ? null : asked;
            UnconfirmedWrites.Record(settings, key, entry, lost, log);
        }

        public void Confirmed()
        {
            if (pending && door.Mode == WriteMode.Live)
                UnconfirmedWrites.Clear(settings, key, "sent again with --again, and confirmed", log);
        }
    }

    private static int Respond(string action, JsonObject e, string subject, DateTime start, DateTime end, string when, string target, string scope,
        string note, bool yes, Settings settings, Calendar calendar, WriteDoor door, Action<string> log, Action<string> Out, Action<string> Err, Once once)
    {
        if (e.Truthy("isOrganizer"))
        {
            Err($"\"{subject}\" is your own meeting: there's nothing to answer. Use `mail-triage move` to move it, or `mail-triage cancel` to cancel it.");
            return 2;
        }
        var (organiserName, organiserAddress) = InviteTriage.Organiser(e);
        var organiser = organiserName.Length > 0 ? organiserName : organiserAddress;
        var what = $"\"{subject}\", {when} ({scope}), organised by {organiser}: you've {(Current(e) == "not answered yet" ? "not answered yet" : "said " + Current(e))}";
        var send = $"{Verb(action)}" + (note.Length > 0 ? $", with the note \"{note}\"" : ", with no note");
        var intended = UnconfirmedWrites.AnswerEntry("respond", target, null, subject, action, $"{PyTime.Strftime(start, "%a %d %b %H:%M")}, {scope}", end);
        if (once.Before("respond", UnconfirmedWrites.Answered, now => UnconfirmedWrites.Answered(now, intended)) is { } done)
            return done;
        if (!yes)
        {
            Out($"Would answer {what}.");
            Out($"  Would send {organiser} a response: {send}.");
            Out(once.Tail.Length > 0 ? once.Tail : "Nothing sent. Run again with --yes to send it.");
            return 0;
        }
        if (Write(() => calendar.Respond(target, action, note, send: true), "answer", subject, start, log, Err, lost => once.Lost(intended, lost)) is { } failed)
            return failed;
        if (door.Mode != WriteMode.Live)
        {
            Out($"Journalled, not sent: {Verb(action)} for {what}.");
            return 0;
        }
        once.Confirmed();
        var now = ReadBack(calendar, target);
        Out($"Answered: {Verb(action)}, \"{subject}\", {when} ({scope}); {organiser} gets your response."
            + (now is null ? "" : $" Your calendar now says: {Current(now)}."));
        var did = $"{Verb(action)} {PyTime.Strftime(start, "%a %d/%m %H:%M")} ({scope})" + (note.Length > 0 ? $": \"{note}\"" : "");
        Triage.RecordCalendarWrite(settings, "Answered by hand", subject, start, organiserAddress, did, action, target,
            $"mail-triage respond {target} {(action == "decline" ? "accept" : "decline")}" + (scope == "the whole series" ? " --series" : ""));
        log($"answered  {subject}: {did}");
        return 0;
    }

    private static int Cancel(JsonObject e, string subject, DateTime start, DateTime end, string when, string target, string scope, string note, bool yes,
        Settings settings, Calendar calendar, WriteDoor door, Action<string> log, Action<string> Out, Action<string> Err, Once once)
    {
        if (!e.Truthy("isOrganizer"))
        {
            Err($"\"{subject}\" is someone else's meeting: you can't cancel it. Use `mail-triage respond <event id> decline` to decline it.");
            return 2;
        }
        var owner = (settings.Email ?? "").ToLowerInvariant();
        var others = InviteTriage.People(e).Where(a => InviteTriage.Address(a) != owner).ToList();
        var isMeeting = others.Count > 0;
        if (once.Before("cancel", UnconfirmedWrites.Cancelled, now => UnconfirmedWrites.Cancelled(now, [])) is { } done)
            return done;
        if (!yes)
        {
            if (isMeeting)
            {
                Out($"Would cancel your meeting \"{subject}\", {when} ({scope}).");
                Out($"  Each of these {others.Count} {(others.Count == 1 ? "person" : "people")} gets a cancellation" + (note.Length > 0 ? $", with the note \"{note}\":" : ", with no note:"));
                foreach (var a in others)
                    Out($"    {Who(a)}");
            }
            else
                Out($"Would delete your block \"{subject}\", {when} ({scope}). No one else is invited, so no one is told.");
            Out(once.Tail.Length > 0 ? once.Tail.Replace("Nothing sent.", "Nothing changed.").Replace("to send it", "to do it")
                : "Nothing changed. Run again with --yes to do it.");
            return 0;
        }
        var intended = UnconfirmedWrites.CancelEntry(target, null, subject, isMeeting, $"{PyTime.Strftime(start, "%a %d %b %H:%M")}, {scope}", end);
        if (Write(() =>
            {
                if (isMeeting)
                    calendar.Cancel(target, note);
                else
                    calendar.Remove(target);
            }, isMeeting ? "cancel" : "delete", subject, start, log, Err, lost => once.Lost(intended, lost)) is { } failed)
            return failed;
        if (door.Mode != WriteMode.Live)
        {
            Out($"Journalled, not sent: {(isMeeting ? "cancel" : "delete")} \"{subject}\", {when} ({scope}).");
            return 0;
        }
        once.Confirmed();
        var still = ReadBack(calendar, target);
        var gone = still is null || still.Truthy("isCancelled");
        var did = isMeeting
            ? $"cancelled {PyTime.Strftime(start, "%a %d/%m %H:%M")} ({scope}); {others.Count} told" + (note.Length > 0 ? $": \"{note}\"" : "")
            : $"deleted block {PyTime.Strftime(start, "%a %d/%m %H:%M")} ({scope}); no one told";
        Out(isMeeting
            ? $"Cancelled: \"{subject}\", {when} ({scope}); {others.Count} attendee{(others.Count == 1 ? " gets" : "s get")} a cancellation."
            : $"Deleted: \"{subject}\", {when} ({scope}); no one else was invited.");
        if (!gone)
            Out("  It still shows in your calendar for now: Outlook can take a moment to catch up.");
        Triage.RecordCalendarWrite(settings, isMeeting ? "Cancelled by hand" : "Deleted by hand", subject, start, settings.Email ?? "", did,
            isMeeting ? "cancel" : "remove", target);
        log($"{(isMeeting ? "cancelled" : "deleted")}  {subject}: {did}");
        return 0;
    }

    /// <summary>One write, once. A refusal from Microsoft (4xx) changed nothing; anything else may have reached it, so it's
    /// never retried: check the calendar instead. Null when the write went through.</summary>
    private static int? Write(Action write, string verb, string subject, DateTime start, Action<string> log, Action<string> Err, Action<string> lost)
    {
        try
        {
            write();
            return null;
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is >= 400 and < 500)
        {
            Err($"Microsoft refused the {verb} ({(int)error.Status}): nothing changed. {PyStr.Head(error.Message, 200)}");
            return 1;
        }
        catch (Exception error) when (error is not WriteRefusedException)
        {
            log($"{verb} failed, may have reached Microsoft: {subject} {PyTime.Strftime(start, "%d/%m %H:%M")}: {error.GetType().Name}: {error.Message}");
            Err($"The {verb} may or may not have gone through (the call failed after it was sent). Check your calendar before trying "
                + "again: it isn't retried, so nothing is sent twice. It's recorded: the next `respond` or `cancel` of it checks whether it landed.");
            lost($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    /// <summary>The event after the write, or null if it's gone (deleted, or Graph can't find it any more).</summary>
    private static JsonObject? ReadBack(Calendar calendar, string eventId)
    {
        try
        {
            return calendar.Event(eventId);
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is 404 or 410)
        {
            return null;
        }
    }
}
