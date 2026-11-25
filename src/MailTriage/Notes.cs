using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;

namespace MailTriage;

/// <summary>What someone wrote in a meeting email that mail-triage answered or deleted without asking (1.31.0). A new
/// time accepted for your 1:1 sent the proposer's note ("can we push to 4, running late") to Deleted Items unseen: the
/// action was right, but the note was lost. So the note is kept in `deleted`, and a [[note_rule]] raises it in the queue.
/// <para>A reply (a new time proposed, an acceptance with a comment) carries only what its sender wrote, once Outlook's
/// and Teams' boilerplate is gone. An organiser's invitation, update or cancellation carries the whole meeting body, so
/// its note is what changed against the last version mail-triage saw of that meeting; with no earlier version, nothing
/// is shown, never the whole body.</para></summary>
public static class Notes
{
    /// <summary>About this many characters of a note are kept, on one line.</summary>
    public const int Max = 300;

    /// <summary>A queue item raised by a [[note_rule]].</summary>
    public const string Type = "note";

    /// <summary>The raised-once record of a note is the email's key with this in front (the state file).</summary>
    public const string RaisedPrefix = "note:";

    /// <summary>How much of a meeting's description is kept for the next comparison.</summary>
    public const int KeptDescription = 4000;

    /// <summary>A description not seen for this long is forgotten.</summary>
    public const int KeepDescriptionDays = 365;

    /// <summary>Asks Graph for the body as text, which it converts from HTML itself.</summary>
    public static readonly IReadOnlyDictionary<string, string> TextBody = new Dictionary<string, string>
    {
        ["Prefer"] = "outlook.body-content-type=\"text\"",
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex LinkTarget = new(@"<(?:https?|mailto|tel):[^>\s]*>", I, Timeout);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, Timeout);

    /// <summary>Lines from which the rest is someone else's: quoted history, a client's signature line, an Outlook
    /// "Original" block.</summary>
    private static readonly Regex[] HistoryStarts =
    [
        new(@"^-{2,}\s*Original (Appointment|Message)\s*-{2,}$", I, Timeout),
        new(@"^On .{3,200}\bwrote:$", I, Timeout),
        new(@"^(Sent from (my |Outlook)|Get Outlook for )", I, Timeout),
    ];

    private static readonly Regex HeaderFrom = new(@"^From:\s*\S", I, Timeout);
    private static readonly Regex HeaderNext = new(@"^(Sent|Date):\s*\S", I, Timeout);

    /// <summary>Lines that start a Teams (or Zoom) join block: it and everything after it is the meeting's furniture.</summary>
    private static readonly Regex[] JoinStarts =
    [
        new(@"^Microsoft Teams(\s+meeting)?(\s+Need help\?.*)?$", I, Timeout),
        new(@"^Join (on your computer|the meeting now|Microsoft Teams Meeting|with a video conferencing device|Zoom Meeting)\b", I, Timeout),
        new(@"^Click here to join the meeting\b", I, Timeout),
        new(@"^Meeting ID:\s*\d", I, Timeout),
    ];

    /// <summary>Lines dropped wherever they are: external-sender banners, and Outlook's own line on a proposal.</summary>
    private static readonly Regex[] Dropped =
    [
        new(@"^\W*\[?external( e-?mail| sender)?\]?\W*$", I, Timeout),
        new(@"^\W*(caution|warning|attention)\b.*\b(external|outside|originated|do not click|phishing)", I, Timeout),
        new(@"^(this (e-?mail|message) (originated|was sent|came) from outside|you don'?t often get e-?mail from|some people who received this message don'?t often get e-?mail from)", I, Timeout),
        new(@"^Proposed new time:", I, Timeout),
    ];

    /// <summary>A line of nothing but rules and punctuation (Outlook's ____ separators), or empty.</summary>
    private static readonly Regex Furniture = new(@"^[\s_\-=*~.·•|]*$", RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex SubjectPrefix = new(
        @"^\s*(new time proposed|accepted|declined|tentative|tentatively accepted|canceled|cancelled|updated invitation with note|updated invitation|updated|invitation|fwd?|re)\s*:\s*",
        I, Timeout);

    private static readonly Regex HtmlDrop = new(@"<(head|style|script)\b.*?</\1\s*>", I | RegexOptions.Singleline, Timeout);
    private static readonly Regex HtmlBreak = new(@"<br\s*/?>|</(p|div|tr|li|h\d|table|blockquote)\s*>|<hr\b[^>]*>", I, Timeout);
    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.CultureInvariant, Timeout);

    /// <summary>A Graph body ({contentType, content}) as text: as it is when Graph sent text, a plain reading of the HTML
    /// otherwise.</summary>
    public static string Text(JsonNode? body)
    {
        var content = body.Str("content") ?? "";
        if (!string.Equals(body.Str("contentType"), "html", StringComparison.OrdinalIgnoreCase))
            return content;
        var text = HtmlDrop.Replace(content, "");
        text = HtmlBreak.Replace(text, "\n");
        text = HtmlTag.Replace(text, "");
        return WebUtility.HtmlDecode(text);
    }

    /// <summary>The text with the boilerplate gone, a line for each paragraph: no link targets, no external-sender
    /// banners, nothing from the first Teams join block or quoted history on, no separator lines. Empty when nothing of
    /// the sender's own is left.</summary>
    public static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Split('\n')
            .Select(l => Spaces.Replace(LinkTarget.Replace(l, ""), " ").Trim()).ToList();
        var kept = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (HistoryStarts.Any(r => r.IsMatch(line)) || JoinStarts.Any(r => r.IsMatch(line)))
                break;
            if (HeaderFrom.IsMatch(line) && lines.Skip(i + 1).Take(4).Any(l => HeaderNext.IsMatch(l)))
                break;   // an Outlook reply header (From:, Sent:, To:, Subject:): what follows is the message replied to
            if (line.StartsWith('>') || Furniture.IsMatch(line) || Dropped.Any(r => r.IsMatch(line)))
                continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    /// <summary>One line, whitespace collapsed, cut to about max characters at a word.</summary>
    public static string OneLine(string text, int max = Max)
    {
        var line = Spaces.Replace(text, " ").Trim();
        if (line.Length <= max)
            return line;
        var cut = line[..max];
        var space = cut.LastIndexOf(' ');
        return (space > max * 3 / 5 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    /// <summary>A reply's note: what its sender wrote, on one line, or null when there's nothing but boilerplate.</summary>
    public static string? Comment(string text) => Clean(text) is { Length: > 0 } clean ? OneLine(clean) : null;

    /// <summary>What changed between two versions of a meeting's description (each already Clean): the lines added, on
    /// one line; or, if lines were only taken out, "removed: " and those. Null when they're the same.</summary>
    public static string? Changed(string before, string after)
    {
        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        var old = before.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in old)
            left[line] = left.GetValueOrDefault(line) + 1;
        var added = new List<string>();
        foreach (var line in after.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (left.GetValueOrDefault(line) > 0)
                left[line]--;
            else
                added.Add(line);
        }
        if (added.Count > 0)
            return OneLine(string.Join(" ", added));
        var removed = old.Where(line =>
        {
            if (left.GetValueOrDefault(line) <= 0)
                return false;
            left[line]--;
            return true;
        }).ToList();
        return removed.Count > 0 ? OneLine("removed: " + string.Join(" ", removed)) : null;
    }

    /// <summary>The meeting's subject from an email's: "New Time Proposed: ", "Accepted: ", "Canceled: " and the like taken off.</summary>
    public static string MeetingSubject(string emailSubject)
    {
        var subject = emailSubject;
        for (var i = 0; i < 4 && SubjectPrefix.Match(subject) is { Success: true } m; i++)
            subject = subject[m.Length..];
        return subject.Trim();
    }
}

/// <summary>A note found in one email (1.31.0): the text, and who it's from ("Sam Jones wrote", "description changed").</summary>
public sealed record Note(string Text, string Label);

/// <summary>One run's notes (1.31.0): reads the note of each email answered or deleted without asking, for its `deleted`
/// record, and raises it in the queue, once per email, when a [[note_rule]] names the meeting. A note that can't be read
/// is logged and left out: it never stops a run.</summary>
public sealed class NoteTaker
{
    private readonly Settings settings;
    private readonly Mailbox mailbox;
    private readonly JsonObject raised;
    private readonly bool dryRun;
    private readonly Action<string> log;
    private JsonObject? descriptions;
    private bool changed;

    private NoteTaker(Settings settings, Mailbox mailbox, JsonObject raised, bool dryRun, Action<string> log)
    {
        this.settings = settings;
        this.mailbox = mailbox;
        this.raised = raised;
        this.dryRun = dryRun;
        this.log = log;
    }

    /// <summary>Null when notes are off: neither quoted nor raised, so nothing is read.</summary>
    public static NoteTaker? For(Settings settings, Mailbox mailbox, JsonObject raised, bool dryRun, Action<string> log) =>
        settings.Notes.Wanted ? new NoteTaker(settings, mailbox, raised, dryRun, log) : null;

    /// <summary>The queue items raised this run.</summary>
    public List<JsonObject> Items { get; } = [];

    /// <summary>A reply carries only its sender's note; an organiser's message carries the whole meeting body.</summary>
    public enum Kind { Reply, Organiser }

    /// <summary>One email answered or deleted without asking. Did says what was done, as the queue item will say it
    /// ("Accepted Sam Jones's new time for 'Planning', Thu 26 Nov 16:00–17:00."). Body, if it's already been read, saves a
    /// read. EventId (and SeriesId, its series) is the meeting, for an organiser's message: without it, no note.</summary>
    public sealed record Email(JsonObject Message, string Folder, Kind Kind, string Did, string MeetingSubject, (string Name, string Address) Organiser,
        JsonNode? Body = null, string? EventId = null, string? SeriesId = null);

    /// <summary>The email's note, or null (none, only boilerplate, no earlier version to compare, or unreadable). It must
    /// be called before the email is moved: a moved email has a new id. Raises the note when a rule names the meeting.</summary>
    public Note? Take(Email email)
    {
        var message = email.Message;
        var subject = message.StrOr("subject");
        var who = message.Get("from").Get("emailAddress");
        var from = (Name: who.StrOr("name"), Address: (who.Str("address") ?? "").ToLowerInvariant());
        string text;
        try
        {
            text = email.Body is not null ? Notes.Text(email.Body) : Notes.Text(mailbox.Graph.Request($"/me/messages/{message.Req("id")}?$select=body",
                scopes: mailbox.ReadScopes, headers: Notes.TextBody).Get("body"));
        }
        catch (Exception error) when (error is not (OutOfMemoryException or M365Graph.SignInNeededException))
        {
            log($"note: couldn't read the note in {Py.ReprString(subject)} ({error.GetType().Name}: {error.Message}); left out");
            return null;
        }
        Note? note;
        if (email.Kind == Kind.Reply)
            note = Notes.Comment(text) is { } said ? new Note(said, $"{(from.Name.Length > 0 ? from.Name : from.Address)} wrote") : null;
        else
            note = Described(email, Notes.Clean(text)) is { } change ? new Note(change, "description changed") : null;
        if (note is null)
            return null;
        if (settings.Notes.Rules.FirstOrDefault(r => r.Matches(email.MeetingSubject, subject, email.Organiser, from)) is { } rule)
            Raise(email, rule, note, from.Address);
        return note;
    }

    /// <summary>Adds the note to a `deleted` record, if notes are quoted there.</summary>
    public void Annotate(JsonObject entry, Note? note)
    {
        if (note is null || !settings.Notes.QuoteInDeleted)
            return;
        entry["note"] = note.Text;
        entry["note_by"] = note.Label;
    }

    /// <summary>Writes the descriptions seen (not in a dry run), forgetting those not seen for a year.</summary>
    public void Save()
    {
        if (dryRun || !changed || descriptions is null)
            return;
        var cutoff = PyTime.Iso(Clock.Now - TimeSpan.FromDays(Notes.KeepDescriptionDays), "seconds");
        foreach (var key in descriptions.Select(p => p.Key).ToList())
            if (PyStr.CompareCodePoints(descriptions[key].Str("seen") ?? "", cutoff) < 0)
                descriptions.Remove(key);
        StateFiles.Save(settings.NotesFile, new JsonObject { ["descriptions"] = descriptions.DeepClone() });
    }

    /// <summary>What changed against the last version of this meeting's description that mail-triage saw (the meeting's
    /// own, or its series'); null with none, or for an email older than that version. This version is remembered.</summary>
    private string? Described(Email email, string clean)
    {
        if (email.EventId is not { Length: > 0 } id)
            return null;
        descriptions ??= StateFiles.LoadObject(settings.NotesFile, () => []).Get("descriptions") as JsonObject ?? [];
        var received = email.Message.Str("receivedDateTime") ?? "";
        var before = descriptions.Get(id) as JsonObject ?? (email.SeriesId is { Length: > 0 } series ? descriptions.Get(series) as JsonObject : null);
        if (before is not null && PyStr.CompareCodePoints(before.Str("received") ?? "", received) > 0)
            return null;   // a later version is already known: this one says nothing new
        var change = before is null ? null : Notes.Changed(before.Str("text") ?? "", clean);
        descriptions[id] = new JsonObject
        {
            ["text"] = clean.Length > Notes.KeptDescription ? clean[..Notes.KeptDescription] : clean, ["received"] = received,
            ["seen"] = PyTime.Iso(Clock.Now, "seconds"),
        };
        changed = true;
        return change;
    }

    /// <summary>The note as a queue item, once per email: recorded in the state file as "note:" and the email's key.</summary>
    private void Raise(Email email, NoteRule rule, Note note, string address)
    {
        var message = email.Message;
        var key = Notes.RaisedPrefix + Triage.Key(message);
        if (raised.ContainsKey(key))
            return;
        raised[key] = PyTime.Iso(Clock.Now, "seconds");
        var label = char.ToUpperInvariant(note.Label[0]) + note.Label[1..];
        Items.Add(new JsonObject
        {
            ["type"] = Notes.Type, ["folder"] = email.Folder, ["id"] = message.Req("id"), ["key"] = key, ["received"] = message.GetCopy("receivedDateTime"),
            ["from"] = address, ["subject"] = message.GetCopy("subject", ""), ["preview"] = note.Text, ["rule"] = rule.Name,
            ["check"] = $"WORTH A LOOK: {email.Did} {label}: \"{note.Text}\"",
        });
        log($"{(dryRun ? "would raise" : "raised")} its note  [{rule.Name}] {message.Fmt("subject")}");
    }
}
