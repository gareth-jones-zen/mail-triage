// The .NET side of the offline parity checks (tools/parity/parity.py is the Python side). Never shipped; never
// calls Graph. Commands:
//   scenarios <dir>          results for <dir>/scenarios.json, one JSON line each, to <dir>/dotnet.jsonl
//   toml <file>              the TOML as Tomlyn reads it, as JSON
//   settings <file>          Settings.Dump()
//   regex <corpus> <out>     search spans for each (pattern, flags, text)
//   lockkey <path>           the run and watch lock names
//   state <in> <out>         read a state file and write it back
//   tz                       London time under invariant globalisation (run it as the AOT build)
using System.Text;
using System.Text.Json.Nodes;
using MailTriage;
using MailTriage.Interim;
using Tomlyn;
using Tomlyn.Model;
using WinBackground;

Console.OutputEncoding = new UTF8Encoding(false);
var command = args.Length > 0 ? args[0] : "";
switch (command)
{
    case "scenarios":
        Scenarios.Run(args[1]);
        return 0;
    case "toml":
        Console.WriteLine(PyJson.Dump(TomlJson(TomlSerializer.Deserialize(File.ReadAllText(args[1]), ParityToml.Default.TomlTable)), null, false));
        return 0;
    case "settings":
        Console.WriteLine(PyJson.Dump(Settings.Load(args[1]).Dump(), null, false));
        return 0;
    case "regex":
        RegexCorpus(args[1], args[2]);
        return 0;
    case "lockkey":
        var path = PyPath.Resolve(args[1]);
        Console.WriteLine($"mail-triage-run-{Locks.Key(path)}\nmail-triage-watch-{Locks.Key(path)}");
        return 0;
    case "state":
        StateFiles.Save(args[2], StateFiles.Load(args[1], () => null));
        return 0;
    case "tz":
        var zone = PyTime.Zone("GMT Standard Time");
        foreach (var t in new[] { "2026-11-12T12:00", "2026-07-01T12:00", "2026-10-25T01:30", "2027-03-28T01:30", "2027-03-28T02:30" })
            Console.WriteLine($"{t} {PyTime.Aware(PyTime.FromIso(t), zone):yyyy-MM-ddTHH:mm:sszzz}");
        Console.WriteLine(PyTime.Local(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), zone).ToString("s"));
        Console.WriteLine(PyTime.Local(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), zone).ToString("s"));
        return 0;
    default:
        Console.Error.WriteLine("usage: Parity scenarios|toml|settings|regex|lockkey|state|tz ...");
        return 2;
}

static JsonNode? TomlJson(object? value) => value switch
{
    null => null,
    TomlTable t => new JsonObject(t.Select(p => KeyValuePair.Create(p.Key, TomlJson(p.Value)))),
    TomlTableArray a => new JsonArray(a.Select(x => TomlJson(x)).ToArray()),
    TomlArray a => new JsonArray(a.Select(TomlJson).ToArray()),
    string s => JsonValue.Create(s),
    bool b => JsonValue.Create(b),
    long l => PyJson.Int(l),
    double d => PyJson.Float(d),
    TomlDateTime d when d.Kind == TomlDateTimeKind.LocalDate => JsonValue.Create(d.DateTime.ToString("yyyy-MM-dd")),
    _ => JsonValue.Create(value.ToString()),
};

static void RegexCorpus(string corpusPath, string outPath)
{
    var corpus = (JsonArray)JsonNode.Parse(File.ReadAllText(corpusPath))!;
    var results = new JsonArray();
    foreach (var entry in corpus)
    {
        var pattern = (string)entry![0]!;
        var flags = (string)entry[1]!;
        var text = (string)entry[2]!;
        try
        {
            var m = PyRegex.Search(pattern, text, flags == "i");
            results.Add(m is null ? null : (JsonNode)new JsonArray(m.Index, m.Index + m.Length));
        }
        catch (PyRegexUnsupportedException)
        {
            results.Add((JsonNode)"error");
        }
    }
    File.WriteAllText(outPath, results.ToJsonString());
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class ParityToml : Tomlyn.Serialization.TomlSerializerContext;

/// <summary>The Python harness's FakeCalendar: fixed events, every change logged, free/busy from the scenario.</summary>
internal sealed class LoggingCalendar(List<JsonObject> items, JsonObject? busy = null) : ICalendar
{
    public readonly JsonArray Log = [];

    public List<JsonObject> Events(DateOnly start, long days) => items;

    public void Respond(string eventId, string action, string note = "", bool send = true) => Log.Add((JsonNode)new JsonArray("respond", eventId, action, note, send));

    public void Move(string eventId, DateTime start, DateTime end) => Log.Add((JsonNode)new JsonArray("move", eventId, PyTime.Iso(start), PyTime.Iso(end)));

    public void Cancel(string eventId, string comment = "") => Log.Add((JsonNode)new JsonArray("cancel", eventId, comment));

    public List<(DateTime Start, DateTime End, string? Status)>? Schedule(string address, DateTime start, DateTime end)
    {
        Log.Add((JsonNode)new JsonArray("schedule", address, PyTime.Iso(start), PyTime.Iso(end)));
        if (busy?[address] is not JsonArray entries)
            return null;   // couldn't be read
        return entries.Select(e => (PyTime.FromIso((string)e![0]!), PyTime.FromIso((string)e[1]!), (string?)e[2]))
            .Where(e => e.Item1 < end && e.Item2 > start).ToList();
    }

    public JsonObject Event(string eventId) => throw new NotSupportedException();

    public List<JsonObject> Between(DateTimeOffset start, DateTimeOffset end)
    {
        var s = PyTime.Local(start, Zone());
        var e = PyTime.Local(end, Zone());
        return items.Where(x => InviteTriage.Times(x).Start < e && InviteTriage.Times(x).End > s).ToList();
    }

    public TimeZoneInfo Zone() => PyTime.Zone("GMT Standard Time");
}

/// <summary>The proposal's mailbox: the scenario's message, an ordinary one, and the full proposal.</summary>
internal sealed class ProposalBox(JsonObject message, JsonObject full) : IGraph, IMailWriter
{
    public readonly JsonArray Deleted = [];

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (url.StartsWith("/me/mailFolders?", StringComparison.Ordinal))
            return [new JsonObject { ["id"] = "Inbox", ["displayName"] = "Inbox" }];
        return [(JsonObject)message.DeepClone(), new JsonObject { ["@odata.type"] = "#microsoft.graph.message", ["id"] = "m2" }];
    }

    public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null) => (JsonObject)full.DeepClone();

    public void Delete(string messageId) => Deleted.Add((JsonNode)messageId);

    public void File(string messageId, string folderId) => throw new NotSupportedException();
}

internal static class Scenarios
{
    private static JsonArray Strs(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());

    private static JsonArray Objs(IEnumerable<JsonObject> items) => new(items.Select(i => (JsonNode)i.DeepClone()).ToArray());

    private static List<JsonObject> Events(JsonNode? node) => ((JsonArray)node!).Select(e => (JsonObject)e!.DeepClone()).ToList();

    private static JsonObject Raised(JsonNode? keys)
    {
        var raised = new JsonObject();
        foreach (var k in (JsonArray?)keys ?? [])
            raised[(string)k!] = "x";
        return raised;
    }

    public static void Run(string dir)
    {
        var scenarios = (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "scenarios.json")))!;
        var lines = new List<string>();
        var cache = new Dictionary<string, Settings>();
        foreach (var node in scenarios)
        {
            var s = (JsonObject)node!;
            var path = (string)s["settings"]!;
            if (!cache.TryGetValue(path, out var settings))
                cache[path] = settings = Settings.Load(path);
            lines.Add(Result(s, settings));
        }
        File.WriteAllText(Path.Combine(dir, "dotnet.jsonl"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"{lines.Count} .NET results");
    }

    private static string Result(JsonObject s, Settings settings)
    {
        var now = PyTime.FromIso((string)s["now"]!);
        var log = new List<string>();
        try
        {
            using var _ = Clock.Use(new FixedTime(now));
            var dryRun = s["dry_run"] is JsonValue d && d.GetValue<bool>();
            switch ((string)s["kind"]!)
            {
                case "invites":
                {
                    var accepted = new JsonObject();
                    var acted = new List<JsonObject>();
                    InviteTriage.Triage(settings, new LoggingCalendar(Events(s["before"])), [], _ => { }, now, accepted);
                    var calendar = new LoggingCalendar(Events(s["events"]));
                    var raised = Raised(s["raised"]);
                    var (items, seen, skipped) = InviteTriage.Triage(settings, calendar, raised, log.Add, now, accepted, acted, dryRun);
                    return PyJson.Dump(new JsonObject
                    {
                        ["items"] = Objs(items), ["seen"] = Strs(seen.Order(PyText.Order)), ["skipped"] = skipped, ["raised"] = raised.DeepClone(),
                        ["acted"] = Objs(acted), ["calendar"] = calendar.Log.DeepClone(), ["accepted"] = accepted.DeepClone(), ["log"] = Strs(log),
                    }, null, false);
                }
                case "one_to_ones":
                {
                    var busy = (JsonObject?)s["busy"];
                    var plan = OneToOnePlanner.Plan(settings, new LoggingCalendar(Events(s["events"]), busy), now).Select(OneToOnePlanner.Describe).ToList();
                    var calendar = new LoggingCalendar(Events(s["events"]), busy);
                    var raised = Raised(s["raised"]);
                    var (items, acted, seen) = OneToOnePlanner.Run(settings, calendar, raised, log.Add, dryRun, now);
                    return PyJson.Dump(new JsonObject
                    {
                        ["plan"] = Strs(plan), ["items"] = Objs(items), ["acted"] = Objs(acted), ["seen"] = Strs(seen.Order(PyText.Order)),
                        ["raised"] = raised.DeepClone(), ["calendar"] = calendar.Log.DeepClone(), ["log"] = Strs(log),
                    }, null, false);
                }
                case "proposal":
                {
                    var box = new ProposalBox((JsonObject)s["message"]!, (JsonObject)s["full"]!);
                    var calendar = new LoggingCalendar(Events(s["events"]));
                    var raised = new JsonObject();
                    var (items, acted, seen) = Proposals.Run(settings, new Mailbox(box, box), calendar, raised, log.Add, dryRun, now);
                    return PyJson.Dump(new JsonObject
                    {
                        ["items"] = Objs(items), ["acted"] = Objs(acted), ["seen"] = Strs(seen.Order(PyText.Order)), ["raised"] = raised.DeepClone(),
                        ["calendar"] = calendar.Log.DeepClone(), ["deleted"] = box.Deleted.DeepClone(), ["log"] = Strs(log),
                    }, null, false);
                }
                default:
                {
                    var slot = OneToOnePlanner.Nearest(settings, PyTime.FromIso((string)s["start"]!), TimeSpan.FromMinutes((int)s["length"]!),
                        ((JsonArray)s["days"]!).Select(x => DateOnly.Parse((string)x!)).ToList(),
                        ((JsonArray)s["busy"]!).Select(b => (PyTime.FromIso((string)b![0]!), PyTime.FromIso((string)b[1]!))).ToList(), now);
                    return PyJson.Dump(new JsonObject { ["slot"] = slot is { } x ? Strs([PyTime.Iso(x.Start), PyTime.Iso(x.End)]) : null }, null, false);
                }
            }
        }
        catch (Exception error)
        {
            return PyJson.Dump(new JsonObject { ["error"] = error.GetType().Name }, null, false);
        }
    }
}
