using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>One entry of someone's free/busy (getSchedule, 1.26.0), in local time. Subject is "" when their sharing
/// doesn't show it (free/busy only) or the entry is private; IsMeeting: it has attendees.</summary>
public sealed record ScheduleItem(DateTime Start, DateTime End, string? Status, string Subject, bool IsPrivate, bool IsMeeting);

/// <summary>What the invite, proposal and 1:1 code needs of a calendar (invites.Calendar in Python).</summary>
public interface ICalendar
{
    /// <summary>calendarView over whole days, in local time.</summary>
    List<JsonObject> Events(DateOnly start, long days);

    void Respond(string eventId, string action, string note = "", bool send = true);

    /// <summary>A new time for one of your meetings; with a note (1.26.0), the note goes at the top of that occurrence's
    /// description, in the same update the attendees get.</summary>
    void Move(string eventId, DateTime start, DateTime end, string note = "");

    void Cancel(string eventId, string comment = "");

    /// <summary>Take a meeting someone else cancelled off the calendar (sends nothing).</summary>
    void Remove(string eventId);

    /// <summary>Someone's free/busy between two local times, as (start, end, status) in local time; null if Graph
    /// couldn't read it.</summary>
    List<(DateTime Start, DateTime End, string? Status)>? Schedule(string address, DateTime start, DateTime end);

    /// <summary>Someone's calendar through free/busy (1.26.0), with what their sharing shows of each entry: the subject
    /// when you can see titles ("" when you can't, or it's private). Null if Graph couldn't read it. A calendar that
    /// doesn't override this gives no subjects.</summary>
    List<ScheduleItem>? ScheduleItems(string address, DateTime start, DateTime end) =>
        Schedule(address, start, end)?.Select(i => new ScheduleItem(i.Start, i.End, i.Status, "", false, false)).ToList();

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

    public void Move(string eventId, DateTime start, DateTime end, string note = "")
    {
        if (note.Length == 0)
        {
            writer.Move(eventId, start, end, Timezone);
            return;
        }
        var body = graph.Request($"/me/events/{eventId}?$select=body", scopes: Scopes, headers: Prefer).Get("body");
        writer.Move(eventId, start, end, Timezone, WithNote(body.Str("contentType") ?? "html", body.Str("content") ?? "", note));
    }

    /// <summary>A description with the note on top (1.26.0): once, however often the occurrence is moved with it.</summary>
    internal static JsonObject WithNote(string contentType, string content, string note)
    {
        var html = string.Equals(contentType, "html", StringComparison.OrdinalIgnoreCase);
        var line = html ? $"<p>{System.Net.WebUtility.HtmlEncode(note)}</p>" : note + "\n\n";
        if (content.Contains(html ? System.Net.WebUtility.HtmlEncode(note) : note, StringComparison.Ordinal))
            return new JsonObject { ["contentType"] = html ? "html" : "text", ["content"] = content };
        if (!html)
            return new JsonObject { ["contentType"] = "text", ["content"] = line + content };
        // After <body ...> when there is one, so the page stays well formed
        var at = content.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        var close = at >= 0 ? content.IndexOf('>', at) : -1;
        var merged = close >= 0 ? content[..(close + 1)] + line + content[(close + 1)..] : line + content;
        return new JsonObject { ["contentType"] = "html", ["content"] = merged };
    }

    public void Cancel(string eventId, string comment = "") => writer.Cancel(eventId, comment);

    public void Remove(string eventId) => writer.Remove(eventId);

    /// <summary>Create a hold (no people: Holds.Body; a room only if it's one of `allowedRooms`), in this calendar's zone. The
    /// created event, or null if journalled.</summary>
    public JsonObject? Hold(string subject, DateTime start, DateTime end, string body = "", string? location = null, bool online = false,
        HoldRoom? room = null, IReadOnlyCollection<string>? allowedRooms = null) =>
        writer.Hold(Holds.Body(subject, start, end, Timezone, body, location, online, room), allowedRooms);

    /// <summary>Send an invitation (1.24.0): SendInvites.Body in this calendar's zone, through the writer, which checks every
    /// attendee against what was approved. The created event, or null if journalled.</summary>
    public JsonObject? Invite(InviteDraft draft, IReadOnlyCollection<string> allowedRooms) =>
        writer.Invite(SendInvites.Body(draft, Timezone), draft.People.Select(p => p.Address).ToList(), allowedRooms);

    /// <summary>An event with the fields a sent invitation is read back for: its attendees as stored, and the Teams join
    /// link (1.24.0).</summary>
    public JsonObject InviteEvent(string eventId) =>
        graph.Request($"/me/events/{eventId}?$select=id,subject,start,end,attendees,isOnlineMeeting,onlineMeeting,webLink", scopes: Scopes, headers: Prefer);

    public List<(DateTime Start, DateTime End, string? Status)>? Schedule(string address, DateTime start, DateTime end) =>
        ScheduleItems(address, start, end)?.Select(i => (i.Start, i.End, i.Status)).ToList();

    public List<ScheduleItem>? ScheduleItems(string address, DateTime start, DateTime end)
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
        return person.Get("scheduleItems").Objects().Select(ReadScheduleItem).ToList();
    }

    /// <summary>A getSchedule scheduleItem: times in this calendar's zone, its show-as, and its subject where the sharing
    /// shows it.</summary>
    internal static ScheduleItem ReadScheduleItem(JsonObject i) =>
        new(PyTime.FromIso(i.Get("start").Req("dateTime")[..19]), PyTime.FromIso(i.Get("end").Req("dateTime")[..19]), i.Str("status"),
            i.Str("subject") ?? "", i.Truthy("isPrivate"), i.Truthy("isMeeting"));

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
