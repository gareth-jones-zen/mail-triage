using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>The calendar writes made by hand whose answer was lost (1.30.0), kept in unconfirmed_file so a later command
/// never resends one silently. A run's own writes have their done records (Unconfirmed); a command typed by hand had only
/// its warning, so the next `move --yes` or `leave --decline --yes` offered the same write again. Now each is recorded:
/// what (a move, an answer, a cancel, a hold), which event, the intended change and when. The next preview or --yes that
/// comes to the same event reads it again: if the change is visibly there, the record is cleared and it says so; if it
/// isn't, it says the earlier write may not have landed and skips it, unless --again (after checking Outlook).</summary>
public static class UnconfirmedWrites
{
    public enum Seen
    {
        /// <summary>No earlier write of this was lost.</summary>
        None,

        /// <summary>One was, and the change is there now: the record is cleared.</summary>
        Went,

        /// <summary>One was, and the change isn't visibly there (or it couldn't be read): skip it unless --again.</summary>
        Pending,
    }

    /// <summary>What a later command found: the record (as it was), and the sentence to show.</summary>
    public sealed record Look(Seen Seen, JsonObject? Entry, string Text);

    public static string Key(string kind, string id) => $"{kind}:{id}";

    /// <summary>A hold has no event id until it's created: its key is its time and subject.</summary>
    public static string HoldKey(string subject, DateTime start, DateTime end) =>
        Key("hold", $"{PyTime.Strftime(start, "%Y-%m-%dT%H:%M")}|{PyTime.Strftime(end, "%Y-%m-%dT%H:%M")}|{subject}");

    public static JsonObject Load(Settings settings) => StateFiles.LoadObject(settings.UnconfirmedFile, () => []);

    /// <summary>Kept until the meeting has ended: a write to a meeting that's over can't matter any more. One whose end
    /// isn't known (an answer to a queued invite's series) is kept for Triage.KeepDays.</summary>
    private static void Save(Settings settings, JsonObject state)
    {
        var now = PyTime.Strftime(Clock.Now, "%Y-%m-%dT%H:%M:%S");
        var old = PyTime.Iso(Clock.Now - TimeSpan.FromDays(Triage.KeepDays), "seconds");
        foreach (var (k, v) in state.ToList())
            if (v is JsonObject o && (o.Str("until") is { Length: > 0 } until ? PyStr.CompareCodePoints(until, now) < 0
                    : PyStr.CompareCodePoints(o.Str("when") ?? "", old) < 0))
                state.Remove(k);
        StateFiles.Save(settings.UnconfirmedFile, state);
    }

    /// <summary>"move of "Subject" (to Thu 26 Nov 10:00–10:30)": what a record was, for logs and messages.</summary>
    public static string Describe(JsonObject entry) => $"{entry.StrOr("verb")} of \"{entry.StrOr("subject")}\" ({entry.StrOr("change")})";

    private static string At(JsonObject entry) =>
        entry.Str("when") is { Length: >= 16 } when ? PyTime.Strftime(PyTime.FromIso(when[..19]), "%a %d %b %H:%M") : "an earlier run";

    /// <summary>A write that may have gone: recorded (replacing any earlier record of the same write) and logged. The
    /// entry carries verb, subject, change, event and until; when and why are added here.</summary>
    public static void Record(Settings settings, string key, JsonObject entry, string lost, Action<string> log)
    {
        entry["when"] = PyTime.Iso(Clock.Now, "seconds");
        entry["why"] = PyStr.Head(lost, 300);
        var state = Load(settings);
        state[key] = entry;
        Save(settings, state);
        log($"unconfirmed: recorded {Describe(entry)}, may have gone, not resent: {lost}");
    }

    public static void Clear(Settings settings, string key, string why, Action<string> log)
    {
        var state = Load(settings);
        if (state[key] is not JsonObject entry)
            return;
        state.Remove(key);
        Save(settings, state);
        log($"unconfirmed: cleared {Describe(entry)}: {why}");
    }

    /// <summary>The records about this event: written to it, or asked of it (an occurrence whose series was answered).</summary>
    public static List<(string Key, JsonObject Entry)> ForEvent(Settings settings, string eventId) =>
        Load(settings).Where(kv => kv.Value is JsonObject o && (o.Str("event") == eventId || o.Str("asked") == eventId))
            .Select(kv => (kv.Key, (JsonObject)kv.Value!)).ToList();

    /// <summary>The records a command wrote (leave's declines), or that belong to one queued invite.</summary>
    public static List<(string Key, JsonObject Entry)> Matching(Settings settings, Func<JsonObject, bool> match) =>
        Load(settings).Where(kv => kv.Value is JsonObject o && match(o)).Select(kv => (kv.Key, (JsonObject)kv.Value!)).ToList();

    /// <summary>Is there an earlier lost write of this, and did it land? Reads only when there's a record: `read` gives the
    /// event as it is now (null when it's gone), and `landed` says whether the recorded change is there. A read that fails
    /// can't tell, so it counts as not landed. Landed clears the record (and logs it).</summary>
    public static Look Check(Settings settings, string key, Func<JsonObject?> read, Func<JsonObject?, JsonObject, bool> landed, Action<string> log)
    {
        if (Load(settings)[key] is not JsonObject entry)
            return new(Seen.None, null, "");
        JsonObject? now;
        var readable = true;
        try
        {
            now = read();
        }
        catch (Exception error) when (error is not M365Graph.SignInNeededException)
        {
            now = null;
            readable = false;
        }
        if (readable && landed(now, entry))
        {
            Clear(settings, key, "it went (it's there now)", log);
            return new(Seen.Went, entry, $"An earlier {Describe(entry)} on {At(entry)} went: it's there now, so it isn't sent again.");
        }
        return new(Seen.Pending, entry, $"An earlier {Describe(entry)} on {At(entry)} may not have landed: check Outlook."
                                        + (readable ? "" : " (It couldn't be read back.)"));
    }

    /// <summary>The event, or null when Graph can't find it any more (deleted, or declined out of the calendar).</summary>
    public static JsonObject? ReadOrGone(ICalendar calendar, string eventId)
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

    private static string Local(DateTime t) => PyTime.Strftime(t, "%Y-%m-%dT%H:%M:%S");

    // ---- what each kind of write leaves behind ---------------------------------------------------------------------

    /// <summary>A move landed if the event is at the recorded time.</summary>
    public static bool Moved(JsonObject? e, JsonObject entry)
    {
        if (e is null)
            return false;
        var (start, end) = InviteTriage.Times(e);
        return Local(start) == entry.Str("start") && Local(end) == entry.Str("end");
    }

    /// <summary>An answer landed if the response is the one sent; a decline also if the meeting has gone from the calendar
    /// (Outlook removes a declined meeting).</summary>
    public static bool Answered(JsonObject? e, JsonObject entry) => entry.Str("action") switch
    {
        "decline" => e is null || e.Get("responseStatus").Str("response") == "declined",
        "tentative" => e?.Get("responseStatus").Str("response") == "tentativelyAccepted",
        _ => e?.Get("responseStatus").Str("response") == "accepted",
    };

    /// <summary>A cancel (or a block's delete) landed if the event is gone or shows as cancelled.</summary>
    public static bool Cancelled(JsonObject? e, JsonObject entry) => e is null || e.Truthy("isCancelled");

    /// <summary>A hold landed if the read found it (see Holds).</summary>
    public static bool Held(JsonObject? e, JsonObject entry) => e is not null;

    // ---- the records --------------------------------------------------------------------------------------------

    public static JsonObject MoveEntry(string command, string eventId, string subject, DateTime oldEnd, DateTime start, DateTime end) => new()
    {
        ["kind"] = "move", ["command"] = command, ["verb"] = "move", ["event"] = eventId, ["subject"] = subject,
        ["change"] = $"to {InviteTriage.Day(start)}–{InviteTriage.Hm(end)}", ["start"] = Local(start), ["end"] = Local(end),
        ["until"] = Local(end > oldEnd ? end : oldEnd),
    };

    public static JsonObject AnswerEntry(string command, string target, string? asked, string subject, string action, string scope, DateTime? end) => new()
    {
        ["kind"] = "respond", ["command"] = command, ["verb"] = action == "tentative" ? "tentative accept" : action, ["event"] = target,
        ["asked"] = asked, ["subject"] = subject, ["change"] = scope, ["action"] = action, ["until"] = end is { } e ? Local(e) : null,
    };

    public static JsonObject CancelEntry(string target, string? asked, string subject, bool isMeeting, string scope, DateTime end) => new()
    {
        ["kind"] = "cancel", ["command"] = "cancel", ["verb"] = isMeeting ? "cancel" : "delete", ["event"] = target, ["asked"] = asked,
        ["subject"] = subject, ["change"] = scope, ["until"] = Local(end),
    };

    public static JsonObject HoldEntry(string subject, DateTime start, DateTime end) => new()
    {
        ["kind"] = "hold", ["command"] = "hold", ["verb"] = "hold", ["event"] = null, ["subject"] = subject,
        ["change"] = $"{PyTime.Strftime(start, "%a %d %b %H:%M")}-{PyTime.Strftime(end, "%H:%M")}", ["start"] = Local(start), ["end"] = Local(end),
        ["until"] = Local(end),
    };
}
