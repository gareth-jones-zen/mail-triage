using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;

namespace MailTriage;

/// <summary>What a run did: the counts the Python's report dict held.</summary>
public sealed record RunReport(int Deleted, int Filed, int New, int Waiting, int Kept, int Invites)
{
    /// <summary>Emails a handler handled (each also counted under what then did with it).</summary>
    public int Handled { get; init; }

    /// <summary>Emails a forward rule sent on (1.9.0).</summary>
    public int Forwarded { get; init; }
}

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
    /// <summary>A meeting reply proposing a new time, in a folder the proposals sweep reads: the sweep decides it (accept
    /// when free, otherwise queue it with the reason). Until 1.6.2 the folder pass got there first once the Inbox was
    /// triaged (16 Nov 2026): it queued the proposal as "no rule", and the sweep then skipped it as already raised, so a
    /// proposal GJ was free for (Yahya's, 17 Nov) was never accepted. One extra read per meeting reply not yet seen.</summary>
    private static bool LeftToProposals(Settings settings, Mailbox mailbox, Folder folder, JsonObject message) =>
        settings.Invites is { AcceptProposals: true } invites
        && invites.ProposalsFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase)
        && message.Str("@odata.type") == "#microsoft.graph.eventMessageResponse"
        && Proposals.IsProposal(mailbox.Graph, message.Req("id"));

    public static RunReport Run(Settings settings, Mailbox mailbox, ICalendar? calendar, bool dryRun, Action<string> log, Handlers? handlers = null)
    {
        var raised = StateFiles.LoadObject(settings.StateFile, () => []);
        var previous = Queue(settings);
        Checks.Graph = mailbox.Graph;   // for the checks that read Graph (sharepoint-access, 1.12.0)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<JsonObject>();
        int deleted = 0, again = 0, filed = 0, handled = 0, forwarded = 0;
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var items = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.DeletedFile} has no items list.");
        var waiting = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var i in previous)
            if (i.Get("key") is { } k)
                waiting.TryAdd(Py.Str(k), i);
        var resolved = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in settings.Folders)
        {
            foreach (var message in mailbox.Messages(mailbox.FolderId(folder.Name), folder.Since))
            {
                var key = Key(message);
                seen.Add(key);
                if (raised.ContainsKey(key))
                {
                    if (waiting.TryGetValue(key, out var queued) && !resolved.Contains(key)
                        && Recheck(settings, mailbox, folder, message, queued) is { } passed)
                    {
                        // Its check passes now (1.9.0): deleted as a first-time pass would be, and off the queue.
                        var passLabel = $"[{passed.Rule.Name}] {Sender(message)}: {message.Fmt("subject")} ({passed.Why})";
                        if (!dryRun)
                        {
                            mailbox.Delete(message.Req("id"));
                            items.Add((JsonNode)new JsonObject
                            {
                                ["when"] = NowMicro(), ["folder"] = folder.Name, ["received"] = message.GetCopy("receivedDateTime"),
                                ["from"] = Sender(message), ["subject"] = message.GetCopy("subject", ""), ["rule"] = passed.Rule.Name,
                                ["check"] = passed.Why,
                            });
                            resolved.Add(key);
                        }
                        log($"{(dryRun ? "would delete" : "deleted")}  {passLabel}");
                        deleted++;
                        continue;
                    }
                    again++;
                    continue;
                }
                if (LeftToProposals(settings, mailbox, folder, message))
                    continue;   // a new time proposed: the proposals sweep below decides it (1.6.2)
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
                var to = rule?.To;
                string? did = null;
                if (action == "handle")
                {
                    var handler = settings.Handlers[rule!.Handler!];
                    var subjectLabel = $"[{rule.Name}] {Sender(message)}: {message.Fmt("subject")}";
                    if (handlers is null || dryRun)
                    {
                        // A dry run, or a write mode that sends nothing: a handler writes outside mail-triage, so it doesn't run.
                        log($"would hand to {handler.Name}, then {handler.Then}  {subjectLabel}");
                        continue;
                    }
                    var outcome = handlers.Handle(mailbox, message, folder.Name, rule, handler);
                    if (outcome.Result == "retry")
                    {
                        log($"{handler.Name} will try again next run  {subjectLabel}" + (outcome.Note.Length > 0 ? $" ({outcome.Note})" : ""));
                        continue;
                    }
                    if (outcome.Result == "failed")
                    {
                        action = "review";
                        note = outcome.Note;
                    }
                    else
                    {
                        handled++;
                        did = $"handled by {handler.Name}" + (outcome.Note.Length > 0 ? $": {outcome.Note}" : "");
                        action = handler.Then;
                        to = handler.To;
                        if (action == "review")
                            note = did;
                        if (action == "leave")
                        {
                            items.Add((JsonNode)new JsonObject
                            {
                                ["when"] = NowMicro(), ["folder"] = folder.Name, ["received"] = message.GetCopy("receivedDateTime"),
                                ["from"] = Sender(message), ["subject"] = message.GetCopy("subject", ""), ["rule"] = rule.Name, ["did"] = did + ", left in the folder",
                            });
                            raised[key] = NowSeconds();
                            log($"{did}, left in the folder  {subjectLabel}");
                            continue;
                        }
                    }
                }
                var label = $"[{(rule is not null ? rule.Name : "no rule")}] {Sender(message)}: {message.Fmt("subject")}"
                            + (note is { Length: > 0 } ? $" ({note})" : did is not null ? $" ({did})" : "");
                if (action == "forward")
                {
                    if (dryRun)
                    {
                        log($"would {Forwarding.Describe(rule!)}  {label}");
                        forwarded++;
                        continue;
                    }
                    var why = Forwarding.Apply(settings, mailbox, key, message.Req("id"), rule!);
                    if (why is null)
                    {
                        items.Add((JsonNode)Forwarding.Entry(folder.Name, message, rule!, NowMicro()));
                        log($"{Forwarding.Describe(rule!, done: true)}  {label}");
                        forwarded++;
                        continue;
                    }
                    action = "review";   // an earlier forward's answer was lost: a person checks, it's never resent
                    note = why;
                    label += $" ({why})";
                }
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
                        if (did is not null)
                            entry["did"] = did;
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
                        mailbox.File(message.Req("id"), mailbox.FolderPath(to!));
                        var entry = new JsonObject
                        {
                            ["when"] = NowMicro(), ["folder"] = folder.Name, ["received"] = message.GetCopy("receivedDateTime"),
                            ["from"] = Sender(message), ["subject"] = message.GetCopy("subject", ""), ["rule"] = rule!.Name, ["to"] = to,
                        };
                        if (did is not null)
                            entry["did"] = did;
                        items.Add((JsonNode)entry);
                    }
                    log($"{(dryRun ? "would file" : "filed")} to {to}  {label}");
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
            var calendarEvents = new List<JsonObject>();
            var (inviteItems, pendingKeys, _) = InviteTriage.Triage(settings, cal, raised, log, accepted: accepted, acted: acted, dryRun: dryRun,
                eventsOut: calendarEvents);
            if (!dryRun)
                StateFiles.Save(settings.AcceptedFile, accepted);
            foreach (var a in acted)
                items.Add((JsonNode)new JsonObject
                {
                    ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = a.GetCopy("received"), ["from"] = a.GetCopy("from"),
                    ["subject"] = a.GetCopy("subject"), ["rule"] = a.Str("rule") ?? "Accepted: already accepted, only shortened",
                    [a.Has("rule") ? "did" : "accepted"] = a.GetCopy("why"),
                });
            if (invites.DeclineDaysOff)
            {
                var done = StateFiles.LoadObject(settings.DaysOffFile, () => []);
                foreach (var a in InviteTriage.DeclineDaysOff(settings, cal, calendarEvents, done, log, Clock.Now, dryRun))
                    items.Add((JsonNode)new JsonObject
                    {
                        ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = a.GetCopy("received"), ["from"] = a.GetCopy("from"),
                        ["subject"] = a.GetCopy("subject"), ["rule"] = a.GetCopy("rule"), ["did"] = a.GetCopy("why"),
                    });
                if (!dryRun)
                    StateFiles.Save(settings.DaysOffFile, done);
            }
            if (invites.CancelDaysOff || invites.RemoveBlocksDaysOff || invites.RaiseGroupDaysOff)
            {
                // 1.14.0: what you organise on your days off
                var done = StateFiles.LoadObject(settings.DaysOffFile, () => []);
                var (oItems, oActed, oSeen) = InviteTriage.OwnDaysOff(settings, cal, calendarEvents, done, raised, log, Clock.Now, dryRun);
                added.AddRange(oItems);
                seen.UnionWith(oSeen);
                foreach (var a in oActed)
                    items.Add((JsonNode)new JsonObject
                    {
                        ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = a.GetCopy("received"), ["from"] = a.GetCopy("from"),
                        ["subject"] = a.GetCopy("subject"), ["rule"] = a.GetCopy("rule"), ["did"] = a.GetCopy("why"),
                    });
                if (!dryRun)
                    StateFiles.Save(settings.DaysOffFile, done);
            }
            if (invites.DoubleBookings)
            {
                // 1.13.0: the [[double_booking_rule]]s settle what they can first; the rest is queued as before.
                var done = StateFiles.LoadObject(settings.DoubleBookingsFile, () => []);
                var (dItems, dActed, dSeen) = DoubleBookingPlanner.Run(settings, cal, calendarEvents, raised, log, Clock.Now, done, dryRun);
                added.AddRange(dItems);
                seen.UnionWith(dSeen);
                foreach (var a in dActed)
                    items.Add((JsonNode)new JsonObject
                    {
                        ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = a.GetCopy("received"), ["from"] = a.GetCopy("from"),
                        ["subject"] = a.GetCopy("subject"), ["rule"] = a.GetCopy("rule"), ["did"] = a.GetCopy("why"),
                    });
                if (!dryRun && settings.DoubleBookings is not null)
                    StateFiles.Save(settings.DoubleBookingsFile, done);
            }
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
                    var cancellation = m.Has("remove") || Py.Str(m.Get("answer")).StartsWith("cancelled,", StringComparison.Ordinal);
                    if (!dryRun)
                    {
                        // The meeting comes off the calendar first, so a failure leaves the email to try again next run.
                        if (m.Has("remove"))
                            cal.Remove(m.Req("remove"));
                        mailbox.Delete(m.Req("id"));
                        var entry = new JsonObject
                        {
                            ["when"] = NowMicro(), ["folder"] = name, ["received"] = m.Get("receivedDateTime").Copy(), ["from"] = Sender(m),
                            ["subject"] = m.GetCopy("subject", ""), ["rule"] = cancellation ? "Cancelled meetings" : "Answered invitations",
                        };
                        if (m.Has("remove"))
                            entry["did"] = "removed from the calendar";
                        items.Add((JsonNode)entry);
                    }
                    log($"{(dryRun ? "would delete" : "deleted")}  [{(cancellation ? "cancellation" : "answered invitation")}: "
                        + $"{Py.Str(m.Get("answer"))}] {Py.Str(m.Get("subject"))}");
                    deleted++;
                }
            }
        }
        // Still waiting from earlier runs, and still in the folder (one deleted by hand in Outlook drops out).
        var newKeys = added.Select(i => Py.Str(i.Get("key"))).ToHashSet(StringComparer.Ordinal);
        var carried = previous.Where(i => i.Get("key") is { } k && seen.Contains(Py.Str(k)) && !newKeys.Contains(Py.Str(k))
                                          && !resolved.Contains(Py.Str(k))).ToList();
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
        var report = new RunReport(deleted, filed, added.Count, carried.Count, Math.Max(0, again - carried.Count), invitesNew) { Handled = handled, Forwarded = forwarded };
        log($"done: {deleted} deleted, " + (filed > 0 ? $"{filed} filed, " : "") + (forwarded > 0 ? $"{forwarded} forwarded, " : "")
            + (handled > 0 ? $"{handled} handled, " : "") + $"{added.Count} new to review"
            + (invitesNew > 0 ? $" ({invitesNew} invites)" : "") + $", {carried.Count} still waiting from earlier runs, {report.Kept} seen before and kept");
        return report;
    }

    /// <summary>A queued email raised by a `check` rule, checked again (1.9.0): the check ran once, when the email
    /// arrived, and a failed build checked while its fixing run was still going (17 Nov 2026) sat in the queue saying
    /// "still failing" for two days. Items whose rule (by name) is still a check rule, and (1.12.0) items queued with no
    /// rule that a check rule now matches, as a new check rule would have checked them (Stu Cowley's access request,
    /// queued before the sharepoint-access rule, was approved by another owner meanwhile). Returns the rule and why
    /// when it passes now; otherwise null, with the item's note updated if the reason changed. A check that throws
    /// leaves the item as it was: an error never removes anything.</summary>
    private static (Rule Rule, string Why)? Recheck(Settings settings, Mailbox mailbox, Folder folder, JsonObject message, JsonObject queued)
    {
        var rule = settings.Rules.FirstOrDefault(r => r.Name == queued.Str("rule") && r.Action == "check");
        if (rule is null && queued.Str("rule") == "no rule matched")
        {
            try
            {
                rule = settings.RuleFor(Sender(message), $"{message.Fmt("subject")}\n{message.Fmt("bodyPreview")}", folder) is { Action: "check" } now ? now : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }
        if (rule is not { Check: { } name } || !Checks.All.TryGetValue(name, out var check))
            return null;
        bool fine;
        string why;
        try
        {
            (fine, why) = check.Run(message, check.NeedsBody ? mailbox.Body(message.Req("id")) : "", settings);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or M365Graph.SignInNeededException))
        {
            return null;
        }
        if (fine)
            return (rule, why);
        if (why.Length > 0 && why != queued.Str("check"))
            queued["check"] = why;
        return null;
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
        var meetings = ids.Where(i => TypeOf(items.GetValueOrDefault(i)) is "one-to-one" or "double-booking" or "own-meeting").Select(i => items[i].Fmt("subject")).ToList();
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

    /// <summary>Give one email to a handler by hand: a queued email (by number or id), or any email by its Graph id.
    /// The handler is the one its rule names, or handlerName. Once handled, its then applies, as in a run.
    /// Returns 0 handled (or to try again later), 1 if the handler failed.</summary>
    public static int HandleByHand(Settings settings, Mailbox mailbox, Handlers handlers, string reference, string? handlerName, Action<string> log)
    {
        var item = PyStr.IsDigit(reference) && reference.Length < 6 ? Pick(settings, [reference])[0]
            : Queue(settings).FirstOrDefault(i => i.Str("id") == reference);
        if (TypeOf(item) is "invite" or "one-to-one" or "double-booking" or "own-meeting")
            throw new LookupException($"{item!.Fmt("subject")} is a meeting, not an email.");
        var id = item?.Req("id") ?? reference;
        var message = mailbox.Graph.Request($"/me/messages/{id}?$select={Mailbox.Fields}", scopes: mailbox.ReadScopes);
        var rule = settings.RuleFor(Sender(message), $"{message.Fmt("subject")}\n{message.Fmt("bodyPreview")}");
        var name = handlerName ?? rule?.Handler
                   ?? throw new LookupException($"No rule hands {Py.ReprString(message.Fmt("subject"))} to a handler: name one with --handler.");
        var handler = settings.Handlers.GetValueOrDefault(name)
                      ?? throw new LookupException($"No [[handler]] called {Py.ReprString(name)} in {settings.Path}.");
        var folder = item?.Str("folder") ?? "";
        var label = $"{Sender(message)}: {message.Fmt("subject")}";
        var outcome = handlers.Handle(mailbox, message, folder, rule, handler);
        if (outcome.Result == "retry")
        {
            log($"{handler.Name} asked to try again later  {label}" + (outcome.Note.Length > 0 ? $" ({outcome.Note})" : ""));
            return 0;
        }
        if (outcome.Result == "failed")
        {
            log($"{outcome.Note}  {label}");
            if (item is not null)
                SaveQueue(settings, Queue(settings).Select(i => { if (i.Str("id") == id) i["check"] = outcome.Note; return i; }).ToList());
            return 1;
        }
        var did = $"handled by {handler.Name}" + (outcome.Note.Length > 0 ? $": {outcome.Note}" : "");
        if (handler.Then == "delete")
            mailbox.Delete(id);
        else if (handler.Then == "file")
            mailbox.File(id, mailbox.FolderPath(handler.To!));
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var entry = new JsonObject
        {
            ["when"] = NowMicro(), ["folder"] = folder, ["received"] = message.GetCopy("receivedDateTime"), ["from"] = Sender(message),
            ["subject"] = message.GetCopy("subject", ""), ["rule"] = rule?.Name ?? handler.Name, ["did"] = handler.Then == "leave" ? did + ", left in the folder" : did,
        };
        if (handler.Then == "file")
            entry["to"] = handler.To;
        if (handler.Then != "review")
        {
            (record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.DeletedFile} has no items list.")).Add((JsonNode)entry);
            StateFiles.Save(settings.DeletedFile, record);
        }
        var raised = StateFiles.LoadObject(settings.StateFile, () => []);
        if (!raised.ContainsKey(Key(message)))
            raised[Key(message)] = NowSeconds();
        StateFiles.Save(settings.StateFile, raised);
        var queue = Queue(settings);
        SaveQueue(settings, handler.Then == "review"
            ? queue.Select(i => { if (i.Str("id") == id) i["check"] = did; return i; }).ToList()
            : queue.Where(i => i.Str("id") != id).ToList());
        log($"{did}, then {handler.Then}  {label}");
        return 0;
    }

    /// <summary>Move queued emails to a folder (left unread), and off the queue.</summary>
    public static int File(Settings settings, Mailbox? mailbox, IReadOnlyList<string> refs, string to, Action<string> log)
    {
        var chosen = Pick(settings, refs);
        if (chosen.Any(i => TypeOf(i) is "invite" or "one-to-one" or "double-booking" or "own-meeting"))
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

    /// <summary>Apply a forward rule to queued emails (by number or Graph id): the rule they now match, or the one named.
    /// For an email queued before its rule existed. Each is forwarded once at most (Forwarding), recorded like a run's
    /// forward, and taken off the queue. Returns 0, or 1 if any was left queued because an earlier forward's answer was lost.</summary>
    public static int Forward(Settings settings, Mailbox mailbox, IReadOnlyList<string> refs, string? ruleName, Action<string> log)
    {
        var chosen = Pick(settings, refs);
        if (chosen.Any(i => TypeOf(i) is "invite" or "one-to-one" or "double-booking" or "own-meeting"))
            throw new LookupException("A meeting can't be forwarded.");
        Rule? named = null;
        if (ruleName is not null)
            named = settings.Rules.FirstOrDefault(r => r.Name == ruleName)
                    ?? throw new LookupException($"No rule called {Py.ReprString(ruleName)} in {settings.Path}.");
        if (named is not null && named.Action != "forward")
            throw new LookupException($"Rule {Py.ReprString(named.Name)} doesn't forward (its action is {named.Action}).");
        var plan = new List<(JsonObject Item, JsonObject Message, Rule Rule)>();
        foreach (var item in chosen)
        {
            var message = mailbox.Graph.Request($"/me/messages/{item.Req("id")}?$select={Mailbox.Fields}", scopes: mailbox.ReadScopes);
            var rule = named ?? settings.RuleFor(Sender(message), $"{message.Fmt("subject")}\n{message.Fmt("bodyPreview")}");
            if (rule is not { Action: "forward" })
                throw new LookupException($"No forward rule matches {Py.ReprString(message.Fmt("subject"))}: name one with --rule.");
            plan.Add((item, message, rule));
        }
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var items = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.DeletedFile} has no items list.");
        var raised = StateFiles.LoadObject(settings.StateFile, () => []);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (item, message, rule) in plan)
            {
                var label = $"{Sender(message)}: {message.Fmt("subject")}";
                var why = Forwarding.Apply(settings, mailbox, Key(message), item.Req("id"), rule);
                if (why is not null)
                {
                    notes[item.Req("id")] = why;
                    log($"not forwarded ({why})  {label}");
                    continue;
                }
                items.Add((JsonNode)Forwarding.Entry(item.Str("folder") ?? "", message, rule, NowMicro()));
                if (!raised.ContainsKey(Key(message)))
                    raised[Key(message)] = NowSeconds();
                gone.Add(item.Req("id"));
                log($"{Forwarding.Describe(rule, done: true)} by hand  {label}");
            }
        }
        finally
        {
            // What was done is recorded even if a later one fails.
            StateFiles.Save(settings.DeletedFile, record);
            StateFiles.Save(settings.StateFile, raised);
            SaveQueue(settings, Queue(settings).Where(i => !gone.Contains(i.Req("id")))
                .Select(i => { if (notes.TryGetValue(i.Req("id"), out var n)) i["check"] = n; return i; }).ToList());
        }
        return notes.Count > 0 ? 1 : 0;
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

    /// <summary>Answer an invite without asking (1.11.0): accept (the series, and any occurrence changed on its own) or
    /// decline (the occurrences named), then any moves of your own blocks, then the occurrences on your days off.</summary>
    internal static void Apply(ICalendar calendar, JsonObject item, string action, bool move, Action<string> log)
    {
        var respond = !item.Has("responseRequested") || Py.Truthy(item.Get("responseRequested"));
        var note = item.Str("note") ?? "";
        if (action == "decline")
        {
            foreach (var target in item.Truthy("decline") ? item.Get("decline").Strings() : [item.Req("occurrence")])
                calendar.Respond(target, "decline", note, respond);
            return;
        }
        foreach (var target in new[] { item.Truthy("series") ? item.Req("series") : item.Req("occurrence") }.Concat(item.Get("exceptions").Strings()))
            calendar.Respond(target, "accept", "", respond);
        if (move)
            foreach (var c in item.Get("clashes").Objects().Where(c => c.Truthy("to")))
            {
                calendar.Move(c.Req("id"), PyTime.FromIso(c.Req("to")), PyTime.FromIso(c.Req("to_end")));
                log($"moved  {c.Fmt("subject")} to {c.Req("to")}");
            }
        foreach (var target in item.Get("decline").Strings())
            calendar.Respond(target, "decline", note, respond);
    }

    /// <summary>Take emails off the queue and leave them in their folder. They won't be raised again.</summary>
    public static int Keep(Settings settings, IReadOnlyList<string> refs, Action<string> log)
    {
        var gone = Pick(settings, refs).Select(i => i.Req("id")).ToHashSet(StringComparer.Ordinal);
        SaveQueue(settings, Queue(settings).Where(i => !gone.Contains(i.Req("id"))).ToList());
        log($"kept {gone.Count}: off the queue, left in the folder");
        return gone.Count;
    }

    /// <summary>A hold made by hand (1.18.0), recorded beside what the rules deleted, moved and declined, so the next review
    /// sees it: `mail-triage deleted` lists it under "Holds".</summary>
    public static void RecordHeld(Settings settings, string subject, DateTime start, DateTime end, string? eventId)
    {
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var items = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.DeletedFile} has no items list.");
        items.Add((JsonNode)new JsonObject
        {
            ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = PyTime.Iso(start), ["from"] = settings.Email,
            ["subject"] = subject, ["rule"] = "Holds",
            ["did"] = $"held {PyTime.Strftime(start, "%a %d/%m %H:%M")}-{PyTime.Strftime(end, "%H:%M")}, no attendees"
                      + (eventId is null ? "" : $" ({eventId})"),
        });
        StateFiles.Save(settings.DeletedFile, record);
    }

    /// <summary>Record a `respond` or `cancel` (1.22.0) in the deleted file, under its own heading ("Answered by hand",
    /// "Cancelled by hand", "Deleted by hand"), so the review sees every calendar write made from a session.</summary>
    public static void RecordCalendarWrite(Settings settings, string heading, string subject, DateTime start, string from, string did)
    {
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var items = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.DeletedFile} has no items list.");
        items.Add((JsonNode)new JsonObject
        {
            ["when"] = NowMicro(), ["folder"] = "Calendar", ["received"] = PyTime.Iso(start), ["from"] = from,
            ["subject"] = subject, ["rule"] = heading, ["did"] = did,
        });
        StateFiles.Save(settings.DeletedFile, record);
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
