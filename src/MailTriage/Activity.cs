using System.Text.Json.Nodes;
using ActivityJournal;
using PyCompat;

namespace MailTriage;

/// <summary>The shared activity journal (1.33.0): one line for each thing mail-triage did, written through
/// background-tools-lib's ActivityJournal to journal_file (by convention %EA_LOCAL%\journal.jsonl), for a "while you were
/// away" view. Only what really happened is written: never a dry run, a preview, or a write that `--writes journal` or
/// `refuse` didn't send. Never a message body: the subject, as `list` and `deleted` already show it, with sensitivity
/// "private" so a screen shown to others can hide it. Writing never fails or undoes the action: a problem is logged.
/// <para>What each line's `what` is: delete, file, forward, handle, accept, tentative, decline, move, cancel, remove (your
/// own block taken out), hold, send-invite, keep, requeue, restore, clear.</para></summary>
internal static class Activity
{
    public const string Tool = "mail-triage";

    /// <summary>On an `acted` entry a planner made: what was done to which event, and how to undo it. Taken off before the
    /// entry goes into the deleted file. An entry without one (a preview's "would") is not journalled.</summary>
    public const string Tag = "journal";

    /// <summary>Tags an acted entry with what was done, the event (or message) id, and an undo command.</summary>
    public static JsonObject Mark(JsonObject entry, string what, string? item = null, string? undo = null)
    {
        entry[Tag] = new JsonObject { ["what"] = what, ["item"] = item, ["undo"] = undo };
        return entry;
    }

    /// <summary>Takes the tag off an entry (null if it has none).</summary>
    public static JsonObject? Take(JsonObject entry)
    {
        if (entry[Tag] is not JsonObject tag)
            return null;
        entry.Remove(Tag);
        return tag;
    }

    /// <summary>Moves a planner's tag from what it returned to the entry the run records.</summary>
    public static JsonObject Carry(JsonObject from, JsonObject to)
    {
        if (from[Tag] is JsonObject tag)
            to[Tag] = tag.DeepClone();
        return to;
    }

    /// <summary>One line. Never throws.</summary>
    public static void Write(Settings settings, Action<string>? log, string what, string by, string? subject, string? rule, string? why,
        string? item, string? undo, bool unconfirmed = false)
    {
        if (settings.JournalFile is not { } path)
            return;
        try
        {
            new Journal(path, clock: () => Clock.Provider.GetLocalNow()).TryWrite(new JournalEntry
            {
                Tool = Tool,
                What = what,
                By = by,
                Subject = Blank(subject),
                Rule = Blank(rule),
                Why = Blank(why),
                Item = Blank(item),
                Undo = Blank(undo),
                Sensitivity = Blank(subject) is null ? Sensitivity.Normal : Sensitivity.Private,
                Outcome = unconfirmed ? JournalEntry.Unconfirmed : null,
            }, log);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log?.Invoke($"journal: couldn't write to {path}: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>The line for a record just added to the deleted file: its tag's what (or <paramref name="what"/>; neither, no
    /// line), its subject and rule, why (the record's own words, then the rule's reason from the settings), the email's key
    /// or the tagged event, and the undo: the tag's, or for an email in Deleted Items, `restore` with the record's id.</summary>
    public static void Recorded(Settings settings, Action<string>? log, JsonObject entry, JsonObject? tag, string by, string? what = null)
    {
        what = tag.Str("what") ?? what;
        if (what is null)
            return;
        var rule = entry.Str("rule");
        var reasons = new List<string>();
        foreach (var said in new[] { entry.Str("check"), entry.Str("did") ?? entry.Str("why") ?? entry.Str("accepted"), RuleWhy(settings, rule) })
            if (said is { Length: > 0 } && !reasons.Contains(said))
                reasons.Add(said);
        var undo = tag.Str("undo");
        if (undo is null && what == "delete" && entry.Str("key") is { Length: > 0 } && Restores.WhyNot(entry) is null)
            undo = $"mail-triage restore {Restores.Id(entry)}";
        Write(settings, log, what, by, entry.Str("subject"), by == By.Hand && entry.Truthy("by_hand") ? "by hand" : rule,
            reasons.Count > 0 ? string.Join("; ", reasons) : null, tag.Str("item") ?? entry.Str("key"), undo, Unconfirmed.Is(entry));
    }

    /// <summary>A [[rule]]'s why, by its name: the person's reason for it.</summary>
    private static string? RuleWhy(Settings settings, string? name) =>
        name is null ? null : settings.Rules.FirstOrDefault(r => r.Name == name)?.Why is { Length: > 0 } why ? why : null;

    /// <summary>The opposite answer, for an answer's undo (a preview until --yes; it tells the organiser).</summary>
    public static string? Answer(string? eventId, string action, bool series = false) =>
        eventId is null || series ? null : $"mail-triage respond {eventId} {(action == "decline" ? "accept" : "decline")}";

    /// <summary>`move` back to where it was (a preview until --yes), when it started and ended on one day.</summary>
    public static string? MoveBack(string? eventId, DateTime start, DateTime end) =>
        eventId is null || start.Date != end.Date || end <= start ? null
            : $"mail-triage move {eventId} {PyTime.Strftime(start, "%Y-%m-%dT%H:%M")} {PyTime.Strftime(end, "%H:%M")}";

    private static string? Blank(string? text) => text is { Length: > 0 } ? text : null;
}
