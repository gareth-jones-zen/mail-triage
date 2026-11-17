using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>New times proposed for your meetings, and invitation emails already answered (invites.py).</summary>
public static class Proposals
{
    public static readonly string[] HoldsTime = ["busy", "oof", "tentative"];
    public static readonly string[] Answered = ["accepted", "tentativelyAccepted", "declined"];
    private static readonly string[] MailAndCalendar = ["Mail.ReadWrite", .. Calendar.Scopes];

    /// <summary>A Graph dateTimeTimeZone as an aware time: UTC if it says so, otherwise local. (Any other zone is read
    /// as local: a Python quirk, kept for parity.)</summary>
    public static DateTimeOffset Aware(JsonNode? value, TimeZoneInfo zone)
    {
        var text = value.Req("dateTime");
        var t = PyTime.FromIso(text[..Math.Min(19, text.Length)]);
        var tz = value.Has("timeZone") && value.Get("timeZone") is not null ? Py.Str(value.Get("timeZone")) : "UTC";
        return tz.ToUpperInvariant() is "UTC" or "Z" ? new DateTimeOffset(t, TimeSpan.Zero) : PyTime.At(zone, t);
    }

    /// <summary>(accept?, why, the move) for someone's proposed new time for one of your meetings. Accepted only when
    /// it's your meeting, the proposer is the only other person in it, the new time is in the future and in working
    /// hours, and you're free then.</summary>
    public static (bool Ok, string Why, (string Id, DateTime Start, DateTime End)? Plan) Judge(Settings settings, ICalendar calendar,
        JsonObject message, JsonObject full, DateTime now)
    {
        var zone = calendar.Zone();
        DateTime Local(DateTimeOffset t) => PyTime.Local(t, zone);
        var who = message.Get("from").Get("emailAddress");
        var proposer = who.Str("name") is { Length: > 0 } n ? n : who.Str("address") is { Length: > 0 } a ? a : "someone";
        var address = (who.Str("address") ?? "").ToLowerInvariant();
        var proposed = full.Get("proposedNewTime");
        var newTime = (Aware(proposed.Get("start"), zone), Aware(proposed.Get("end"), zone));
        var old = (Aware(full.Get("startDateTime"), zone), Aware(full.Get("endDateTime"), zone));
        var when = $"{InviteTriage.Day(Local(newTime.Item1))}–{InviteTriage.Hm(Local(newTime.Item2))}";
        var asked = $"{proposer} proposes {when} instead of {InviteTriage.Day(Local(old.Item1))}";
        var e = full.Get("event") as JsonObject ?? [];
        if (!e.Truthy("id") || e.Truthy("isCancelled"))
            return (false, $"{asked}, but the meeting is gone or cancelled", null);
        if (!e.Truthy("isOrganizer"))
            return (false, $"{asked}: not your meeting to move", null);
        var eventId = Py.Str(e.Get("id"));
        var meeting = calendar.Between(old.Item1, old.Item2).FirstOrDefault(x =>
            (x.Str("id") == eventId || (x.Has("seriesMasterId") && x.Get("seriesMasterId") is not null && Py.Str(x.Get("seriesMasterId")) == eventId))
            && InviteTriage.Times(x).Start == Local(old.Item1));
        if (meeting is null)
            return (false, $"{asked}, but the meeting isn't at that time any more", null);
        var start = Local(newTime.Item1);
        var end = Local(newTime.Item2);
        if (start <= now)
            return (false, $"{asked}, which has passed", null);
        var others = InviteTriage.People(meeting).Where(x => InviteTriage.Address(x) is var ad && ad != settings.Email && ad != address).ToList();
        if (others.Count > 0)
            return (false, $"{asked}: {others.Count} other attendee(s) would move too", null);
        var hours = settings.Invites!.WorkingHours;
        var first = hours.Start != "24:00" ? PyTime.TimeFromIso(hours.Start) : PyTime.TimeMax;
        var last = hours.End != "24:00" ? PyTime.TimeFromIso(hours.End) : PyTime.TimeMax;
        if (start.TimeOfDay < first || (end.TimeOfDay > last && end.Date == start.Date) || end.Date != start.Date)
            return (false, $"{asked}: outside working hours", null);
        var meetingId = meeting.Req("id");
        var clashes = calendar.Between(newTime.Item1, newTime.Item2).Where(x => x.Str("id") != meetingId && !x.Truthy("isAllDay") && !x.Truthy("isCancelled")
            && x.Str("showAs") is { } showAs && HoldsTime.Contains(showAs) && x.Get("responseStatus").Str("response") != "declined"
            && InviteTriage.Times(x).Start < end && InviteTriage.Times(x).End > start).ToList();
        if (clashes.Count > 0)
            return (false, $"{asked}: clashes with {string.Join(", ", clashes.Select(x => x.StrOr("subject") is { Length: > 0 } s ? s : "(no subject)"))}", null);
        return (true, $"{asked}, and you're free", (meetingId, start, end));
    }

    private static string FromAddress(JsonObject m)
    {
        var from = m.Get("from");
        if (from is not JsonObject f)
            return "";
        return f.Has("emailAddress") ? f.Get("emailAddress").Fmt("address") : "";
    }

    /// <summary>Someone declined (or tentatively accepted) one of your meetings and proposed a new time. With
    /// accept_proposals, one you're free for is accepted: that occurrence moves and the proposal is deleted. The rest
    /// are queued as emails, each once, saying why. (queue items, acted, keys seen).</summary>
    public static (List<JsonObject> Items, List<JsonObject> Acted, HashSet<string> Seen) Run(Settings settings, Mailbox mailbox, ICalendar calendar,
        JsonObject raised, Action<string> log, bool dryRun = false, DateTime? now = null)
    {
        var at = now ?? Clock.Now;
        var items = new List<JsonObject>();
        var acted = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in settings.Invites!.ProposalsFolders)
        {
            const string query = "$top=50&$select=id,internetMessageId,subject,from,receivedDateTime,webLink";
            foreach (var m in mailbox.Graph.Pages($"/me/mailFolders/{mailbox.FolderId(name)}/messages?{query}", Mailbox.Scopes))
            {
                if (m.Str("@odata.type") != "#microsoft.graph.eventMessageResponse")
                    continue;
                var k = m.Str("internetMessageId") is { Length: > 0 } key ? key : m.Req("id");
                if (raised.ContainsKey(k))
                {
                    seen.Add(k);
                    continue;
                }
                // No $select: Graph reads it against the base message type and refuses proposedNewTime with a
                // 400 (13 Nov 2026, which failed every run for an hour). The whole message carries it.
                (bool Ok, string Why, (string Id, DateTime Start, DateTime End)? Plan) judged;
                try
                {
                    var full = mailbox.Graph.Request($"/me/messages/{m.Req("id")}"
                                                     + "?$expand=microsoft.graph.eventMessage/event($select=id,isOrganizer,isCancelled)", scopes: MailAndCalendar);
                    if (!full.Truthy("proposedNewTime"))
                        continue;   // an ordinary reply: nothing to decide
                    judged = Judge(settings, calendar, m, full, at);
                }
                catch (M365Graph.GraphRequestException error) when ((int)error.Status is 400 or 404)
                {
                    judged = (false, $"couldn't read the proposal (Graph said {(int)error.Status}); decide in Outlook", null);
                }
                var (ok, why, plan) = judged;
                if (ok && settings.Invites.AcceptProposals)
                {
                    if (!dryRun)
                    {
                        calendar.Move(plan!.Value.Id, plan.Value.Start, plan.Value.End);
                        mailbox.Delete(m.Req("id"));
                    }
                    acted.Add(new JsonObject
                    {
                        ["received"] = m.Get("receivedDateTime").Copy(), ["from"] = FromAddress(m), ["subject"] = m.GetCopy("subject", ""), ["why"] = why,
                    });
                    log($"{(dryRun ? "would accept" : "new time accepted")}  [{why}] {Py.Str(m.Get("subject"))}");
                    continue;
                }
                seen.Add(k);
                var item = new JsonObject
                {
                    ["folder"] = name, ["id"] = m.Req("id"), ["key"] = k, ["received"] = m.Get("receivedDateTime").Copy(), ["from"] = FromAddress(m),
                    ["subject"] = m.GetCopy("subject", ""), ["preview"] = "", ["rule"] = "New time proposed",
                    ["check"] = $"{(ok ? "ACCEPT" : "DECIDE")}: {why}", ["webLink"] = m.Get("webLink").Copy(),
                };
                if (plan is { } p)
                    item["move"] = new JsonObject { ["id"] = p.Id, ["start"] = PyTime.Iso(p.Start, "minutes"), ["end"] = PyTime.Iso(p.End, "minutes") };
                items.Add(item);
                raised[k] = PyTime.Iso(Clock.Now, "seconds");
                log($"{(dryRun ? "would queue" : "queued")}  [new time proposed] {why}");
            }
        }
        return (items, acted, seen);
    }

    /// <summary>Invitation emails in a folder whose meeting is answered, cancelled or gone (a decline removes it).
    /// Outlook deletes the invitation when you respond there; a response through Graph leaves it behind.
    ///
    /// Cancellation notices too (1.2.0): Graph types them eventMessage with meetingMessageType "meetingCancelled", not
    /// eventMessageRequest, so until then they sat in the folder for good. Each is found once its meeting is gone or
    /// cancelled; a cancelled meeting still on your calendar (Outlook's "Canceled:" entry, left until you remove it)
    /// carries its id as "remove", for the caller to take off the calendar before deleting the email.</summary>
    public static List<JsonObject> AnsweredRequests(IGraph graph, string folderId)
    {
        var output = new List<JsonObject>();
        const string query = "$top=50&$select=id,subject,from,receivedDateTime";
        foreach (var m in graph.Pages($"/me/mailFolders/{folderId}/messages?{query}", Mailbox.Scopes))
        {
            if (m.Str("@odata.type") == "#microsoft.graph.eventMessage")
            {
                if (Cancellation(graph, m) is { } c)
                    output.Add(c);
                continue;
            }
            if (m.Str("@odata.type") != "#microsoft.graph.eventMessageRequest")
                continue;
            var full = WithEvent(graph, m.Req("id"), "?$select=id&$expand=microsoft.graph.eventMessage/event($select=id,responseStatus,isCancelled)");
            if (full is null)
                continue;   // the email itself has gone (answered or deleted in Outlook since it was listed)
            var e = full.Get("event") as JsonObject ?? [];
            var response = e.Get("responseStatus").Str("response");
            if (!e.Truthy("id") || e.Truthy("isCancelled") || (response is not null && Answered.Contains(response)))
            {
                m["answer"] = !e.Truthy("id") ? "gone" : e.Truthy("isCancelled") ? "cancelled" : response;
                output.Add(m);
            }
        }
        return output;
    }

    /// <summary>A cancellation notice whose meeting is gone, or cancelled and still on your calendar (then "remove" is
    /// its id); null for any other eventMessage, or one whose meeting isn't marked cancelled. Never removes a meeting
    /// you organise. The message is read whole: meetingMessageType can't be $select-ed on the messages collection.</summary>
    private static JsonObject? Cancellation(IGraph graph, JsonObject m)
    {
        var full = WithEvent(graph, m.Req("id"), "?$expand=microsoft.graph.eventMessage/event($select=id,isCancelled,isOrganizer)");
        if (full is null || full.Str("meetingMessageType") != "meetingCancelled")
            return null;
        var e = full.Get("event") as JsonObject ?? [];
        if (!e.Truthy("id"))
            m["answer"] = "cancelled, already off the calendar";
        else if (e.Truthy("isCancelled") && !e.Truthy("isOrganizer"))
        {
            m["answer"] = "cancelled, removed from the calendar";
            m["remove"] = Py.Str(e.Get("id"));
        }
        else
            return null;
        return m;
    }

    /// <summary>A meeting email with its meeting expanded; null if the email itself has gone.
    /// <para>When the meeting has already been deleted (a cancelled meeting taken off the calendar), Graph answers the
    /// expanded read with 404 for the whole email, not an email without its event: seven cancellation notices did
    /// this on 17 Nov 2026, and the run stopped at the first. So a 404 is read again without the expansion; the email
    /// then comes back with no event, which the callers treat as a meeting that's gone (1.6.1).</para></summary>
    private static JsonObject? WithEvent(IGraph graph, string id, string expandQuery)
    {
        try
        {
            return graph.Request($"/me/messages/{id}{expandQuery}", scopes: MailAndCalendar);
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status == 404)
        {
            try
            {
                // Read whole: meetingMessageType can't be $select-ed (400), as on the collection.
                return graph.Request($"/me/messages/{id}", scopes: MailAndCalendar);
            }
            catch (M365Graph.GraphRequestException again) when ((int)again.Status == 404)
            {
                return null;
            }
        }
    }
}
