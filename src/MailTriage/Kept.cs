using System.Globalization;
using System.Text.Json.Nodes;
using ActivityJournal;
using PyCompat;

namespace MailTriage;

/// <summary>What `keep` took off the queue (1.33.0), so `requeue` can undo it: each item exactly as it was in the queue,
/// with when it was kept and a number of its own, in kept_file:
/// <code>{"next": 4, "items": [{"n": 3, "kept": "2026-11-25T09:14:03", "item": {"folder": "Inbox", "id": "...", ...}}]}</code>
/// Numbers never change while a record is kept and aren't reused. A record goes when it's requeued, or after KeepDays
/// (60 days). Local state only: neither `keep` nor `requeue` touches the mailbox.</summary>
public static class Kept
{
    public const int KeepDays = 60;

    public sealed record Record(long N, string Kept, JsonObject Item);

    private static JsonObject Load(Settings settings) =>
        StateFiles.LoadObject(settings.KeptFile, () => new JsonObject { ["next"] = 1, ["items"] = new JsonArray() });

    private static long Next(JsonObject record) => record["next"] is JsonValue v && v.TryGetValue<long>(out var n) && n > 0 ? n : 1;

    /// <summary>Every kept item, oldest first.</summary>
    public static List<Record> All(Settings settings) => All(Load(settings));

    private static List<Record> All(JsonObject record) =>
        record.Get("items").Objects().Where(o => o["item"] is JsonObject)
            .Select(o => new Record(o["n"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0, o.StrOr("kept"), (JsonObject)o["item"]!.DeepClone()))
            .ToList();

    /// <summary>Records items `keep` has just taken off the queue, each as it was (its queue number left out).</summary>
    public static void Add(Settings settings, IEnumerable<JsonObject> items)
    {
        var record = Load(settings);
        var list = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.KeptFile} has no items list.");
        var next = Next(record);
        var now = PyTime.Iso(Clock.Now, "seconds");
        foreach (var item in items)
        {
            var copy = (JsonObject)item.DeepClone();
            copy.Remove("n");
            list.Add((JsonNode)new JsonObject { ["n"] = next++, ["kept"] = now, ["item"] = copy });
        }
        record["next"] = next;
        Prune(list);
        StateFiles.Save(settings.KeptFile, record);
    }

    private static void Prune(JsonArray list)
    {
        var cutoff = PyTime.Iso(Clock.Now - TimeSpan.FromDays(KeepDays), "seconds");
        foreach (var old in list.Where(o => PyStr.CompareCodePoints(o.StrOr("kept"), cutoff) < 0).ToList())
            list.Remove(old);
    }

    /// <summary>A kept record by its number in `kept`, or its item's id (the Graph id `list` and `keep` use) or key
    /// (internet message ID); the latest if the same item was kept more than once. Throws LookupException.</summary>
    public static Record Find(Settings settings, string reference)
    {
        var all = All(settings);
        return all.LastOrDefault(r => r.N.ToString(CultureInfo.InvariantCulture) == reference)
               ?? all.LastOrDefault(r => r.Item.Str("id") == reference)
               ?? all.LastOrDefault(r => r.Item.Str("key") == reference)
               ?? throw new LookupException($"Nothing kept is {Py.ReprString(reference)}. Run `mail-triage kept`.");
    }

    /// <summary>`requeue`: puts kept items back on the queue as they were (every field, the recommendation included), at the
    /// end, so `list` shows them again with the next numbers; and off the kept record. Refuses (nothing changed) anything
    /// unknown, or already in the queue. Call under the run lock. Like any queued email, one no longer in its folder (moved
    /// or deleted in Outlook since) drops off the queue at the next run.</summary>
    public static List<JsonObject> Requeue(Settings settings, IReadOnlyList<string> refs, Action<string> log)
    {
        var chosen = new List<Record>();
        foreach (var r in refs)
        {
            var found = Find(settings, r);
            if (!chosen.Any(c => c.N == found.N))
                chosen.Add(found);
        }
        var queue = Triage.Queue(settings);
        foreach (var c in chosen)
            if (queue.FirstOrDefault(i => i.Str("id") == c.Item.Str("id")) is { } there)
                throw new LookupException($"{c.Item.Fmt("subject")} is in the queue already, as {there.Fmt("n", "?")}: nothing requeued.");
        var back = chosen.Select(c => (JsonObject)c.Item.DeepClone()).ToList();
        Triage.SaveQueue(settings, [.. queue, .. back]);
        var record = Load(settings);
        var list = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.KeptFile} has no items list.");
        var gone = chosen.Select(c => c.N).ToHashSet();
        foreach (var o in list.Where(o => o?["n"] is JsonValue v && v.TryGetValue<long>(out var n) && gone.Contains(n)).ToList())
            list.Remove(o);
        StateFiles.Save(settings.KeptFile, record);
        var numbered = Triage.Queue(settings);
        foreach (var c in chosen)
        {
            var at = numbered.LastOrDefault(i => i.Str("id") == c.Item.Str("id"));
            log($"requeued as {at.Fmt("n", "?")}  {c.Item.Fmt("from")}: {c.Item.Fmt("subject")}");
            Activity.Write(settings, log, "requeue", By.Hand, c.Item.Str("subject"), "by hand", $"kept {c.Kept.Replace('T', ' ')}, back on the queue",
                c.Item.Str("key") ?? c.Item.Str("id"), c.Item.Str("id") is { } id ? $"mail-triage keep {id}" : null);
        }
        return back;
    }

    /// <summary>`kept --json`: each record as the UI needs it.</summary>
    public static JsonObject Json(Record r) => new()
    {
        ["n"] = r.N, ["kept"] = r.Kept, ["id"] = r.Item.GetCopy("id"), ["key"] = r.Item.GetCopy("key"), ["type"] = r.Item.GetCopy("type"),
        ["from"] = r.Item.GetCopy("from"), ["subject"] = r.Item.GetCopy("subject"), ["received"] = r.Item.GetCopy("received"),
        ["rule"] = r.Item.GetCopy("rule"), ["recommendation"] = r.Item.GetCopy("recommendation"),
    };
}
