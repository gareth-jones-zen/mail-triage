using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>A person on an invitation: their address (lower case), their name when Graph could say cheaply, whether
/// they're optional, and whether the address is outside the owner's own domain.</summary>
public sealed record InvitePerson(string Address, string? Name, bool Optional, bool External)
{
    public string Shown => (Name is { Length: > 0 } ? $"{Name} <{Address}>" : Address) + (External ? " (external)" : "");
}

/// <summary>Everything an invitation says: what the preview shows is built from this, and so is what's sent.</summary>
public sealed record InviteDraft(string Subject, DateTime Start, DateTime End, IReadOnlyList<InvitePerson> People, string Body,
    HoldRoom? Room, string? Location, bool Online);

/// <summary>The body of an invitation: plain text with paragraphs (blank lines between) and `- ` bullet lines, nothing
/// else. Turned into simple HTML with everything escaped, and shown in the preview as it will read.</summary>
public static class InviteText
{
    /// <summary>A run of text lines or of bullet items; AfterGap when a blank line came before it (blank lines between
    /// paragraphs; several count as one, and those at the start or end are dropped).</summary>
    public sealed record Block(bool Bullets, List<string> Lines, bool AfterGap);

    public static List<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        Block? current = null;
        var gap = false;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                gap = blocks.Count > 0;
                current = null;
                continue;
            }
            var bullet = line.StartsWith("- ", StringComparison.Ordinal);
            var content = bullet ? line[2..].Trim() : line;
            if (current is null || current.Bullets != bullet)
            {
                current = new Block(bullet, [], gap);
                blocks.Add(current);
                gap = false;
            }
            current.Lines.Add(content);
        }
        return blocks;
    }

    /// <summary>&amp;, &lt;, &gt; and both quotes escaped: the text can never become markup.</summary>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    private const string Line = "<p style=\"margin:0\">";

    /// <summary>Laid out as Outlook writes mail, so it reads line for line as typed: each text line a paragraph with no
    /// spacing, each blank line between paragraphs an empty one, each run of bullets one list; in Outlook's own font.
    /// Empty text is an empty body.</summary>
    public static string Html(string text)
    {
        var blocks = Parse(text);
        if (blocks.Count == 0)
            return "";
        var html = new StringBuilder("<div style=\"font-family: Aptos, Calibri, Arial, sans-serif; font-size: 11pt;\">");
        foreach (var block in blocks)
        {
            if (block.AfterGap)
                html.Append(Line).Append("&nbsp;</p>");
            if (block.Bullets)
                html.Append("<ul style=\"margin-top:0;margin-bottom:0\">").Append(string.Concat(block.Lines.Select(l => $"<li>{Escape(l)}</li>"))).Append("</ul>");
            else
                html.Append(string.Concat(block.Lines.Select(l => $"{Line}{Escape(l)}</p>")));
        }
        return html.Append("</div>").ToString();
    }

    /// <summary>The body as it will read, for the preview: the lines as typed, bullets as "- ", one blank line where
    /// there were any.</summary>
    public static List<string> Readable(string text)
    {
        var lines = new List<string>();
        foreach (var block in Parse(text))
        {
            if (block.AfterGap)
                lines.Add("");
            lines.AddRange(block.Bullets ? block.Lines.Select(l => "- " + l) : block.Lines);
        }
        return lines;
    }
}

/// <summary>`send-invite` (1.24.0): send a meeting invitation from the owner's calendar to named people, once they've
/// approved its exact wording. It completes the find-a-slot flow (find a time, draft the invite in chat, approve it,
/// send it). It's `hold` with attendees: without --yes it shows exactly what would go and sends nothing; with --yes it
/// creates the meeting (Exchange sends the invitations), reads it back, and records it. **Never sent twice**: each send is
/// recorded by a hash of its start, end, subject and people, "sending" before the create and "sent" after, so a repeat
/// is refused, and a send whose answer was lost is never resent. --replace-hold deletes one of your holds once the
/// invitation is confirmed created (validated before anything is sent).</summary>
public static class SendInvites
{
    /// <summary>The meeting to create: Holds.Body's event (busy, normal, the room as a resource and the location) with the
    /// people added as required or optional attendees, an HTML body, responses requested, and Teams on unless turned off.
    /// The reminder is left to Outlook's default, as a hold's is.</summary>
    public static JsonObject Body(InviteDraft draft, string timezone)
    {
        var e = Holds.Body(draft.Subject, draft.Start, draft.End, timezone, "", draft.Location, draft.Online, draft.Room);
        e["body"] = new JsonObject { ["contentType"] = "html", ["content"] = InviteText.Html(draft.Body) };
        var attendees = new JsonArray();
        foreach (var person in draft.People)
        {
            var address = new JsonObject { ["address"] = person.Address };
            if (person.Name is { Length: > 0 } name)
                address["name"] = name;
            attendees.Add((JsonNode)new JsonObject { ["emailAddress"] = address, ["type"] = person.Optional ? "optional" : "required" });
        }
        foreach (var room in (JsonArray)e["attendees"]!)
            attendees.Add(room!.DeepClone());
        e["attendees"] = attendees;
        e["responseRequested"] = true;
        if (!draft.Online)
            e["isOnlineMeeting"] = false;
        return e;
    }

    /// <summary>Refuses any attendee the owner didn't approve: a person (required or optional) must be one of `people`, a
    /// resource one of `rooms` (`[hold] rooms`), and there must be at least one person.</summary>
    public static void CheckAttendees(JsonObject body, IReadOnlyCollection<string> people, IReadOnlyCollection<string> rooms)
    {
        if (body["attendees"] is not JsonArray attendees)
            throw new InvalidOperationException("Refusing an invitation whose attendees aren't a list.");
        var approved = new HashSet<string>(people, StringComparer.OrdinalIgnoreCase);
        var allowedRooms = new HashSet<string>(rooms, StringComparer.OrdinalIgnoreCase);
        var persons = 0;
        foreach (var attendee in attendees)
        {
            var address = attendee?["emailAddress"]?["address"]?.GetValue<string>();
            var type = attendee?["type"]?.GetValue<string>();
            var ok = type switch
            {
                "resource" => address is not null && allowedRooms.Contains(address),
                "required" or "optional" => address is not null && approved.Contains(address),
                _ => false,
            };
            if (!ok)
                throw new InvalidOperationException($"Refusing to send an invitation to {address ?? "(no address)"} ({type ?? "no type"}): "
                                                    + "only the people approved, and a room from [hold] rooms, may be on it.");
            if (type != "resource")
                persons++;
        }
        if (persons == 0)
            throw new InvalidOperationException("Refusing an invitation with no people on it: that's a hold (`mail-triage hold`).");
    }

    /// <summary>What makes two sends the same invitation: start, end, subject and the people, sorted (not the room).</summary>
    public static string Key(InviteDraft draft)
    {
        var people = string.Join(",", draft.People.Select(p => p.Address.ToLowerInvariant()).Order(StringComparer.Ordinal));
        var text = $"{PyTime.Strftime(draft.Start, "%Y-%m-%dT%H:%M")}|{PyTime.Strftime(draft.End, "%Y-%m-%dT%H:%M")}|{draft.Subject}|{people}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The Teams join link on a read-back event, if Microsoft has added it.</summary>
    public static string? JoinUrl(JsonObject e) =>
        e["onlineMeeting"] is JsonObject meeting && meeting["joinUrl"] is JsonValue v && v.TryGetValue<string>(out var url) && url.Length > 0 ? url : null;

    /// <summary>Whether --replace-hold's event can be replaced: yours, a single event, its subject starting "Hold:", and no
    /// one on it but a room. Returns the event, or why not.</summary>
    internal static (JsonObject? Hold, string? Refusal) CheckHold(Calendar calendar, string eventId, string? owner)
    {
        JsonObject e;
        try
        {
            e = calendar.Event(eventId);
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is 404 or 400)
        {
            return (null, $"--replace-hold: no event with that id in your calendar ({(int)error.Status}). Nothing sent.");
        }
        var subject = e.StrOr("subject");
        string? why = null;
        if (!e.Truthy("isOrganizer"))
            why = "it isn't yours (someone else organised it)";
        else if (!subject.TrimStart().StartsWith("Hold:", StringComparison.OrdinalIgnoreCase))
            why = "its subject doesn't start \"Hold:\"";
        else if (e.Truthy("isCancelled"))
            why = "it's cancelled";
        else if ((e.Str("type") ?? "singleInstance") != "singleInstance")
            why = "it's part of a series";
        else if (InviteTriage.People(e).Where(a => InviteTriage.Address(a) != owner).Select(InviteTriage.Address).ToList()
                 is { Count: > 0 } people)
            why = $"it has people on it ({string.Join(", ", people)}), so deleting it would send them a cancellation";
        return why is null ? (e, null) : (null, $"--replace-hold: \"{subject}\" can't be replaced: {why}. Only a hold of your own, with no one "
                                                + "on it but a room, is replaced. Nothing sent.");
    }

    /// <summary>The rooms a hold has booked (its resource attendees), by address.</summary>
    internal static List<string> HoldRooms(JsonObject hold) =>
        hold.Get("attendees").Objects().Where(a => a.Str("type") == "resource").Select(InviteTriage.Address).ToList();

    private static string When(DateTime start, DateTime end) => $"{PyTime.Strftime(start, "%a %d %b %H:%M")}-{PyTime.Strftime(end, "%H:%M")}";

    /// <summary>"\"Subject\", Tue 01 Dec 10:00-10:30, to 3 people (1 optional), room MTG Room 3.A.1, Teams".</summary>
    internal static string Summary(InviteDraft d)
    {
        var optional = d.People.Count(p => p.Optional);
        return $"\"{d.Subject}\", {When(d.Start, d.End)}, to {d.People.Count} {(d.People.Count == 1 ? "person" : "people")}"
               + (optional > 0 ? $" ({optional} optional)" : "") + (d.Room is null ? "" : $", room {d.Room.Name}")
               + (d.Location is { Length: > 0 } l ? $", at {l}" : "") + (d.Online ? ", Teams" : ", no Teams");
    }

    /// <summary>The preview: exactly what will go.</summary>
    internal static List<string> Preview(InviteDraft d, JsonObject? hold, IReadOnlyList<HoldRoom> rooms)
    {
        var lines = new List<string> { "Would send this invitation from your calendar (Exchange sends it to each person):" };
        void Field(string name, IEnumerable<string> values)
        {
            var first = true;
            foreach (var v in values)
            {
                lines.Add(first ? $"  {name,-9} {v}" : $"  {"",-9} {v}");
                first = false;
            }
        }
        Field("Subject", [d.Subject]);
        Field("When", [When(d.Start, d.End)]);
        Field("To", d.People.Where(p => !p.Optional).Select(p => p.Shown));
        Field("Optional", d.People.Where(p => p.Optional).Select(p => p.Shown));
        if (d.Room is { } room)
            Field("Room", [$"{room.Name} <{room.Address}> (booked as a resource: its mailbox accepts or declines)"]);
        if (d.Location is { Length: > 0 } location)
            Field("Location", [location]);
        Field("Teams", [d.Online ? "yes: a Teams meeting (Microsoft adds the join details; they can't be removed afterwards)" : "no"]);
        Field("Replies", ["requested; busy in your calendar"]);
        if (hold is not null)
        {
            var (start, end) = InviteTriage.Times(hold);
            var held = HoldRooms(hold);
            var notes = new List<string> { $"your hold \"{hold.StrOr("subject")}\", {When(start, end)}: deleted once the invitation is created" };
            string Named(string address) => rooms.FirstOrDefault(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase))?.Name ?? address;
            if (d.Room is { } r && held.Contains(r.Address, StringComparer.OrdinalIgnoreCase))
                notes.Add($"The hold has {r.Name} booked, so the room may decline this invitation as double-booked. The hold is deleted "
                          + "after the send, freeing the room; if the room declined, add it again in Outlook (it isn't resent from here).");
            else if (held.Count > 0)
                notes.Add($"The hold's room ({string.Join(", ", held.Select(Named))}) is freed when the hold is deleted.");
            Field("Replaces", notes);
        }
        var body = InviteText.Readable(d.Body);
        if (body.Count == 0)
            Field("Body", ["(none)"]);
        else
        {
            lines.Add("  Body:");
            lines.AddRange(body.Select(l => l.Length == 0 ? "    |" : $"    | {l}"));
        }
        return lines;
    }

    private static JsonObject LoadState(Settings settings) => StateFiles.LoadObject(settings.SentInvitesFile, () => []);

    /// <summary>Kept until the meeting has ended: a past start is refused anyway, so it can't be repeated after that.</summary>
    private static void SaveState(Settings settings, JsonObject state)
    {
        var now = PyTime.Strftime(Clock.Now, "%Y-%m-%dT%H:%M:%S");
        foreach (var (k, v) in state.ToList())
            if (v is JsonObject o && PyStr.CompareCodePoints(o.Str("end") ?? "", now) < 0)
                state.Remove(k);
        StateFiles.Save(settings.SentInvitesFile, state);
    }

    internal static int Run(Cli.Args args, Settings settings, IGraph graph, WriteDoor door, Action<string> log, CliHost host)
    {
        void Out(string text) => host.Out.WriteLine(Py.Lines(text));
        void Err(string text) => host.Err.WriteLine(Py.Lines(text));

        DateTime start, end;
        try
        {
            start = PyTime.FromIso(args.Positional[0]);
            end = args.Positional[1].Contains('T') ? PyTime.FromIso(args.Positional[1]) : start.Date + PyTime.TimeFromIso(args.Positional[1]);
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            Err("Start is YYYY-MM-DDTHH:MM and end is HH:MM, in local time, the same day.");
            return 2;
        }
        var subject = args.Positional[2].Trim();
        if (subject.Length == 0)
        {
            Err("The invitation needs a subject.");
            return 2;
        }
        if (end <= start)
        {
            Err($"The end ({PyTime.Strftime(end, "%d/%m %H:%M")}) must be after the start ({PyTime.Strftime(start, "%d/%m %H:%M")}).");
            return 2;
        }
        if (end.Date != start.Date)
        {
            Err("The end must be on the same day as the start.");
            return 2;
        }
        if (start <= Clock.Now)
        {
            Err($"The start ({PyTime.Strftime(start, "%a %d/%m %H:%M")}) is in the past.");
            return 2;
        }

        var owner = settings.Email?.ToLowerInvariant();
        var domain = owner?.Split('@') is [_, var d] ? d : null;
        static List<string> Split(string? value) =>
            (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToLowerInvariant()).Distinct().ToList();
        var to = Split(args.Value("--to"));
        var optional = Split(args.Value("--optional"));
        if (to.Count == 0)
        {
            Err("--to needs at least one address.");
            return 2;
        }
        foreach (var address in to.Concat(optional))
        {
            string? refusal = !Settings.IsAddress(address) ? $"{address} isn't an email address."
                : address == owner ? $"{address} is you: you're the organiser, so you're on it already."
                : settings.HoldRooms.Any(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase))
                    ? $"{address} is a meeting room: give it with --room, not as a person."
                : to.Contains(address) && optional.Contains(address) ? $"{address} is in both --to and --optional: give it once."
                : null;
            if (refusal is not null)
            {
                Err(refusal + " Nothing sent.");
                return 2;
            }
        }
        HoldRoom? room = null;
        if (args.Value("--room") is { } wanted)
        {
            room = Holds.FindRoom(settings.HoldRooms, wanted);
            if (room is null)
            {
                Err(Holds.NotARoom(settings.HoldRooms, wanted, "an invitation").Replace("Nothing written.", "Nothing sent."));
                return 2;
            }
        }
        string body;
        try
        {
            body = args.Value("--body") ?? (args.Value("--body-file") is { } file ? PyStr.ReadText(file) : "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Err($"Can't read the body file: {error.Message}");
            return 2;
        }

        // Names, when Graph gives them cheaply (a directory read the sign-in may not allow): the address is always shown.
        var reader = new ReadOnlyGraph(graph);
        bool External(string address) => domain is not null && !address.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase);
        string? Name(string address)
        {
            if (External(address))
                return null;
            try
            {
                return reader.Request($"/users/{Uri.EscapeDataString(address)}?$select=displayName", scopes: Calendar.Scopes).Str("displayName");
            }
            catch (M365Graph.GraphRequestException)
            {
                return null;
            }
        }
        var people = to.Select(a => new InvitePerson(a, Name(a), false, External(a)))
            .Concat(optional.Select(a => new InvitePerson(a, Name(a), true, External(a)))).ToList();
        var draft = new InviteDraft(subject, start, end, people, body, room, args.Value("--location"), !args.Flag("--no-online"));

        var calendar = new Calendar(graph, door.Calendar);
        JsonObject? hold = null;
        if (args.Value("--replace-hold") is { } holdId)
        {
            (hold, var refusal) = CheckHold(calendar, holdId, owner);
            if (refusal is not null)
            {
                Err(refusal);
                return 2;
            }
        }

        var key = Key(draft);
        var state = LoadState(settings);
        if (state[key] is JsonObject prior)
        {
            Err(prior.Str("state") == "sent"
                ? $"This invitation was already sent ({prior.Fmt("when")}{(prior.Str("id") is null ? "" : ", event " + prior.Str("id"))}): check your calendar. "
                  + "Not sent again."
                : $"This invitation may already have gone: a send started {prior.Fmt("when")} and its answer was lost. Check Outlook (your "
                  + "calendar and Sent Items); if it didn't go, send it from Outlook. It's never sent twice from here.");
            return 2;
        }

        if (!args.Flag("--yes"))
        {
            foreach (var line in Preview(draft, hold, settings.HoldRooms))
                Out(line);
            Out("Nothing sent. Run again with --yes to send it.");
            return 0;
        }

        var live = door.Mode == WriteMode.Live;
        var entry = new JsonObject
        {
            ["state"] = "sending", ["when"] = PyTime.Iso(Clock.Now, "seconds"), ["subject"] = subject,
            ["start"] = PyTime.Strftime(start, "%Y-%m-%dT%H:%M:%S"), ["end"] = PyTime.Strftime(end, "%Y-%m-%dT%H:%M:%S"),
            ["to"] = J.Array(to), ["optional"] = J.Array(optional),
        };
        if (live)
        {
            state[key] = entry;   // before the create: if its answer is lost, it's never sent again
            SaveState(settings, state);
        }
        void Forget()
        {
            if (!live)
                return;
            state.Remove(key);
            SaveState(settings, state);
        }
        JsonObject? created;
        try
        {
            created = calendar.Invite(draft, settings.HoldRooms.Select(r => r.Address).ToList());
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is >= 400 and < 500)
        {
            Forget();
            Err($"Microsoft refused the invitation ({(int)error.Status}): nothing was sent. {PyStr.Head(error.Message, 200)}");
            return 1;
        }
        catch (InvalidOperationException error)
        {
            Forget();
            Err(error.Message);
            return 1;
        }
        catch (Exception error) when (error is not WriteRefusedException)
        {
            log($"invite failed, may have gone: {subject} {PyTime.Strftime(start, "%d/%m %H:%M")}: {error.GetType().Name}: {error.Message}");
            Triage.RecordCalendarWrite(settings, "Invites sent", subject, start, owner ?? "",
                $"MAY HAVE GONE {PyTime.Strftime(start, "%a %d/%m %H:%M")}-{PyTime.Strftime(end, "%H:%M")}: the answer was lost; not resent",
                "send-invite", unconfirmed: true);
            Err("The invitation may or may not have gone (the call failed after it was sent). Check Outlook (your calendar and Sent "
                + "Items) before doing anything else. It's recorded as possibly sent, so it's never sent again from here; if it didn't "
                + "go, send it from Outlook." + (hold is null ? "" : " The hold wasn't deleted."));
            return 1;
        }
        if (!live)
        {
            Out($"Journalled, not sent: {Summary(draft)}." + (hold is null ? "" : $" Would then delete the hold \"{hold.StrOr("subject")}\"."));
            return 0;
        }
        var id = created?.Str("id");
        entry["state"] = "sent";
        if (id is not null)
            entry["id"] = id;
        SaveState(settings, state);

        if (id is null)
        {
            Out($"Sent: {Summary(draft)} (Microsoft returned no id: check your calendar)."
                + (hold is null ? "" : " The hold wasn't deleted, as the invitation couldn't be confirmed: delete it in Outlook once you've checked."));
            Triage.RecordCalendarWrite(settings, "Invites sent", subject, start, owner ?? "",
                $"sent {PyTime.Strftime(start, "%a %d/%m %H:%M")}-{PyTime.Strftime(end, "%H:%M")} to {people.Count}; no id returned", "send-invite");
            log($"invite sent  {subject}: no id returned");
            return 0;
        }

        Out($"Sent: {Summary(draft)}.");
        Out($"  id     {id}");
        string? answer = null;
        try
        {
            var back = Holds.ReadUntil(() => calendar.InviteEvent(id),
                e => (room is null || Holds.RoomAnswer(e, room.Address) is not null) && (!draft.Online || JoinUrl(e) is not null), host.Sleep);
            answer = room is null ? null : Holds.RoomAnswer(back, room.Address);
            Out("  attendees as stored:");
            foreach (var a in back.Get("attendees").Objects())
            {
                var name = a.Get("emailAddress").Str("name");
                var address = a.Get("emailAddress").Str("address") ?? "";
                Out($"    {(name is { Length: > 0 } && !string.Equals(name, address, StringComparison.OrdinalIgnoreCase) ? $"{name} <{address}>" : address)} ({a.Str("type") ?? "?"})");
            }
            if (draft.Online)
                Out(JoinUrl(back) is { } join ? $"  Teams  join link present: {join}" : "  Teams  no join link yet: check the invitation in Outlook.");
            if (back.Str("webLink") is { } link)
                Out($"  open   {link}");
        }
        catch (Exception error) when (error is not WriteRefusedException and not M365Graph.SignInNeededException)
        {
            Out($"  (couldn't read it back: {PyStr.Head(error.Message, 120)}; it was sent)");
        }
        var holdRooms = hold is null ? [] : HoldRooms(hold);
        var sameRoom = room is not null && holdRooms.Contains(room.Address, StringComparer.OrdinalIgnoreCase);
        if (room is not null)
            Out(answer switch
            {
                "accepted" => $"  room {room.Name} accepted: it's booked.",
                "tentativelyAccepted" => $"  room {room.Name} tentatively accepted: check the invitation in Outlook.",
                "declined" when sameRoom => $"  room {room.Name} DECLINED: your hold still had it booked.",
                "declined" => $"  room {room.Name} DECLINED (probably already booked at that time): the invitation went without the room. "
                              + "Not resent: pick another room in Outlook and send the update from there.",
                _ => $"  room {room.Name} hasn't answered yet: check the invitation in Outlook before relying on the room.",
            });

        var replaced = false;
        if (hold is not null)
        {
            var replaceId = hold.Req("id");
            var holdSubject = hold.StrOr("subject");
            try
            {
                calendar.Remove(replaceId);
                replaced = true;
                Out($"  hold \"{holdSubject}\" deleted" + (holdRooms.Count > 0 ? ", which frees the room it had booked." : "."));
            }
            catch (M365Graph.GraphRequestException error) when ((int)error.Status is 404 or 410)
            {
                replaced = true;
                Out($"  hold \"{holdSubject}\" had already gone.");
            }
            catch (Exception error) when (error is not WriteRefusedException)
            {
                log($"replace-hold: couldn't delete {holdSubject}: {error.GetType().Name}: {error.Message}");
                Out($"  couldn't delete the hold \"{holdSubject}\" ({PyStr.Head(error.Message, 120)}): delete it in Outlook. Not retried.");
            }
            if (sameRoom && answer == "declined")
                Out($"  The room declined because the hold had it; the hold is {(replaced ? "now deleted" : "still there")}, so open the "
                    + $"invitation in Outlook and add {room!.Name} again. It isn't resent from here.");
            else if (sameRoom && answer is null)
                Out($"  If {room!.Name} declines (the hold had it booked), add it again in Outlook now the hold's gone.");
        }

        var did = $"sent {PyTime.Strftime(start, "%a %d/%m %H:%M")}-{PyTime.Strftime(end, "%H:%M")} to {people.Count} "
                  + $"{(people.Count == 1 ? "person" : "people")}" + (optional.Count > 0 ? $" ({optional.Count} optional)" : "")
                  + (room is null ? "" : $"; room {room.Name} ({answer ?? "no answer yet"})") + $"; Teams: {(draft.Online ? "yes" : "no")}"
                  + (replaced ? $"; replaced hold \"{hold!.StrOr("subject")}\"" : "") + $" ({id})";
        Triage.RecordCalendarWrite(settings, "Invites sent", subject, start, owner ?? "", did, "send-invite", id, $"mail-triage cancel {id}");
        log($"invite sent  {subject}: {did}");
        return 0;
    }
}
