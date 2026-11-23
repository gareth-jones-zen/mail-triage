using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>Holds (1.18.0): an event in your own calendar with no attendees, so it notifies nobody. For the find-a-slot
/// skill: it holds the chosen time; you open it, add the people and send it yourself. There is no way to add attendees
/// here, on purpose: an attendee would get an invitation the moment the event is created.</summary>
public static class Holds
{
    /// <summary>The event to create: busy, normal sensitivity, local wall-clock times in the calendar's zone, and an
    /// explicitly empty attendee list. `online` makes it a Teams meeting (its join details can't be removed later).</summary>
    public static JsonObject Body(string subject, DateTime start, DateTime end, string timezone, string body = "",
        string? location = null, bool online = false)
    {
        JsonObject When(DateTime t) => new() { ["dateTime"] = PyTime.Strftime(t, "%Y-%m-%dT%H:%M:00"), ["timeZone"] = timezone };
        var e = new JsonObject
        {
            ["subject"] = subject,
            ["start"] = When(start),
            ["end"] = When(end),
            ["showAs"] = "busy",
            ["sensitivity"] = "normal",
            ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = body },
            ["attendees"] = new JsonArray(),
        };
        if (location is { Length: > 0 })
            e["location"] = new JsonObject { ["displayName"] = location };
        if (online)
        {
            e["isOnlineMeeting"] = true;
            e["onlineMeetingProvider"] = "teamsForBusiness";
        }
        return e;
    }

    /// <summary>Refuses any body with an attendee: a hold must never send an invitation.</summary>
    public static void CheckNoAttendees(JsonObject body)
    {
        if (body["attendees"] is JsonArray { Count: > 0 } || body.ContainsKey("attendees") && body["attendees"] is not JsonArray)
            throw new InvalidOperationException("A hold never has attendees: refusing to create an event that would send invitations.");
    }
}
