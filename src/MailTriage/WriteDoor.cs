using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage;

/// <summary>Every change to mail goes through one of these. Nothing else in the tool can write to a mailbox.</summary>
public interface IMailWriter
{
    /// <summary>Mark read, then move to Deleted Items (recoverable).</summary>
    void Delete(string messageId);

    /// <summary>Move to a folder, left unread.</summary>
    void File(string messageId, string folderId);
}

/// <summary>Every change to the calendar goes through one of these. Each notifies other people.</summary>
public interface ICalendarWriter
{
    /// <summary>accept, tentative or decline; a comment only when a response is sent.</summary>
    void Respond(string eventId, string action, string note, bool send);

    /// <summary>A new start and end, as local wall-clock times in the calendar's zone.</summary>
    void Move(string eventId, DateTime start, DateTime end, string timezone);

    /// <summary>Cancel one of your meetings (an occurrence's id cancels that one only).</summary>
    void Cancel(string eventId, string comment);
}

/// <summary>How a run may write: Live sends; Journal records what it would send and sends nothing; Refuse throws.
/// Shadow and replay runs never construct Live. A dry run uses Refuse, so a bug that writes fails loudly.</summary>
public enum WriteMode
{
    Live,
    Journal,
    Refuse,
}

/// <summary>One intended Graph write: the same method, path and body Live would send.</summary>
public sealed record WriteCall(string Method, string Url, JsonNode? Body)
{
    public string Text => $"{Method} {Url} {PyJson.Dump(Body, null, false)}";
}

/// <summary>The writers for one mode. Live writers write through the given (unfenced) Graph; the others never touch it.</summary>
public sealed class WriteDoor
{
    private WriteDoor(WriteMode mode, IMailWriter mail, ICalendarWriter calendar, List<WriteCall> journal)
    {
        Mode = mode;
        Mail = mail;
        Calendar = calendar;
        Journal = journal;
    }

    public WriteMode Mode { get; }
    public IMailWriter Mail { get; }
    public ICalendarWriter Calendar { get; }

    /// <summary>What a Journal door was asked to write, in order (empty otherwise).</summary>
    public List<WriteCall> Journal { get; }

    public static WriteDoor Live(IGraph graph)
    {
        var send = new GraphWrites(graph);
        return new WriteDoor(WriteMode.Live, send, send, []);
    }

    public static WriteDoor Journaling(Action<WriteCall>? also = null)
    {
        var journal = new List<WriteCall>();
        var record = new GraphWrites(call =>
        {
            journal.Add(call);
            also?.Invoke(call);
        });
        return new WriteDoor(WriteMode.Journal, record, record, journal);
    }

    public static WriteDoor Refusing()
    {
        var refuse = new GraphWrites(call => throw new WriteRefusedException($"{call.Method} {call.Url}"));
        return new WriteDoor(WriteMode.Refuse, refuse, refuse, []);
    }

    public static WriteDoor For(WriteMode mode, IGraph graph, Action<WriteCall>? journal = null) => mode switch
    {
        WriteMode.Live => Live(graph),
        WriteMode.Journal => Journaling(journal),
        _ => Refusing(),
    };
}

/// <summary>The Graph calls for each write, exactly as the Python made them, sent to a Graph or to a sink.</summary>
internal sealed class GraphWrites : IMailWriter, ICalendarWriter
{
    private static readonly string[] MailScopes = ["Mail.ReadWrite"];
    private static readonly string[] CalendarScopes = ["Calendars.ReadWrite"];
    private readonly Action<WriteCall, string[]> send;

    public GraphWrites(IGraph graph) => send = (call, scopes) => graph.Request(call.Url, call.Method, call.Body, scopes);

    public GraphWrites(Action<WriteCall> sink) => send = (call, _) => sink(call);

    public void Delete(string messageId)
    {
        send(new WriteCall("PATCH", $"/me/messages/{messageId}", new JsonObject { ["isRead"] = true }), MailScopes);
        send(new WriteCall("POST", $"/me/messages/{messageId}/move", new JsonObject { ["destinationId"] = "deleteditems" }), MailScopes);
    }

    public void File(string messageId, string folderId) =>
        send(new WriteCall("POST", $"/me/messages/{messageId}/move", new JsonObject { ["destinationId"] = folderId }), MailScopes);

    public void Respond(string eventId, string action, string note, bool send)
    {
        var verb = action switch
        {
            "accept" => "accept",
            "tentative" => "tentativelyAccept",
            "decline" => "decline",
            _ => throw new KeyNotFoundException(Py.ReprString(action)),
        };
        var body = new JsonObject { ["sendResponse"] = send };
        if (note.Length > 0 && send)
            body["comment"] = note;
        this.send(new WriteCall("POST", $"/me/events/{eventId}/{verb}", body), CalendarScopes);
    }

    public void Move(string eventId, DateTime start, DateTime end, string timezone)
    {
        JsonObject When(DateTime t) => new() { ["dateTime"] = PyTime.Strftime(t, "%Y-%m-%dT%H:%M:00"), ["timeZone"] = timezone };
        send(new WriteCall("PATCH", $"/me/events/{eventId}", new JsonObject { ["start"] = When(start), ["end"] = When(end) }), CalendarScopes);
    }

    public void Cancel(string eventId, string comment) =>
        send(new WriteCall("POST", $"/me/events/{eventId}/cancel", new JsonObject { ["comment"] = comment }), CalendarScopes);
}
