using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;

namespace MailTriage;

/// <summary>What a run did: the counts the Python's report dict held.</summary>
public sealed record RunReport(int Deleted, int Filed, int New, int Waiting, int Kept, int Invites);

/// <summary>One triage run, and what happens to the queue afterwards (triage.py).
/// - Delete marks the email read and moves it to Deleted Items, so it's recoverable.
/// - Review leaves it where it is and queues it for a decision.
/// - Check runs a test on the email (Checks) and deletes it only if there's nothing to do.
/// - Anything no rule names is queued, never deleted. A rule whose pattern runs out of time never deletes either.
/// - Each email is raised once: the state file records every email ever queued, by its internet message ID.
/// - What the rules delete is recorded (the deleted file) for KeepDays, as is everything the calendar rules did.</summary>
public static class Triage
{
    public const int KeepDays = 60;

    public static string Key(JsonObject message) => message.Str("internetMessageId") is { Length: > 0 } k ? k : message.Req("id");

    /// <summary>message.get("from", {}).get("emailAddress", {}).get("address", ""), failing where Python would.</summary>
    public static string Sender(JsonObject message)
    {
        var from = message.Has("from") ? message.Get("from") : new JsonObject();
        if (from is not JsonObject f)
            throw new InvalidOperationException("'NoneType' object has no attribute 'get' (from)");
        var address = f.Has("emailAddress") ? f.Get("emailAddress") : new JsonObject();
        if (address is not JsonObject a)
            throw new InvalidOperationException("'NoneType' object has no attribute 'get' (emailAddress)");
        if (!a.Has("address"))
            return "";
        return a.Str("address") ?? throw new InvalidOperationException("expected string or bytes-like object (address)");
    }

    private static string Preview(JsonObject message)
    {
        if (!message.Has("bodyPreview"))
            return "";
        return PyStr.Head(message.Str("bodyPreview") ?? throw new InvalidOperationException("'NoneType' object is not subscriptable (bodyPreview)"), 300);
    }

    public static List<JsonObject> Queue(Settings settings) =>
        StateFiles.LoadObject(settings.QueueFile, () => []).Get("review").Objects().Select(o => (JsonObject)o.DeepClone()).ToList();

    public static void SaveQueue(Settings settings, List<JsonObject> items)
    {
        var review = new JsonArray();
        var n = 1;
        foreach (var item in items)
        {
            item["n"] = n++;
            review.Add(item.DeepClone());
        }
        StateFiles.Save(settings.QueueFile, new JsonObject { ["checked"] = PyTime.Iso(Clock.Now, "seconds"), ["review"] = review });
    }

    private static string NowMicro() => PyTime.Iso(Clock.Now, "microseconds");

    private static string NowSeconds() => PyTime.Iso(Clock.Now, "seconds");

    /// <summary>One run. calendar is needed when the settings have [invites].</summary>
    public static RunReport Run(Settings settings, Mailbox mailbox, ICalendar? calendar, bool dryRun, Action<string> log)
    {
        var raised = StateFiles.LoadObject(settings.StateFile, () => []);
        var previous = Queue(settings);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<JsonObject>();
        int deleted = 0, again = 0, filed = 0;
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var items = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.DeletedFile} has no items list.");

        foreach (var folder in settings.Folders)
        {
            foreach (var message in mailbox.Messages(mailbox.FolderId(folder.Name), folder.Since))
            {
                var key = Key(message);
                seen.Add(key);
                if (raised.ContainsKey(key))
                {
                    again++;
                    continue;
                }
                var text = $"{message.Fmt("subject")}\n{message.Fmt("bodyPreview")}";
                Rule? rule;
                string? note = null;
                string action;
                try
                {
                    rule = settings.RuleFor(Sender(message), text, folder);
                    if (rule is null && folder.Unmatched == "leave")
                        continue;   // an inbox: only the rules act there
                    action = rule?.Action ?? "review";
                }
                catch (RegexMatchTimeoutException error)
                {
                    // A pattern that runs out of time decides nothing: never a delete (a .NET-only safeguard).
                    rule = null;
                    action = "review";
                    note = $"a rule's pattern ran out of time ({Py.ReprString(error.Pattern)}), so no rule was applied";
                }
                if (action == "check")
                {
                    var (needsBody, check) = Checks.All[rule!.Check!];
                    (var fine, note) = check(message, needsBody ? mailbox.Body(message.Req("id")) : "", settings);
                    action = fine ? "delete" : "review";
                }
                var label = $"[{(rule is not null ? rule.Name : "no rule")}] {Sender(message)}: {message.Fmt("subject")}"
                            + (note is { Length: > 0 } ? $" ({note})" : "");
                if (action == "delete")
                {
                    if (!dryRun)
                    {
                        mailbox.Delete(message.Req("id"));
                        var entry = new JsonObject
                        {
                            ["when"] = NowMicro(), ["folder"] = folder.Name, ["received"] = message.GetCopy("receivedDateTime"),
                            ["from"] = Sender(message), ["subject"] = message.GetCopy("subject", ""), ["rule"] = rule is not null ? rule.Name : "",
                        };
                        if (note is { Length: > 0 })
                            entry["check"] = note;
                        items.Add((JsonNode)entry);
                    }
                    log($"{(dryRun ? "would delete" : "deleted")}  {label}");
                    deleted++;
                    continue;
                }
                if (action == "file")
                {
                    if (!dryRun)
                    {
                        mailbox.File(message.Req("id"), mailbox.FolderPath(rule!.To!));
                        items.Add((JsonNode)new JsonObject
                        {
                            ["when"] = NowMicro(), ["folder"] = folder.Name, ["received"] = message.GetCopy("receivedDateTime"),
                            ["from"] = Sender(message), ["subject"] = message.GetCopy("subject", ""), ["rule"] = rule.Name, ["to"] = rule.To,
                        });
                    }
                    log($"{(dryRun ? "would file" : "filed")} to {rule!.To}  {label}");
                    filed++;
                    continue;
                }
                var item = new JsonObject
                {
                    ["folder"] = folder.Name, ["id"] = message.Req("id"), ["key"] = key, ["received"] = message.GetCopy("receivedDateTime"),
                    ["from"] = Sender(message), ["subject"] = message.GetCopy("subject", ""), ["preview"] = Preview(message),
                    ["rule"] = rule is not null ? rule.Name : "no rule matched",
                };
                if (note is { Length: > 0 })
                    item["check"] = note;
                item["webLink"] = message.Get("webLink").Copy();
                added.Add(item);
                if (!raised.ContainsKey(key))
                    raised[key] = NowSeconds();
                log($"{(dryRun ? "would queue" : "queued")}  {label}");
            }
        }

        var invitesNew = 0;
        if (settings.Invites is { } invites)
        {
            var cal = calendar ?? throw new InvalidOperationException("[invites] needs a calendar.");
            // The first run has nothing to compare with, so it only remembers what's accepted.
            var accepted = StateFiles.LoadObject(settings.AcceptedFile, () => []);
            var acted = new List<JsonObject>();
            var (inviteItems, pendingKeys, _) = InviteTriage.Triage(settings, cal, raised, log, accepted: accepted, acted: acted, dryRun: dryRun);
            if (!dryRun)
                StateFiles.Save(settings.AcceptedFile, accepted);
            foreach (var a in acted)
                items.Add((JsonNode)new JsonObject
                {
                    ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = a.GetCopy("received"), ["from"] = a.GetCopy("from"),
                    ["subject"] = a.GetCopy("subject"), ["rule"] = "Accepted: already accepted, only shortened", ["accepted"] = a.GetCopy("why"),
                });
            if (invites.ProposalsFolders.Count > 0)
            {
                var (pItems, pActed, pSeen) = Proposals.Run(settings, mailbox, cal, raised, log, dryRun);
                added.AddRange(pItems);
                seen.UnionWith(pSeen);
                foreach (var a in pActed)
                    items.Add((JsonNode)new JsonObject
                    {
                        ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = a.GetCopy("received"), ["from"] = a.GetCopy("from"),
                        ["subject"] = a.GetCopy("subject"), ["rule"] = "Accepted: new time proposed, and free", ["accepted"] = a.GetCopy("why"),
                    });
            }
            seen.UnionWith(pendingKeys);   // an invite answered elsewhere (in Outlook) drops off the queue
            added.AddRange(inviteItems);
            invitesNew = inviteItems.Count;
            if (settings.OneToOnes is not null)
            {
                var (oItems, oActed, oSeen) = OneToOnePlanner.Run(settings, cal, raised, log, dryRun);
                added.AddRange(oItems);
                seen.UnionWith(oSeen);
                foreach (var a in oActed)
                    items.Add((JsonNode)new JsonObject
                    {
                        ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = a.GetCopy("received"), ["from"] = a.GetCopy("from"),
                        ["subject"] = a.GetCopy("subject"), ["rule"] = a.GetCopy("rule"), ["did"] = a.GetCopy("why"),
                    });
            }
            foreach (var name in invites.RequestsFolders)
            {
                foreach (var m in Proposals.AnsweredRequests(mailbox.Graph, mailbox.FolderId(name)))
                {
                    if (!dryRun)
                    {
                        mailbox.Delete(m.Req("id"));
                        items.Add((JsonNode)new JsonObject
                        {
                            ["when"] = NowMicro(), ["folder"] = name, ["received"] = m.Get("receivedDateTime").Copy(), ["from"] = Sender(m),
                            ["subject"] = m.GetCopy("subject", ""), ["rule"] = "Answered invitations",
                        });
                    }
                    log($"{(dryRun ? "would delete" : "deleted")}  [answered invitation: {Py.Str(m.Get("answer"))}] {Py.Str(m.Get("subject"))}");
                    deleted++;
                }
            }
        }
        // Still waiting from earlier runs, and still in the folder (one deleted by hand in Outlook drops out).
        var newKeys = added.Select(i => Py.Str(i.Get("key"))).ToHashSet(StringComparer.Ordinal);
        var carried = previous.Where(i => i.Get("key") is { } k && seen.Contains(Py.Str(k)) && !newKeys.Contains(Py.Str(k))).ToList();
        if (!dryRun)
        {
            StateFiles.Save(settings.StateFile, raised);
            var cutoff = PyTime.Iso(Clock.Now - TimeSpan.FromDays(KeepDays));
            var kept = new JsonArray();
            foreach (var entry in items.ToList())
                if (PyStr.CompareCodePoints(entry.Req("when"), cutoff) >= 0)
                    kept.Add(entry!.DeepClone());
            record["items"] = kept;
            StateFiles.Save(settings.DeletedFile, record);
            SaveQueue(settings, [.. carried, .. added]);
        }
        var report = new RunReport(deleted, filed, added.Count, carried.Count, Math.Max(0, again - carried.Count), invitesNew);
        log($"done: {deleted} deleted, " + (filed > 0 ? $"{filed} filed, " : "") + $"{added.Count} new to review"
            + (invitesNew > 0 ? $" ({invitesNew} invites)" : "") + $", {carried.Count} still waiting from earlier runs, {report.Kept} seen before and kept");
        return report;
    }

    /// <summary>Queue items by their number in `list` (1, 2...) or by Graph id. Unknown references throw.</summary>
    public static List<JsonObject> Pick(Settings settings, IEnumerable<string> refs)
    {
        var items = Queue(settings);
        var found = new List<JsonObject>();
        foreach (var r in refs)
        {
            var item = items.FirstOrDefault(i => (i.Has("n") ? Py.Str(i.Get("n")) : "None") == r || i.Str("id") == r)
                       ?? throw new LookupException($"Nothing in the queue is {Py.ReprString(r)}. Run `mail-triage list`.");
            found.Add(item);
        }
        return found;
    }

    private static string? TypeOf(JsonObject? item) => item?.Str("type");

    /// <summary>Delete emails from the queue (by number or id), or any email by its Graph id.</summary>
    public static int Delete(Settings settings, Mailbox? mailbox, IReadOnlyList<string> refs, Action<string> log)
    {
        var items = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var i in Queue(settings))
            items[i.Req("id")] = i;
        var ids = new List<string>();
        foreach (var r in refs)
            ids.Add(PyStr.IsDigit(r) && r.Length < 6 ? Pick(settings, [r])[0].Req("id") : r);
        var invites = ids.Where(i => TypeOf(items.GetValueOrDefault(i)) == "invite").Select(i => items[i].Fmt("subject")).ToList();
        if (invites.Count > 0)
            throw new LookupException($"{invites[0]} is an invite, not an email: answer it with `mail-triage invite decline <n>`.");
        var meetings = ids.Where(i => TypeOf(items.GetValueOrDefault(i)) == "one-to-one").Select(i => items[i].Fmt("subject")).ToList();
        if (meetings.Count > 0)
            throw new LookupException($"{meetings[0]} is your own meeting, not an email: change it in Outlook (or `mail-triage move`), "
                                      + "then `mail-triage keep <n>`.");
        var box = mailbox ?? throw new InvalidOperationException("No mailbox.");
        foreach (var id in ids)
        {
            box.Delete(id);
            var item = items.GetValueOrDefault(id);
            log($"deleted by hand  {(item is not null ? item.Fmt("from") + ": " + item.Fmt("subject") : PyStr.Head(id, 24) + "…")}");
        }
        SaveQueue(settings, Queue(settings).Where(i => !ids.Contains(i.Req("id"))).ToList());
        return ids.Count;
    }

    /// <summary>Move queued emails to a folder (left unread), and off the queue.</summary>
    public static int File(Settings settings, Mailbox? mailbox, IReadOnlyList<string> refs, string to, Action<string> log)
    {
        var chosen = Pick(settings, refs);
        if (chosen.Any(i => TypeOf(i) is "invite" or "one-to-one"))
            throw new LookupException("A meeting can't be filed: answer an invite with `mail-triage invite`.");
        var box = mailbox ?? throw new InvalidOperationException("No mailbox.");
        var folderId = box.FolderPath(to);
        foreach (var item in chosen)
        {
            box.File(item.Req("id"), folderId);
            log($"filed to {to} by hand  {item.Fmt("from")}: {item.Fmt("subject")}");
        }
        var gone = chosen.Select(i => i.Req("id")).ToHashSet(StringComparer.Ordinal);
        SaveQueue(settings, Queue(settings).Where(i => !gone.Contains(i.Req("id"))).ToList());
        return chosen.Count;
    }

    /// <summary>Answer queued invites (by number): accept, tentative or decline. Accepting a series accepts it whole,
    /// with each unanswered occurrence changed on its own; declining declines instances, never the series, unless
    /// series is true. No response is sent where the organiser didn't ask for one.</summary>
    public static int Answer(Settings settings, ICalendar calendar, IReadOnlyList<string> refs, string action, string note = "",
        bool send = true, Action<string>? log = null, bool series = false, bool move = false)
    {
        log ??= _ => { };
        var chosen = Pick(settings, refs);
        var wrong = chosen.Where(i => TypeOf(i) != "invite").Select(i => i.Fmt("subject")).ToList();
        if (wrong.Count > 0)
            throw new LookupException($"{wrong[0]} isn't an invite.");
        if (series && chosen.Any(i => !i.Truthy("series")))
            throw new LookupException("--series is for a recurring meeting; that one isn't.");
        var keep = new List<string>();
        foreach (var item in chosen)
        {
            var respond = send && (!item.Has("responseRequested") || Py.Truthy(item.Get("responseRequested")));
            List<string> targets;
            string text;
            if (action == "decline")
            {
                targets = series ? [item.Req("series")] : item.Truthy("decline") ? item.Get("decline").Strings() : [item.Req("occurrence")];
                text = note.Length > 0 ? note : item.Str("note") ?? "";
            }
            else
            {
                // An occurrence the organiser changed on its own (an exception) isn't answered by the series' answer.
                targets = [item.Truthy("series") ? item.Req("series") : item.Req("occurrence"), .. item.Get("exceptions").Strings()];
                text = note;
            }
            foreach (var target in targets)
                calendar.Respond(target, action, text, respond);
            var done = action switch { "accept" => "accepted", "tentative" => "accepted tentatively", "decline" => "declined", _ => throw new KeyNotFoundException(action) };
            var what = series || (action != "decline" && item.Truthy("series")) ? "the whole series"
                : $"{targets.Count} occurrence{(targets.Count > 1 ? "s" : "")}";
            log($"invite {done} ({what}){(respond ? "" : " (no response sent)")}  "
                + $"{item.Fmt("from")}: {item.Fmt("subject")}" + (text.Length > 0 && respond ? $" | note: {text}" : ""));
            if (move && action == "accept")
            {
                foreach (var c in item.Get("clashes").Objects())
                {
                    if (!c.Truthy("to"))
                        continue;
                    var start = PyTime.FromIso(c.Req("to"));
                    var end = PyTime.FromIso(c.Req("to_end"));
                    calendar.Move(c.Req("id"), start, end);
                    var was = c.Req("start");
                    log($"moved  {c.Fmt("subject")}: {was[5..Math.Min(16, was.Length)]} -> {PyTime.Strftime(start, "%a %d %b %H:%M")}–{PyTime.Strftime(end, "%H:%M")}");
                }
            }
            if (action != "decline" && item.Truthy("decline") && (item.Str("recommendation") ?? "").StartsWith("accept, and decline", StringComparison.Ordinal))
            {
                keep.Add(item.Req("id"));
                log($"  still queued: decline {item.Get("decline").Strings().Count} occurrence(s) with `mail-triage invite decline {item.Fmt("n", "None")}`");
            }
        }
        var gone = chosen.Select(i => i.Req("id")).Except(keep).ToHashSet(StringComparer.Ordinal);
        SaveQueue(settings, Queue(settings).Where(i => !gone.Contains(i.Req("id"))).ToList());
        return chosen.Count;
    }

    /// <summary>Take emails off the queue and leave them in their folder. They won't be raised again.</summary>
    public static int Keep(Settings settings, IReadOnlyList<string> refs, Action<string> log)
    {
        var gone = Pick(settings, refs).Select(i => i.Req("id")).ToHashSet(StringComparer.Ordinal);
        SaveQueue(settings, Queue(settings).Where(i => !gone.Contains(i.Req("id"))).ToList());
        log($"kept {gone.Count}: off the queue, left in the folder");
        return gone.Count;
    }

    /// <summary>Empty the queue; what the rules deleted counts as seen too.</summary>
    public static int Clear(Settings settings, Action<string> log)
    {
        var count = Queue(settings).Count;
        SaveQueue(settings, []);
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        record["seen_until"] = NowMicro();
        StateFiles.Save(settings.DeletedFile, record);
        log($"queue cleared: {count}");
        return count;
    }

    /// <summary>What the rules deleted since the queue was last cleared (or all that's kept), oldest first.</summary>
    public static List<JsonObject> Deleted(Settings settings, bool everything = false)
    {
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var since = everything ? "" : record.Has("seen_until") ? Py.Str(record.Get("seen_until")) : "";
        return record.Get("items").Objects().Where(i => PyStr.CompareCodePoints(i.Req("when"), since) > 0)
            .Select(i => (JsonObject)i.DeepClone()).ToList();
    }
}
