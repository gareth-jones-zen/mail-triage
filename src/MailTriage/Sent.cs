using System.Text.Json.Nodes;
using PyCompat;
using Tomlyn.Model;

namespace MailTriage;

/// <summary>[sent] (1.19.0): what's tidied from Sent Items, by kind. Each kind is "off" (the default: left alone),
/// "immediately", "after_meeting" (once the meeting it's about has ended, plus its days) or "after_sending" (its days after
/// you sent it). The kinds are the meeting messages you sent, told apart by Graph's message type, never by subject:
///   invites        meeting requests and updates (meetingRequest)
///   forwarded_invites  meeting requests you forwarded for someone else's meeting (1.21.0: the message's from, the
///                  organiser, isn't its sender, you); when forwarded_invites isn't set, invites governs them
///   cancellations  cancellations (meetingCancelled)
///   responses      your accept, tentative and decline replies, and Outlook "Follow" notifications (1.20.0: shown as
///                  "follows", told by their message class IPM.Schedule.Meeting.Resp.Tent.Follow, governed by responses)
/// Forwards made by a forward rule with file_sent are moved, never deleted, to that folder.</summary>
public sealed class SentSettings
{
    public static readonly string[] Kinds = ["invites", "cancellations", "responses"];
    /// <summary>Kinds that follow another kind's setting unless set themselves (1.21.0): forwarded_invites follows invites.</summary>
    public static readonly Dictionary<string, string> Inherits = new(StringComparer.Ordinal) { ["forwarded_invites"] = "invites" };
    public static readonly string[] Whens = ["off", "immediately", "after_meeting", "after_sending"];

    public Dictionary<string, (string When, int Days)> ByKind { get; } = new(StringComparer.Ordinal);

    public (string When, int Days) For(string kind) => ByKind.GetValueOrDefault(kind, ("off", 0));

    /// <summary>The setting that governs a kind: follows are responses (GJ, 23 Nov 2026); forwarded_invites are invites
    /// unless [sent] sets forwarded_invites itself (GJ, 23 Nov 2026).</summary>
    public string Governing(string kind)
    {
        if (kind == "follows")
            return "responses";
        if (Inherits.TryGetValue(kind, out var parent) && !ByKind.ContainsKey(kind))
            return parent;
        return kind;
    }

    public static SentSettings Parse(object value, string full)
    {
        if (value is not TomlTable table)
            throw new SettingsException($"sent in {full} should be a [sent] table.");
        var allowed = Kinds.Concat(Inherits.Keys).SelectMany(k => new[] { k, k + "_days" }).ToHashSet(StringComparer.Ordinal);
        foreach (var key in table.Keys)
            if (!allowed.Contains(key))
                throw new SettingsException($"[sent] in {full}: {Py.ReprString(key)} isn't a setting (it takes {string.Join(", ", Kinds.Concat(Inherits.Keys))}, "
                                            + $"each with an optional <kind>_days).");
        var sent = new SentSettings();
        foreach (var kind in Kinds.Concat(Inherits.Keys))
        {
            if (Inherits.TryGetValue(kind, out var parent) && !table.ContainsKey(kind))
            {
                if (table.ContainsKey(kind + "_days"))
                    throw new SettingsException($"[sent] in {full}: {kind}_days needs {kind} itself (without it, {parent} governs them).");
                continue;
            }
            var when = table.TryGetValue(kind, out var w) ? w as string : "off";
            if (when is null || !Whens.Contains(when))
                throw new SettingsException($"[sent] in {full}: {kind} should be one of {string.Join(", ", Whens.Select(x => $"\"{x}\""))}.");
            long days = 0;
            if (table.TryGetValue(kind + "_days", out var d))
            {
                if (d is not long n || n < 0)
                    throw new SettingsException($"[sent] in {full}: {kind}_days should be a whole number of days, 0 or more.");
                if (when is "off" or "immediately")
                    throw new SettingsException($"[sent] in {full}: {kind}_days is only for after_meeting or after_sending.");
                days = n;
            }
            sent.ByKind[kind] = (when, (int)days);
        }
        return sent;
    }
}

/// <summary>One Sent Items message and what [sent] does with it.</summary>
public sealed record SentFinding(string Kind, string Action, JsonObject Message, string Why, DateTime? Due = null, string? To = null)
{
    public string Id => Message.Req("id");
    public string Subject => Message.StrOr("subject");
    public string Sent => Message.StrOr("sentDateTime");
}

/// <summary>Tidying Sent Items (1.19.0). Real correspondence is never touched: only the meeting messages you sent (by
/// Graph's type) and copies of forwards a forward rule made. A message with attachments is never deleted (skipped and
/// reported). Deletes go to Deleted Items (recoverable); moves keep the message as it is. Everything done is listed by
/// `mail-triage deleted`. The first `sent --yes` reads the whole of Sent Items; after that each run reads only what's
/// new (and what was waiting for its time). A scheduled run does nothing until that first `sent --yes`.</summary>
public static class SentItems
{
    public const string FolderName = "Sent Items";
    private const int KeepSeenDays = 2;
    private static readonly TimeSpan Overlap = TimeSpan.FromHours(1);

    public static string Label(string kind) => kind switch
    {
        "invites" => "invites (meeting requests and updates you sent)",
        "forwarded_invites" => "forwarded invitations (meeting requests you forwarded)",
        "cancellations" => "cancellations you sent",
        "responses" => "responses (your accepts, tentatives and declines)",
        "follows" => "follows (meetings you followed; [sent] responses governs them)",
        _ => kind,
    };

    /// <summary>A meeting message's kind from Graph's meetingMessageType, or null (not a meeting message).</summary>
    public static string? KindOf(string? meetingMessageType) => meetingMessageType switch
    {
        "meetingRequest" => "invites",
        "meetingCancelled" => "cancellations",
        "meetingAccepted" or "meetingTenativelyAccepted" or "meetingTentativelyAccepted" or "meetingDeclined" => "responses",
        _ => null,
    };

    /// <summary>A meeting message Graph types without a meetingMessageType [sent] knows, by its MAPI message class (1.20.0):
    /// Outlook's "Follow" (IPM.Schedule.Meeting.Resp.Tent.Follow, "Following: …", sent when you follow a meeting you can't
    /// attend) is "follows"; any other IPM.Schedule.Meeting.Resp.* is "responses". Never by subject.</summary>
    public static string? KindOfClass(string? messageClass)
    {
        if (messageClass is null || !messageClass.StartsWith("IPM.Schedule.Meeting.Resp.", StringComparison.OrdinalIgnoreCase))
            return null;
        return messageClass.EndsWith(".Follow", StringComparison.OrdinalIgnoreCase) ? "follows" : "responses";
    }

    /// <summary>The [sent] setting that governs a kind: follows are responses (GJ, 23 Nov 2026).</summary>
    public static string SettingFor(string kind) => kind == "follows" ? "responses" : kind;

    /// <summary>A meeting request you forwarded for someone else's meeting (1.21.0): in Sent Items its from is the
    /// organiser and its sender is you (read live, 23 Nov 2026: "FW: DD Re-write", from Fahad, sender GJ; your own
    /// invites have from and sender both you, and the forward-instance flag isn't set). Never by the "FW:" subject.</summary>
    public static bool IsForwarded(JsonObject message)
    {
        var from = message.Get("from").Get("emailAddress").Str("address");
        var sender = message.Get("sender").Get("emailAddress").Str("address");
        return from is not null && sender is not null && !from.Equals(sender, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEventMessage(JsonObject m) =>
        m.Str("@odata.type") is { } t && t.StartsWith("#microsoft.graph.eventMessage", StringComparison.Ordinal);

    /// <summary>The forward rule whose copy this is: sent to exactly the rule's forward_to, a forward ("FW:"), opening with
    /// the rule's covering comment. Only rules with file_sent count.</summary>
    public static Rule? ForwardRule(Settings settings, JsonObject m)
    {
        var to = m.Get("toRecipients").Objects().Select(r => r.Get("emailAddress").StrOr("address").ToLowerInvariant()).Order().ToList();
        var subject = m.StrOr("subject");
        var preview = PyStr.Strip(m.StrOr("bodyPreview"));
        if (!PyRegex.IsMatch(@"^\s*(fw|fwd)\s*:", subject, true))
            return null;
        return settings.Rules.FirstOrDefault(r => r is { Action: "forward", FileSent: not null, ForwardTo: not null, Comment: not null }
            && r.ForwardTo.Select(a => a.ToLowerInvariant()).Order().SequenceEqual(to)
            && preview.StartsWith(PyStr.Strip(r.Comment), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A Graph dateTimeTimeZone (or an ISO time with Z) as local time; null if it can't be read.</summary>
    internal static DateTime? Local(JsonNode? when)
    {
        try
        {
            if (when is JsonObject o && o.Str("dateTime") is { } dt)
            {
                var t = DateTime.Parse(dt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
                var zoneName = o.Str("timeZone") ?? "UTC";
                var zone = zoneName.Equals("UTC", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(zoneName);
                return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone, Clock.Provider.LocalTimeZone);
            }
            if (when is JsonValue v && v.TryGetValue<string>(out var s))
                return TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture), Clock.Provider.LocalTimeZone).DateTime;
        }
        catch (Exception error) when (error is FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
        }
        return null;
    }

    /// <summary>What [sent] would do with each message read, and with what was waiting and is now due. Reads only.</summary>
    public static (List<SentFinding> Findings, bool FirstRun, int Read) Plan(Settings settings, Mailbox mailbox, JsonObject state, DateTime now)
    {
        var sent = settings.Sent!;
        var first = !state.Has("until");
        string? since = null;
        if (!first && Local(JsonValue.Create(state.Req("until"))) is { } until)
            since = PyTime.Strftime(TimeZoneInfo.ConvertTimeToUtc(until - Overlap, Clock.Provider.LocalTimeZone), "%Y-%m-%dT%H:%M:%SZ");
        var seen = state.Get("seen") as JsonObject ?? [];
        var waiting = state.Get("waiting") as JsonObject ?? [];
        var findings = new List<SentFinding>();
        var messages = mailbox.SentMessages(since);
        foreach (var m in messages)
        {
            var id = m.Req("id");
            if (seen.ContainsKey(id) || waiting.ContainsKey(id))
                continue;
            if (ForwardRule(settings, m) is { } rule)
            {
                findings.Add(new SentFinding("forwards", "move", m, $"a copy of a forward by \"{rule.Name}\"", To: rule.FileSent));
                continue;
            }
            if (!IsEventMessage(m))
            {
                findings.Add(new SentFinding("other", "leave", m, "not a meeting message or a rule's forward"));
                continue;
            }
            var details = mailbox.Message(id);
            var kind = KindOf(details.Str("meetingMessageType"));
            if (kind is null && KindOfClass(mailbox.MessageClass(id)) is { } byClass)
                kind = byClass;
            if (kind == "invites" && IsForwarded(details))
                kind = "forwarded_invites";
            if (kind is null)
            {
                findings.Add(new SentFinding("other", "leave", m, "a meeting message of no kind [sent] handles"));
                continue;
            }
            var governing = sent.Governing(kind);
            var (when, days) = sent.For(governing);
            if (when == "off")
            {
                findings.Add(new SentFinding(kind, "leave", m, $"{governing} is off in [sent]"));
                continue;
            }
            if (m.Truthy("hasAttachments"))
            {
                findings.Add(new SentFinding(kind, "skip", m, "it has attachments: never deleted"));
                continue;
            }
            DateTime? due = when switch
            {
                "immediately" => now,
                "after_meeting" => Local(details.Get("endDateTime")) is { } end ? end.AddDays(days) : null,
                _ => Local(JsonValue.Create(m.StrOr("sentDateTime"))) is { } at ? at.AddDays(days) : null,
            };
            if (due is null)
                findings.Add(new SentFinding(kind, "skip", m, when == "after_meeting" ? "the meeting's time isn't known" : "when it was sent isn't known"));
            else if (due <= now)
                findings.Add(new SentFinding(kind, "delete", m, when == "immediately" ? "immediately" : $"due {PyTime.Strftime(due.Value, "%d/%m %H:%M")}", due));
            else
                findings.Add(new SentFinding(kind, "wait", m, $"waits until {PyTime.Strftime(due.Value, "%d/%m %H:%M")}", due));
        }
        foreach (var (id, node) in waiting)
        {
            if (node is not JsonObject w || Local(JsonValue.Create(w.Req("due"))) is not { } due || due > now)
                continue;
            var stub = new JsonObject { ["id"] = id, ["subject"] = w.GetCopy("subject", ""), ["sentDateTime"] = w.GetCopy("sent", "") };
            findings.Add(new SentFinding(w.Req("kind"), "delete", stub, $"due {PyTime.Strftime(due, "%d/%m %H:%M")}", due));
        }
        return (findings, first, messages.Count);
    }

    /// <summary>The preview: per kind, how many, the oldest and newest, and what would happen; the skipped listed.</summary>
    public static List<string> Describe(List<SentFinding> findings, bool first, int read, bool done, SentSettings? sent = null)
    {
        static string Day(string iso) => iso.Length >= 10 ? $"{iso[8..10]}/{iso[5..7]}/{iso[..4]}" : "?";
        var lines = new List<string>
        {
            $"Sent Items: {read} message(s) read" + (first ? " (all of Sent Items: the first run)." : " (new since the last run)."),
        };
        var order = new[] { "invites", "forwarded_invites", "cancellations", "responses", "follows", "forwards", "other" };
        foreach (var kind in order)
        {
            var group = findings.Where(f => f.Kind == kind).ToList();
            if (group.Count == 0)
                continue;
            var dates = group.Select(f => f.Sent).Where(s => s.Length > 0).Order(StringComparer.Ordinal).ToList();
            var span = dates.Count > 0 ? $", {Day(dates[0])} to {Day(dates[^1])}" : "";
            var parts = new List<string>();
            int Count(string action) => group.Count(f => f.Action == action);
            if (Count("delete") > 0)
                parts.Add($"{(done ? "deleted" : "would delete")} {Count("delete")}");
            if (Count("move") > 0)
                parts.Add($"{(done ? "moved" : "would move")} {Count("move")} to " + string.Join(", ", group.Where(f => f.To is not null).Select(f => f.To).Distinct()));
            if (Count("wait") > 0)
                parts.Add($"{Count("wait")} waiting for their time (first {PyTime.Strftime(group.Where(f => f.Action == "wait").Min(f => f.Due!.Value), "%d/%m")})");
            if (Count("skip") > 0)
                parts.Add($"{Count("skip")} skipped");
            if (Count("leave") > 0)
                parts.Add(kind == "other" ? $"{Count("leave")} left alone (real correspondence and anything else)" : $"{Count("leave")} left alone ({group.First(f => f.Action == "leave").Why})");
            var label = kind switch { "forwards" => "forwards made by a rule", "other" => "everything else", _ => Label(kind) };
            if (sent is not null && kind != "follows" && sent.Governing(kind) != kind)
                label = label.Replace(")", $"; [sent] {sent.Governing(kind)} governs them)");
            lines.Add($"  {label}: {group.Count}{span}: {string.Join("; ", parts)}");
        }
        foreach (var f in findings.Where(f => f.Action == "skip").Take(20))
            lines.Add($"    skipped: {Day(f.Sent)}  {PyStr.Head(f.Subject, 80)}  ({f.Why})");
        return lines;
    }

    /// <summary>Do it: delete what's due, move rule forwards' copies, remember what waits, and record each in the deleted
    /// list. State is saved only when the writes really reach Graph (a journal or refused run changes nothing).</summary>
    public static (int Deleted, int Moved, int Waiting, int Skipped) Apply(Settings settings, Mailbox mailbox, JsonObject state,
        List<SentFinding> findings, bool first, Action<string> log, string by = ActivityJournal.By.Auto)
    {
        var live = mailbox.Writer is GraphWrites { Sends: true };
        var seen = state.Get("seen") as JsonObject ?? [];
        var waiting = state.Get("waiting") as JsonObject ?? [];
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var items = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.DeletedFile} has no items list.");
        int deleted = 0, moved = 0, waits = 0, skipped = 0;
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonObject Entry(SentFinding f, string rule, string did) => new()
        {
            ["when"] = PyTime.Iso(Clock.Now, "microseconds"), ["folder"] = FolderName, ["received"] = f.Sent, ["from"] = settings.Email,
            ["subject"] = f.Subject, ["rule"] = rule, ["did"] = did,
        };
        foreach (var f in findings)
        {
            switch (f.Action)
            {
                case "delete":
                    try
                    {
                        mailbox.Delete(f.Id);
                    }
                    catch (M365Graph.GraphRequestException error) when ((int)error.Status == 404)
                    {
                        waiting.Remove(f.Id);   // already gone from Sent Items
                        continue;
                    }
                    deleted++;
                    waiting.Remove(f.Id);
                    items.Add((JsonNode)Entry(f, $"Sent Items: {f.Kind}", "deleted from Sent Items"));
                    if (live)   // 1.33.0
                        Activity.Write(settings, log, "delete", by, f.Subject, $"Sent Items: {f.Kind}", "deleted from Sent Items (restore it in Outlook)", f.Id, null);
                    log($"sent: deleted  [{f.Kind}] {f.Subject}");
                    break;
                case "move":
                    if (!folders.TryGetValue(f.To!, out var folderId))
                        folders[f.To!] = folderId = mailbox.FolderPath(f.To!);
                    mailbox.File(f.Id, folderId);
                    moved++;
                    items.Add((JsonNode)Entry(f, $"Sent Items: forwards", $"filed to {f.To} (the Sent Items copy)"));
                    if (live)   // 1.33.0
                        Activity.Write(settings, log, "file", by, f.Subject, "Sent Items: forwards", $"filed to {f.To} (the Sent Items copy)", f.Id, null);
                    log($"sent: filed  {f.Subject} -> {f.To}");
                    break;
                case "wait":
                    waits++;
                    waiting[f.Id] = new JsonObject
                    {
                        ["kind"] = f.Kind, ["due"] = PyTime.Iso(f.Due!.Value, "seconds"), ["subject"] = f.Subject, ["sent"] = f.Sent,
                    };
                    break;
                case "skip":
                    skipped++;
                    break;
            }
            if (f.Sent.Length > 0 && f.Action != "wait")
                seen[f.Id] = f.Sent;
        }
        if (!live)
            return (deleted, moved, waits, skipped);
        var newest = findings.Select(f => f.Sent).Where(s => s.Length > 0).Append(state.Str("until") ?? "").Max(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(newest))
            state["until"] = newest;
        else if (first)
            state["until"] = PyTime.Strftime(TimeZoneInfo.ConvertTimeToUtc(Clock.Now, Clock.Provider.LocalTimeZone), "%Y-%m-%dT%H:%M:%SZ");
        // Seen messages matter only where the next read overlaps this one (the hour before "until"): keep those sent within
        // a couple of days of it, and forget the rest, so the file stays small however big Sent Items is.
        var keepFrom = Local(JsonValue.Create(state.Req("until"))) is { } u ? u.AddDays(-KeepSeenDays) : Clock.Now.AddDays(-KeepSeenDays);
        var keep = new JsonObject();
        foreach (var (id, when) in seen)
            if (when is not null && Local(JsonValue.Create(Py.Str(when))) is { } t && t >= keepFrom)
                keep[id] = when.DeepClone();
        state["seen"] = keep;
        state["waiting"] = waiting.DeepClone();
        StateFiles.Save(settings.SentFile, state);
        StateFiles.Save(settings.DeletedFile, record);
        if (deleted + moved > 0)
            log($"sent: {deleted} deleted, {moved} filed" + (waits > 0 ? $", {waits} waiting for their time" : "") + (skipped > 0 ? $", {skipped} skipped (attachments)" : ""));
        return (deleted, moved, waits, skipped);
    }

    /// <summary>The scheduled part of a run: nothing until the first `sent --yes` has read the whole folder; then what's new.</summary>
    public static void RunScheduled(Settings settings, Mailbox mailbox, bool dryRun, Action<string> log)
    {
        var state = StateFiles.LoadObject(settings.SentFile, () => []);
        if (!state.Has("until"))
            return;
        var (findings, first, _) = Plan(settings, mailbox, state, Clock.Now);
        if (dryRun)
        {
            foreach (var f in findings.Where(f => f.Action is "delete" or "move"))
                log($"sent: would {(f.Action == "delete" ? "delete" : $"file to {f.To}")}  [{f.Kind}] {f.Subject}");
            return;
        }
        Apply(settings, mailbox, state, findings, first, log);
    }
}
