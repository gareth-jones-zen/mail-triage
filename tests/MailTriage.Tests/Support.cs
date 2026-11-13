using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>A temporary folder with a settings file in it, removed afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = PyPath.Resolve(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mail-triage-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string name, string text)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public Settings Settings(string text, string name = "config.toml") => MailTriage.Settings.Load(Write(name, text));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The Python tests' FakeGraph: one folder tree (Inbox > Triage), the messages in it, and a digest body.</summary>
public sealed class FakeGraph(List<JsonObject> messages, string? body = null) : IGraph
{
    public const string Digest = "<table><tr><td>Weekly report flow</td><td>3</td></tr><tr><td>Name</td><td>Failures</td></tr></table>";

    public List<JsonObject> Messages { get; set; } = messages;
    public string Body { get; set; } = body ?? Digest;
    public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (url.StartsWith("/me/mailFolders?", StringComparison.Ordinal))
            return [new JsonObject { ["id"] = "top", ["displayName"] = "Inbox", ["childFolderCount"] = 1 }];
        if (url.Contains("/childFolders"))
            return [new JsonObject { ["id"] = "f1", ["displayName"] = "Triage", ["childFolderCount"] = 0 }];
        return Messages.Select(m => (JsonObject)m.DeepClone()).ToList();
    }

    public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        Calls.Add((method, url, body?.DeepClone()));
        if (url.EndsWith("/move", StringComparison.Ordinal))
        {
            var moved = url.Split('/')[3];
            Messages = Messages.Where(m => m.Str("id") != moved).ToList();
        }
        return url.Contains("$select=body") ? new JsonObject { ["body"] = new JsonObject { ["content"] = Body } } : [];
    }

    public bool Has(string method, string url, JsonNode body) =>
        Calls.Any(c => c.Method == method && c.Url == url && JsonNode.DeepEquals(c.Body, body));
}

/// <summary>The Python tests' FakeCalendar: fixed events, and every change recorded rather than made.</summary>
public class FakeCalendar(List<JsonObject> items, Dictionary<string, List<(DateTime, DateTime, string)>>? busy = null) : ICalendar
{
    public List<JsonObject> Items { get; set; } = items;
    public Dictionary<string, List<(DateTime Start, DateTime End, string Status)>> Busy { get; } = busy ?? [];
    public List<(string Id, string Action, string Note, bool Send)> Answers { get; } = [];
    public List<(string Id, DateTime Start, DateTime End)> Moves { get; } = [];
    public List<(string Id, string Comment)> Cancels { get; } = [];
    public List<string> Asked { get; } = [];

    public List<JsonObject> Events(DateOnly start, long days) => Items;

    public void Respond(string eventId, string action, string note = "", bool send = true) => Answers.Add((eventId, action, note, send));

    public void Move(string eventId, DateTime start, DateTime end) => Moves.Add((eventId, start, end));

    public void Cancel(string eventId, string comment = "") => Cancels.Add((eventId, comment));

    public List<(DateTime Start, DateTime End, string? Status)>? Schedule(string address, DateTime start, DateTime end)
    {
        Asked.Add(address);
        if (address == "unreadable@example.com")
            return null;
        return Busy.GetValueOrDefault(address, []).Where(b => b.Start < end && b.End > start).Select(b => (b.Start, b.End, (string?)b.Status)).ToList();
    }

    public JsonObject Event(string eventId) => Items.First(e => e.Str("id") == eventId);

    /// <summary>GMT in November: UTC is local.</summary>
    public virtual List<JsonObject> Between(DateTimeOffset start, DateTimeOffset end)
    {
        var s = start.UtcDateTime;
        var e = end.UtcDateTime;
        return Items.Where(x => InviteTriage.Times(x).Start < e && InviteTriage.Times(x).End > s).ToList();
    }

    public virtual TimeZoneInfo Zone() => TimeZoneInfo.Utc;
}

public static class Make
{
    public const string Me = "alex@example.com";

    public const string InviteSettings = """
        email = "alex@example.com"
        queue_file = "queue.json"
        state_file = "raised.json"
        deleted_file = "deleted.json"
        log_file = "log.txt"

        [invites]
        days_ahead = 30
        commute_blocks = ['^Travel']
        protected = ['^Weekly 1-2-1 - Alex$']

        [[movable_block]]
        subject = '^Weekly update for Pat'
        within = "day"

        [[movable_block]]
        subject = '^Prep for leadership'
        before = '^Leadership weekly'

        [[invite_rule]]
        name = "Colleagues' leave and away notices"
        subject = 'holiday|annual leave'
        all_day = true
        show_as = ["free", "oof"]
        action = "skip"

        """;

    public static readonly (string Name, string Address, string Type)[] MeOnly = [("Alex Smith", Me, "required")];

    /// <summary>The Python tests' ev(): an event in the shape Graph returns.</summary>
    public static JsonObject Ev(string id, string subject, string day, string start, string end, string response = "notResponded",
        string organiser = "Sam Jones", string orgAddr = "sam@example.com", (string Name, string Address, string Type)[]? attendees = null,
        string? endDay = null, bool isAllDay = false, string showAs = "busy", bool responseRequested = true, string type = "singleInstance",
        string? seriesMasterId = null, string[]? categories = null, string? sensitivity = null)
    {
        var people = new JsonArray();
        foreach (var (n, a, t) in attendees ?? MeOnly)
            people.Add((JsonNode)new JsonObject { ["type"] = t, ["emailAddress"] = new JsonObject { ["name"] = n, ["address"] = a } });
        var e = new JsonObject
        {
            ["id"] = id, ["subject"] = subject, ["isAllDay"] = isAllDay, ["showAs"] = showAs, ["isCancelled"] = false,
            ["isOrganizer"] = organiser == "Alex Smith",
            ["responseStatus"] = new JsonObject { ["response"] = organiser == "Alex Smith" ? "organizer" : response },
            ["responseRequested"] = responseRequested,
            ["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = organiser, ["address"] = orgAddr } },
            ["attendees"] = people,
            ["start"] = new JsonObject { ["dateTime"] = $"{day}T{start}:00.0000000" },
            ["end"] = new JsonObject { ["dateTime"] = $"{endDay ?? day}T{end}:00.0000000" },
            ["type"] = type, ["seriesMasterId"] = seriesMasterId,
        };
        if (categories is not null)
            e["categories"] = J.Array(categories);
        if (sensitivity is not null)
            e["sensitivity"] = sensitivity;
        return e;
    }

    /// <summary>One of Alex's own entries (SELF in the Python tests).</summary>
    public static JsonObject Self(string id, string subject, string day, string start, string end, string? endDay = null, bool isAllDay = false,
        string showAs = "busy", string[]? categories = null, string? sensitivity = null) =>
        Ev(id, subject, day, start, end, organiser: "Alex Smith", orgAddr: Me, attendees: [], endDay: endDay, isAllDay: isAllDay, showAs: showAs,
            categories: categories, sensitivity: sensitivity);

    public static DateTime At(string day, string time) => PyTime.FromIso($"{day}T{time}");

    public static Action<string> Quiet => _ => { };
}
