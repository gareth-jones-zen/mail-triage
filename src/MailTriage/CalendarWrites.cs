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

    public static int Run(string command, List<string> positional, string note, bool series, bool yes, Settings settings, IGraph graph,
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
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is 404 or 400)
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
        return command == "respond"
            ? Respond(positional[1], e, subject, start, when, target!, scope, note, yes, settings, calendar, door, log, Out, Err)
            : Cancel(e, subject, start, when, target!, scope, note, yes, settings, calendar, door, log, Out, Err);
    }

    private static int Respond(string action, JsonObject e, string subject, DateTime start, string when, string target, string scope,
        string note, bool yes, Settings settings, Calendar calendar, WriteDoor door, Action<string> log, Action<string> Out, Action<string> Err)
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
        if (!yes)
        {
            Out($"Would answer {what}.");
            Out($"  Would send {organiser} a response: {send}.");
            Out("Nothing sent. Run again with --yes to send it.");
            return 0;
        }
        if (Write(() => calendar.Respond(target, action, note, send: true), "answer", subject, start, log, Err) is { } failed)
            return failed;
        if (door.Mode != WriteMode.Live)
        {
            Out($"Journalled, not sent: {Verb(action)} for {what}.");
            return 0;
        }
        var now = ReadBack(calendar, target);
        Out($"Answered: {Verb(action)}, \"{subject}\", {when} ({scope}); {organiser} gets your response."
            + (now is null ? "" : $" Your calendar now says: {Current(now)}."));
        var did = $"{Verb(action)} {PyTime.Strftime(start, "%a %d/%m %H:%M")} ({scope})" + (note.Length > 0 ? $": \"{note}\"" : "");
        Triage.RecordCalendarWrite(settings, "Answered by hand", subject, start, organiserAddress, did);
        log($"answered  {subject}: {did}");
        return 0;
    }

    private static int Cancel(JsonObject e, string subject, DateTime start, string when, string target, string scope, string note, bool yes,
        Settings settings, Calendar calendar, WriteDoor door, Action<string> log, Action<string> Out, Action<string> Err)
    {
        if (!e.Truthy("isOrganizer"))
        {
            Err($"\"{subject}\" is someone else's meeting: you can't cancel it. Use `mail-triage respond <event id> decline` to decline it.");
            return 2;
        }
        var owner = (settings.Email ?? "").ToLowerInvariant();
        var others = InviteTriage.People(e).Where(a => InviteTriage.Address(a) != owner).ToList();
        var isMeeting = others.Count > 0;
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
            Out("Nothing changed. Run again with --yes to do it.");
            return 0;
        }
        if (Write(() =>
            {
                if (isMeeting)
                    calendar.Cancel(target, note);
                else
                    calendar.Remove(target);
            }, isMeeting ? "cancel" : "delete", subject, start, log, Err) is { } failed)
            return failed;
        if (door.Mode != WriteMode.Live)
        {
            Out($"Journalled, not sent: {(isMeeting ? "cancel" : "delete")} \"{subject}\", {when} ({scope}).");
            return 0;
        }
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
        Triage.RecordCalendarWrite(settings, isMeeting ? "Cancelled by hand" : "Deleted by hand", subject, start, settings.Email ?? "", did);
        log($"{(isMeeting ? "cancelled" : "deleted")}  {subject}: {did}");
        return 0;
    }

    /// <summary>One write, once. A refusal from Microsoft (4xx) changed nothing; anything else may have reached it, so it's
    /// never retried: check the calendar instead. Null when the write went through.</summary>
    private static int? Write(Action write, string verb, string subject, DateTime start, Action<string> log, Action<string> Err)
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
                + "again: it isn't retried, so nothing is sent twice.");
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
