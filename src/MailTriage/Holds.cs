using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>A meeting room a hold may book (1.23.0): one of `[hold] rooms` in the settings, the allow-list that is the
/// only way a room gets onto a hold. Name is what Outlook shows (and the hold's location); Address is the room's
/// mailbox.</summary>
public sealed record HoldRoom(string Name, string Address);

/// <summary>Holds (1.18.0): an event in your own calendar that invites no people, so it notifies nobody. For the find-a-slot
/// skill: it holds the chosen time; you open it, add the people and send it yourself. There is no way to add a person
/// here, on purpose: an attendee would get an invitation the moment the event is created. Since 1.23.0 a hold may book
/// one meeting room from `[hold] rooms`, added as a resource attendee (the room's mailbox answers; no person is
/// asked).</summary>
public static class Holds
{
    /// <summary>The event to create: busy, normal sensitivity, local wall-clock times in the calendar's zone, and an
    /// attendee list that is empty or holds only the room. `online` makes it a Teams meeting (its join details can't be
    /// removed later). A room sets the location to the room, as Outlook's room finder does.</summary>
    public static JsonObject Body(string subject, DateTime start, DateTime end, string timezone, string body = "",
        string? location = null, bool online = false, HoldRoom? room = null)
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
        if (room is not null)
        {
            ((JsonArray)e["attendees"]!).Add((JsonNode)new JsonObject
            {
                ["emailAddress"] = new JsonObject { ["address"] = room.Address, ["name"] = room.Name },
                ["type"] = "resource",
            });
            e["location"] = new JsonObject
            {
                ["displayName"] = room.Name, ["locationEmailAddress"] = room.Address, ["locationType"] = "conferenceRoom",
            };
        }
        else if (location is { Length: > 0 })
            e["location"] = new JsonObject { ["displayName"] = location };
        if (online)
        {
            e["isOnlineMeeting"] = true;
            e["onlineMeetingProvider"] = "teamsForBusiness";
        }
        return e;
    }

    /// <summary>Refuses any body that would invite a person: every attendee must be a resource whose address is one of
    /// `rooms` (the `[hold] rooms` allow-list; none when it's empty, so then no attendee at all). A hold must never send a
    /// person an invitation.</summary>
    public static void CheckOnlyRooms(JsonObject body, IReadOnlyCollection<string>? rooms = null)
    {
        if (!body.ContainsKey("attendees"))
            return;
        if (body["attendees"] is not JsonArray attendees)
            throw new InvalidOperationException("A hold never invites people: refusing an event whose attendees aren't a list.");
        var allowed = new HashSet<string>(rooms ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var attendee in attendees)
        {
            var address = attendee?["emailAddress"]?["address"]?.GetValue<string>();
            var type = attendee?["type"]?.GetValue<string>();
            if (type != "resource" || address is null || !allowed.Contains(address))
                throw new InvalidOperationException(
                    $"A hold never invites people: refusing to create an event with attendee {address ?? "(no address)"} "
                    + $"({type ?? "no type"}); only a resource from [hold] rooms may be added.");
        }
    }

    /// <summary>The room in `[hold] rooms` that `wanted` names, by address or name (any case); null if none does.</summary>
    public static HoldRoom? FindRoom(IEnumerable<HoldRoom> rooms, string wanted) =>
        rooms.FirstOrDefault(r => string.Equals(r.Address, wanted.Trim(), StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(r.Name, wanted.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Why `wanted` can't be booked: it isn't in `[hold] rooms`, or there's no such list.</summary>
    public static string NotARoom(IReadOnlyCollection<HoldRoom> rooms, string wanted, string what) => rooms.Count == 0
        ? $"{wanted} isn't a room {what} can book: there's no [hold] rooms in the settings, so {what} books no room."
        : $"{wanted} isn't one of [hold] rooms ({string.Join(", ", rooms.Select(r => r.Name))}): {what} books only a "
          + "listed room, never a person. Nothing written.";

    /// <summary>How many times an event is read back for a room's answer (or a Teams link), and the wait between reads.</summary>
    public const int Reads = 4;
    public static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(5);

    /// <summary>Read an event back until `done` holds, up to Reads times, ReadWait apart. Only reads: the write is never
    /// repeated, whatever comes back.</summary>
    public static JsonObject ReadUntil(Func<JsonObject> read, Func<JsonObject, bool> done, Action<TimeSpan> sleep)
    {
        var back = read();
        for (var n = 1; !done(back) && n < Reads; n++)
        {
            sleep(ReadWait);
            back = read();
        }
        return back;
    }

    /// <summary>The room's answer on a read-back event: "accepted", "declined", "tentativelyAccepted", or null while it
    /// hasn't answered ("none", "notResponded") or isn't on the event.</summary>
    public static string? RoomAnswer(JsonObject ev, string address)
    {
        if (ev["attendees"] is not JsonArray attendees)
            return null;
        foreach (var attendee in attendees)
            if (string.Equals(attendee?["emailAddress"]?["address"]?.GetValue<string>(), address, StringComparison.OrdinalIgnoreCase))
            {
                var response = attendee?["status"]?["response"]?.GetValue<string>();
                return response is null or "none" or "notResponded" ? null : response;
            }
        return null;
    }
}
