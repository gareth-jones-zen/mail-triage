using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage;

/// <summary>What the invite, proposal and 1:1 code needs of a calendar (invites.Calendar in Python).</summary>
public interface ICalendar
{
    /// <summary>calendarView over whole days, in local time.</summary>
    List<JsonObject> Events(DateOnly start, long days);

    void Respond(string eventId, string action, string note = "", bool send = true);

    void Move(string eventId, DateTime start, DateTime end);

    void Cancel(string eventId, string comment = "");

    /// <summary>Someone's free/busy between two local times, as (start, end, status) in local time; null if Graph
    /// couldn't read it.</summary>
    List<(DateTime Start, DateTime End, string? Status)>? Schedule(string address, DateTime start, DateTime end);

    JsonObject Event(string eventId);

    /// <summary>What's in the calendar between two aware times, in local time.</summary>
    List<JsonObject> Between(DateTimeOffset start, DateTimeOffset end);

    /// <summary>The calendar's zone.</summary>
    TimeZoneInfo Zone();
}

/// <summary>The calendar through Graph: reads through a ReadOnlyGraph, changes only through the write door.</summary>
public sealed class Calendar : ICalendar
{
    public const string Fields = "id,subject,start,end,isAllDay,showAs,isCancelled,isOrganizer,responseStatus,responseRequested,"
                                 + "organizer,attendees,type,seriesMasterId,webLink,bodyPreview,categories";
    public static readonly string[] Scopes = ["Calendars.ReadWrite"];

    private readonly IGraph graph;
    private readonly ICalendarWriter writer;

    public Calendar(IGraph graph, ICalendarWriter writer, string timezone = "GMT Standard Time")
    {
        this.graph = graph as ReadOnlyGraph ?? new ReadOnlyGraph(graph);
        this.writer = writer;
        Timezone = timezone;
    }

    public string Timezone { get; }

    private Dictionary<string, string> Prefer => new() { ["Prefer"] = $"outlook.timezone=\"{Timezone}\"" };

    public List<JsonObject> Events(DateOnly start, long days)
    {
        var query = Url.Encode([
            ("startDateTime", $"{PyTime.Iso(start)}T00:00:00"),
            ("endDateTime", $"{PyTime.Iso(start.AddDays((int)days))}T00:00:00"),
            ("$top", "200"), ("$orderby", "start/dateTime"), ("$select", Fields)]);
        return graph.Pages($"/me/calendarView?{query}", Scopes, Prefer).ToList();
    }

    public void Respond(string eventId, string action, string note = "", bool send = true) => writer.Respond(eventId, action, note, send);

    public void Move(string eventId, DateTime start, DateTime end) => writer.Move(eventId, start, end, Timezone);

    public void Cancel(string eventId, string comment = "") => writer.Cancel(eventId, comment);

    public List<(DateTime Start, DateTime End, string? Status)>? Schedule(string address, DateTime start, DateTime end)
    {
        JsonObject When(DateTime t) => new() { ["dateTime"] = PyTime.Strftime(t, "%Y-%m-%dT%H:%M:%S"), ["timeZone"] = Timezone };
        var body = new JsonObject
        {
            ["schedules"] = new JsonArray(address), ["startTime"] = When(start), ["endTime"] = When(end), ["availabilityViewInterval"] = 15,
        };
        var found = graph.Request(ReadOnlyGraph.SchedulePath, "POST", body, Scopes, Prefer);
        var person = found.Get("value") is JsonArray { Count: > 0 } people ? people[0] : new JsonObject();
        if (person.Truthy("error") || !person.Has("scheduleItems"))
            return null;
        return person.Get("scheduleItems").Objects()
            .Select(i => (PyTime.FromIso(i.Get("start").Req("dateTime")[..19]), PyTime.FromIso(i.Get("end").Req("dateTime")[..19]), i.Str("status")))
            .ToList();
    }

    public JsonObject Event(string eventId) => graph.Request($"/me/events/{eventId}?$select={Fields}", scopes: Scopes, headers: Prefer);

    public List<JsonObject> Between(DateTimeOffset start, DateTimeOffset end)
    {
        static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var query = Url.Encode([("startDateTime", Utc(start)), ("endDateTime", Utc(end)), ("$top", "50"), ("$select", Fields)]);
        return graph.Pages($"/me/calendarView?{query}", Scopes, Prefer).ToList();
    }

    /// <summary>The zone, by its Windows id (Python mapped it to Europe/London).</summary>
    public TimeZoneInfo Zone() => PyTime.Zone(Timezone);
}
