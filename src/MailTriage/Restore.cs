using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;
using WinBackground;

namespace MailTriage;

/// <summary>`mail-triage restore` (1.32.0): an email mail-triage deleted (a rule, a check that passed, a handler's
/// `then`, the requests and proposals sweeps, or `delete` by hand) moved from Deleted Items back to the Inbox, once you've
/// seen the preview. Each `deleted` record has a number and an id for it; the email is found in Deleted Items by its
/// internet message ID (recorded since 1.32.0), or, for an older record, by its received time, sender and subject.
/// <para>Restoring counts as keeping it: the email is recorded as raised (the state file), which every pass of a run
/// already skips, and with "restored:" + its key, which the requests sweep skips too. So no rule deletes it again and it
/// isn't queued as new: you've just chosen it, and it's in your Inbox.</para>
/// <para>The move is sent once. One whose answer is lost is recorded in unconfirmed_file, as every write by hand is
/// (UnconfirmedWrites), and treated as restored meanwhile so a run can't delete it again; the next `restore` of it reads
/// the Inbox and settles it, and otherwise skips it unless --again.</para></summary>
public static class Restores
{
    /// <summary>In the state file, before an email's key: restored by hand, so the requests sweep leaves it alone.</summary>
    public const string Marker = "restored:";

    /// <summary>The heading `delete` by hand records under (1.32.0).</summary>
    public const string ByHandRule = "Emails deleted by hand";

    /// <summary>In the deleted file: how many records have been pruned (older than KeepDays), so each record's number
    /// stays the same as long as it's kept.</summary>
    public const string PrunedField = "pruned";

    private const string Fields = "id,internetMessageId,subject,from,receivedDateTime,isRead";

    public static long Pruned(JsonObject record) => record[PrunedField] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

    /// <summary>One `deleted` record, with its number and id.</summary>
    public sealed record Numbered(long N, string Id, JsonObject Entry);

    /// <summary>A record's id: the first 12 hex characters of the SHA-256 of what it records (when, folder, received, from,
    /// subject, rule), so it never changes and older records have one too.</summary>
    public static string Id(JsonObject entry)
    {
        var text = string.Join('\u001f', new[] { "when", "folder", "received", "from", "subject", "rule" }.Select(k => entry.Str(k) ?? ""));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }

    /// <summary>Every record kept, oldest first, numbered from 1 for the oldest ever kept: pruning doesn't renumber, and a
    /// new record takes the next number, so `deleted` and `deleted --all` agree.</summary>
    public static List<Numbered> All(JsonObject record)
    {
        var offset = Pruned(record);
        return record.Get("items").Objects().Select((e, i) => new Numbered(offset + i + 1, Id(e), e)).ToList();
    }

    /// <summary>What `deleted` lists: since the queue was last cleared, or (everything) all that's kept.</summary>
    public static List<Numbered> Listed(Settings settings, bool everything = false)
    {
        var record = Load(settings);
        var since = everything ? "" : record.Has("seen_until") ? Py.Str(record.Get("seen_until")) : "";
        return All(record).Where(x => PyStr.CompareCodePoints(x.Entry.Req("when"), since) > 0)
            .Select(x => x with { Entry = (JsonObject)x.Entry.DeepClone() }).ToList();
    }

    private static JsonObject Load(Settings settings) =>
        StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });

    /// <summary>"restored", "unconfirmed" (the move's answer was lost), or null.</summary>
    public static string? State(JsonObject entry) => entry.Get("restored").Str("state");

    /// <summary>Why a record can't be restored, or null when it's an email in Deleted Items. A record with a key was made
    /// when the email went to Deleted Items (1.32.0); an older one is told by what it says.</summary>
    public static string? WhyNot(JsonObject e)
    {
        if (e.Str("rule") == SendDrafts.Heading)   // 1.36.0
            return "it was sent, not deleted: a sent email can't be brought back";
        if (e.Str("key") is { Length: > 0 })
            return null;
        if (Unconfirmed.Is(e) || e.Str("folder") == "Calendar")
            return "it's a calendar change, not an email";
        if ((e.Str("rule") ?? "").StartsWith("Sent Items", StringComparison.Ordinal))
            return "it's from Sent Items: restore it in Outlook";
        if (e.Truthy("to"))
            return "it was filed, not deleted";
        if (e.Has("forward_to"))
            return "it was forwarded and left in its folder, not deleted";
        if ((e.Str("did") ?? "").EndsWith("left in the folder", StringComparison.Ordinal))
            return "it was left in its folder, not deleted";
        if (!e.Truthy("received") || !e.Truthy("from"))
            return "its record hasn't enough to find it by";
        return null;
    }

    /// <summary>The rule, or "by hand" for `delete`.</summary>
    private static string Rule(JsonObject e) => e.Truthy("by_hand") ? "by hand" : e.Str("rule") ?? "";

    /// <summary>`deleted --json`: each record as the UI needs it.</summary>
    public static JsonObject Json(Numbered x)
    {
        var e = x.Entry;
        var state = State(e);
        var whyNot = WhyNot(e) ?? state switch
        {
            "restored" => "it's been restored to the Inbox",
            "unconfirmed" => "an earlier restore may have gone: `mail-triage restore` checks the Inbox",
            _ => null,
        };
        return new JsonObject
        {
            ["n"] = x.N, ["id"] = x.Id, ["message_id"] = e.Str("key") is { Length: > 0 } key ? key : null,
            ["from"] = e.GetCopy("from"), ["subject"] = e.GetCopy("subject"), ["received"] = e.GetCopy("received"), ["deleted_at"] = e.GetCopy("when"),
            ["folder"] = e.GetCopy("folder"), ["rule"] = Rule(e), ["did"] = e.GetCopy("did") ?? e.GetCopy("accepted"), ["note"] = e.GetCopy("note"),
            ["restorable"] = whyNot is null, ["why_not"] = whyNot, ["restored"] = state, ["restored_at"] = e.Get("restored").GetCopy("at"),
        };
    }

    /// <summary>A record by its number in `deleted`, its id, or its email's internet message ID.</summary>
    public static Numbered Find(Settings settings, string reference)
    {
        var all = All(Load(settings));
        return all.FirstOrDefault(x => x.N.ToString(System.Globalization.CultureInfo.InvariantCulture) == reference)
               ?? all.FirstOrDefault(x => x.Id == reference)
               ?? all.FirstOrDefault(x => x.Entry.Str("key") == reference)
               ?? throw new LookupException($"Nothing in `mail-triage deleted` is {Py.ReprString(reference)}. Run `mail-triage deleted --all`.");
    }

    /// <summary>The emails in a folder (a well-known name: deleteditems, inbox) with this internet message ID.</summary>
    private static List<JsonObject> FindIn(Mailbox mailbox, string folder, string key)
    {
        var query = new List<(string, string)> { ("$filter", $"internetMessageId eq '{key.Replace("'", "''")}'"), ("$select", Fields) };
        return mailbox.Graph.Pages($"/me/mailFolders/{folder}/messages?{Url.Encode(query)}", mailbox.ReadScopes).ToList();
    }

    /// <summary>The record's email in Deleted Items: by its key, or (an older record) by its received time, sender and subject.</summary>
    private static List<JsonObject> InDeletedItems(Mailbox mailbox, JsonObject e)
    {
        if (e.Str("key") is { Length: > 0 } key)
            return FindIn(mailbox, "deleteditems", key);
        // Within the second Graph gives: Exchange keeps finer times, so "eq" a time Graph printed in seconds finds nothing.
        if (!DateTimeOffset.TryParse(e.Req("received"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
            return [];
        var received = at.ToUniversalTime();
        static string Utc(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var query = new List<(string, string)>
        {
            ("$filter", $"receivedDateTime ge {Utc(received)} and receivedDateTime lt {Utc(received.AddSeconds(1))}"), ("$select", Fields),
        };
        return mailbox.Graph.Pages($"/me/mailFolders/deleteditems/messages?{Url.Encode(query)}", mailbox.ReadScopes)
            .Where(m => string.Equals(Triage.Sender(m), e.Str("from"), StringComparison.OrdinalIgnoreCase) && m.StrOr("subject") == e.StrOr("subject"))
            .ToList();
    }

    /// <summary>Records it as restored (or, with the move's answer lost, as maybe restored) and as raised, so a run leaves
    /// it alone either way.</summary>
    private static void MarkRestored(Settings settings, string id, string key, string state)
    {
        var now = PyTime.Iso(Clock.Now, "seconds");
        var record = Load(settings);
        if (All(record).FirstOrDefault(x => x.Id == id) is { } found)
        {
            found.Entry["restored"] = new JsonObject { ["state"] = state, ["at"] = now, ["to"] = "Inbox" };
            StateFiles.Save(settings.DeletedFile, record);
        }
        var raised = StateFiles.LoadObject(settings.StateFile, () => []);
        if (!raised.ContainsKey(key))
            raised[key] = now;
        raised[Marker + key] = now;
        StateFiles.Save(settings.StateFile, raised);
    }

    /// <summary>`mail-triage restore <n or id> [--yes] [--again]`. Without --yes, the preview; with it, the move (once),
    /// read back, and recorded. 0: done, nothing to do, or a preview; 1: the move's answer was lost, refused by Microsoft,
    /// or skipped for an earlier lost one; 2: refused before anything was sent (unknown, not an email, not in Deleted Items).</summary>
    public static int Run(Settings settings, IGraph graph, WriteDoor door, string reference, bool yes, bool again, Action<string> log,
        Action<string> output, Action<TimeSpan> sleep)
    {
        if (!yes)
            return Restore(settings, graph, door, reference, false, again, log, output, sleep, locked: false);
        using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))   // a run can't act on it meanwhile
            return Restore(settings, graph, door, reference, true, again, log, output, sleep, locked: true);
    }

    private static int Restore(Settings settings, IGraph graph, WriteDoor door, string reference, bool yes, bool again, Action<string> log,
        Action<string> output, Action<TimeSpan> sleep, bool locked)
    {
        var mailbox = new Mailbox(graph, door.Mail);
        var (n, id, e) = Find(settings, reference);
        var label = $"{n}. {e.StrOr("from")}: \"{e.StrOr("subject")}\"";
        void Mark(string key, string state)
        {
            if (locked)
                MarkRestored(settings, id, key, state);
            else
                using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))
                    MarkRestored(settings, id, key, state);
        }

        if (WhyNot(e) is { } why)
        {
            output($"Can't restore {label}: {why}.");
            return 2;
        }
        if (State(e) == "restored")
        {
            output($"{label} was restored to the Inbox on {e.Get("restored").StrOr("at").Replace('T', ' ')}: nothing to do.");
            return 0;
        }

        // An earlier move whose answer was lost: settled by reading the Inbox, never sent again by itself.
        var writeKey = UnconfirmedWrites.Key("restore", id);
        if (UnconfirmedWrites.Load(settings)[writeKey] is JsonObject earlier)
        {
            var earlierKey = earlier.Str("message_id");
            var look = UnconfirmedWrites.Check(settings, writeKey,
                () => earlierKey is null ? null : FindIn(mailbox, "inbox", earlierKey).FirstOrDefault(), (m, _) => m is not null, log);
            output(look.Text);
            if (look.Seen == UnconfirmedWrites.Seen.Went)
            {
                Mark(earlierKey!, "restored");
                return 0;
            }
            if (!again)
            {
                if (yes)
                {
                    output("Not moved again. Check the Inbox and Deleted Items in Outlook; if it's still in Deleted Items, "
                           + $"`mail-triage restore {n} --again --yes` moves it.");
                    log($"restore skipped: an earlier one may have gone  {e.StrOr("from")}: {e.StrOr("subject")}");
                    return 1;
                }
                output($"--yes would skip it; `mail-triage restore {n} --again --yes` moves it once you've checked Outlook.");
                return 0;
            }
        }

        List<JsonObject> found;
        try
        {
            found = InDeletedItems(mailbox, e);
        }
        catch (GraphRequestException error)
        {
            output($"Can't restore {label}: Deleted Items couldn't be searched (Graph said {(int)error.Status}).");
            return 2;
        }
        if (found.Count == 0)
        {
            var inInbox = e.Str("key") is { Length: > 0 } k ? FindIn(mailbox, "inbox", k).FirstOrDefault() : null;
            if (inInbox is null)
            {
                output($"Can't restore {label}: it isn't in Deleted Items any more (emptied, or moved in Outlook).");
                return 2;
            }
            output($"{label} is already in the Inbox (moved back in Outlook?).");
            if (!yes)
            {
                output("Nothing changed. Run again with --yes to record it as restored, so mail-triage leaves it alone.");
                return 0;
            }
            if (door.Mode == WriteMode.Live)
                Mark(e.Req("key"), "restored");
            output("Recorded as restored: no rule deletes it again, and it isn't queued as new.");
            return 0;
        }
        if (found.Count > 1)
        {
            output($"Can't restore {label}: {found.Count} emails in Deleted Items have its sender, subject and time. Restore it in Outlook.");
            return 2;
        }
        var message = found[0];
        if ((e.Str("key") is { Length: > 0 } recorded ? recorded : message.Str("internetMessageId")) is not { Length: > 0 } messageKey)
        {
            output($"Can't restore {label}: it has no internet message ID, so it couldn't be told apart once moved. Restore it in Outlook.");
            return 2;
        }

        var deleted = e.StrOr("when").Replace('T', ' ');
        JsonObject? moved = null;
        output(yes ? "Moving this email from Deleted Items back to the Inbox:" : "Would move this email from Deleted Items back to the Inbox:");
        output($"  From      {Triage.Sender(message)}");
        output($"  Subject   {message.StrOr("subject")}");
        output($"  Received  {message.StrOr("receivedDateTime").Replace('T', ' ').TrimEnd('Z')} UTC");
        output($"  Deleted   {(deleted.Length >= 16 ? deleted[..16] : deleted)}, " + (e.Truthy("by_hand") ? "by hand (`mail-triage delete`)" : $"rule \"{e.StrOr("rule")}\"")
               + (e.Str("check") is { Length: > 0 } check ? $" ({check})" : ""));
        output($"  Goes to   the Inbox, {(message.Truthy("isRead") ? "read" : "unread")} as it is (its read state isn't changed)");
        output("  Then      mail-triage leaves it alone: no rule deletes it again, and it isn't queued as new");
        if (!yes)
        {
            output($"Nothing moved. Run again with --yes to restore it: `mail-triage restore {n} --yes`.");
            return 0;
        }

        try
        {
            moved = door.Mail.Restore(message.Req("id"));
        }
        catch (GraphDeliveryUnknownException error)
        {
            // It may have gone: recorded, never resent by itself, and kept from the rules meanwhile.
            UnconfirmedWrites.Record(settings, writeKey, new JsonObject
            {
                ["kind"] = "restore", ["command"] = "restore", ["verb"] = "restore", ["event"] = null, ["subject"] = message.StrOr("subject"),
                ["change"] = "to the Inbox", ["message_id"] = messageKey, ["until"] = null,
            }, error.Message, log);
            Mark(messageKey, "unconfirmed");
            output("Microsoft didn't confirm the move, so it may or may not have gone. Check the Inbox in Outlook. It isn't sent again: "
                   + $"the next `mail-triage restore {n}` checks the Inbox, and mail-triage leaves the email alone meanwhile.");
            return 1;
        }
        catch (GraphRequestException error)
        {
            output($"Microsoft refused the move (Graph said {(int)error.Status}): nothing moved.");
            log($"restore refused ({(int)error.Status})  {e.StrOr("from")}: {e.StrOr("subject")}");
            return 1;
        }
        if (door.Mode != WriteMode.Live)
        {
            output("Journal only: nothing moved, and nothing recorded.");
            return 0;
        }
        UnconfirmedWrites.Clear(settings, writeKey, "moved again with --again, and Microsoft confirmed it", log);
        Mark(messageKey, "restored");
        log($"restored to the Inbox by hand  {e.StrOr("from")}: {e.StrOr("subject")}");
        Activity.Write(settings, log, "restore", ActivityJournal.By.Hand, e.Str("subject"), "by hand",
            $"back to the Inbox from Deleted Items (deleted {(deleted.Length >= 16 ? deleted[..16] : deleted)}, "
            + (e.Truthy("by_hand") ? "by hand" : $"rule \"{e.StrOr("rule")}\"") + ")",
            messageKey, moved?.Str("id") is { Length: > 0 } movedId ? $"mail-triage delete {movedId}" : null);   // 1.33.0

        // Read back: Microsoft answered, so it went; this says where it is now.
        JsonObject? back = null;
        for (var attempt = 0; attempt < 3 && back is null; attempt++)
        {
            if (attempt > 0)
                sleep(TimeSpan.FromSeconds(2));
            try
            {
                back = FindIn(mailbox, "inbox", messageKey).FirstOrDefault();
            }
            catch (GraphRequestException)
            {
            }
        }
        output(back is not null
            ? $"Restored to the Inbox ({(back.Truthy("isRead") ? "read" : "unread")}). mail-triage leaves it alone from now on."
            : "Moved: Microsoft confirmed it, but it couldn't be read back in the Inbox yet. Check Outlook. mail-triage leaves it alone from now on.");
        return 0;
    }
}
