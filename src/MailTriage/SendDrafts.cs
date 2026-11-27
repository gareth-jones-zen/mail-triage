using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using M365Graph;
using PyCompat;
using WinBackground;

namespace MailTriage;

/// <summary>`drafts` and `send-draft` (1.35.0): send an Outlook draft, as it is, once the owner has seen exactly what will
/// go. The preview reads only and prints a fingerprint of everything it showed; `--yes` sends only with `--seen` that
/// fingerprint, after reading the draft again under the run lock, so an edit in Outlook (or a different draft at that
/// number) is refused, never sent unseen. The send is Graph's POST /me/messages/{id}/send (Mail.Send), made once.
/// <para><b>Never sent twice:</b> "sending" is saved in sent_drafts_file before the POST and "sent" after. A send whose answer
/// was lost is read back at once (a sent draft is no longer a draft); if that doesn't settle it, it's recorded as
/// unconfirmed and <b>never sent again by this tool</b>: the owner checks Sent Items and, if it didn't go, sends it from
/// Outlook. There is no undo: once Microsoft accepts it, it's gone to people.</para>
/// <para>The body is never written anywhere: the state file keeps the addresses, `deleted` and the journal only counts.</para></summary>
public static class SendDrafts
{
    /// <summary>The heading in the deleted file (`deleted` lists them under it; `restore` refuses them).</summary>
    public const string Heading = "Emails sent";

    /// <summary>The Prefer header for immutable ids: a message's ordinary id changes when sending moves it to Sent Items.</summary>
    public const string ImmutableId = "IdType=\"ImmutableId\"";

    public static readonly string[] SendScopes = ["Mail.Send"];

    internal const string MayHaveGone = "MAY HAVE GONE: check Sent Items in Outlook; this tool won't send it again.";

    private static readonly IReadOnlyDictionary<string, string> Immutable = new Dictionary<string, string> { ["Prefer"] = ImmutableId };

    private static readonly IReadOnlyDictionary<string, string> ImmutableText = new Dictionary<string, string>
    {
        ["Prefer"] = ImmutableId + ", " + Notes.TextBody["Prefer"],
    };

    private const string Fields = "id,subject,from,sender,toRecipients,ccRecipients,bccRecipients,replyTo,body,importance,isDraft,parentFolderId,"
                                  + "conversationId,internetMessageId,lastModifiedDateTime,changeKey,isReadReceiptRequested,isDeliveryReceiptRequested,hasAttachments";

    private const string ThreadFields = "id,isDraft,from,toRecipients,ccRecipients,receivedDateTime,subject";

    private static readonly Regex Substantive = new(@"[\p{L}\p{N}]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Furniture = new(@"^[\s_\-=*~.·•|]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Forwarded = new(@"^\s*(fw|fwd)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // ---- the model ------------------------------------------------------------------------------------------------

    /// <summary>One address on the draft, as Graph gives it; External when its domain isn't one of internal_domains.</summary>
    public sealed record Person(string Address, string? Name, bool External)
    {
        public string Key => Address.ToLowerInvariant();
        public string Who => Name is { Length: > 0 } ? Name : Address;

        public string Shown => (Name is { Length: > 0 } n && !string.Equals(n, Address, StringComparison.OrdinalIgnoreCase) ? $"{n} <{Address}>" : Address)
                               + (External ? " (external)" : "");
    }

    public sealed record Attachment(string Name, long Size, string ContentType, bool Inline, string Type);

    /// <summary>A draft as read, everything the preview shows and the fingerprint covers.</summary>
    public sealed class Draft
    {
        public required string Id { get; init; }
        public string Subject { get; init; } = "";
        public string? From { get; init; }
        public string? FromName { get; init; }
        public string? Sender { get; init; }
        public List<Person> To { get; init; } = [];
        public List<Person> Cc { get; init; } = [];
        public List<Person> Bcc { get; init; } = [];
        public List<Person> ReplyTo { get; init; } = [];
        public string BodyType { get; init; } = "";
        public string BodyContent { get; init; } = "";
        public string Text { get; set; } = "";
        public string Importance { get; init; } = "normal";
        public bool ReadReceipt { get; init; }
        public bool DeliveryReceipt { get; init; }
        public bool IsDraft { get; init; }
        public bool Meeting { get; init; }
        public string? ParentFolderId { get; init; }
        public string? ConversationId { get; init; }
        public string? InternetMessageId { get; init; }
        public string? LastModified { get; init; }
        public List<Attachment> Attachments { get; set; } = [];

        public string Short => ShortId(Id);

        /// <summary>Everyone it goes to, each address once.</summary>
        public List<Person> People => To.Concat(Cc).Concat(Bcc).GroupBy(p => p.Key).Select(g => g.First()).ToList();
    }

    /// <summary>What the preview works out about a draft.</summary>
    public sealed class View
    {
        public required Draft Draft { get; init; }
        public string Kind { get; set; } = "can't tell";
        public string KindText { get; set; } = "";
        public string ThreadText { get; set; } = "";
        public int Newer { get; set; }
        public List<string> Written { get; set; } = [];
        public List<string> Quoted { get; set; } = [];
        public List<string> Flags { get; } = [];
        public List<string> Refusals { get; } = [];
        public string Fingerprint { get; set; } = "";
    }

    /// <summary>The first 12 hex characters of the SHA-256 of the draft's immutable Graph id: stable while it's kept.</summary>
    public static string ShortId(string graphId) => Hex(graphId)[..12];

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>What the preview showed, as 12 hex characters: the first 8 of a SHA-256 over all of it (the draft's id; from,
    /// to, cc, bcc and reply-to, lower case and sorted; subject, importance and the receipts; the body's type and content as
    /// stored; each attachment's name, size, type and inline flag, sorted), then one character for each of those four
    /// parts, so a refusal can say which changed.</summary>
    public static string Fingerprint(Draft d)
    {
        static string Addresses(IEnumerable<Person> people) => string.Join(",", people.Select(p => p.Key).Order(StringComparer.Ordinal));
        string[] parts =
        [
            string.Join("\u001e", (d.From ?? "").ToLowerInvariant(), (d.Sender ?? "").ToLowerInvariant(), Addresses(d.To), Addresses(d.Cc),
                Addresses(d.Bcc), Addresses(d.ReplyTo)),
            string.Join("\u001e", d.Subject, d.Importance.ToLowerInvariant(), d.ReadReceipt ? "1" : "0", d.DeliveryReceipt ? "1" : "0"),
            string.Join("\u001e", d.BodyType.ToLowerInvariant(), d.BodyContent),
            string.Join("\u001e", d.Attachments.Select(a => $"{a.Name}\u001f{a.Size}\u001f{a.ContentType}\u001f{(a.Inline ? 1 : 0)}").Order(StringComparer.Ordinal)),
        ];
        return Hex(d.Id + "\u001d" + string.Join("\u001d", parts))[..8] + string.Concat(parts.Select(p => Hex(p)[0]));
    }

    private static readonly string[] PartNames = ["the recipients", "the subject or how it's sent", "the body", "the attachments"];

    /// <summary>What differs between the fingerprint a preview printed and the draft's now, in words.</summary>
    internal static string Changed(string seen, string now)
    {
        if (!PyRegex.Get("^[0-9a-f]{12}$").FullMatch(seen))
            return "that isn't a fingerprint a preview printed";
        var parts = Enumerable.Range(0, 4).Where(i => seen[8 + i] != now[8 + i]).Select(i => PartNames[i]).ToList();
        return parts.Count == 0 ? "it's a different draft, or it changed in a way the fingerprint can't place"
            : "changed: " + string.Join(", ", parts);
    }

    // ---- reading --------------------------------------------------------------------------------------------------

    /// <summary>The reads one command makes: the folders' ids (once), the owner and the internal domains.</summary>
    internal sealed class Context(Settings settings, IGraph graph)
    {
        private readonly Dictionary<string, string?> folders = [];
        public Settings Settings { get; } = settings;
        public IGraph Reader { get; } = graph as ReadOnlyGraph ?? new ReadOnlyGraph(graph);
        public string Owner { get; } = settings.Email ?? "";

        public IReadOnlyList<string> Domains { get; } = settings.SendDraft.InternalDomains.Count > 0 ? settings.SendDraft.InternalDomains
            : Forwarding.Domain(settings) is { } d ? [d] : [];

        /// <summary>Outside every internal domain: only an exact match counts, so a subdomain is outside.</summary>
        public bool External(string address) =>
            address.LastIndexOf('@') is var at and >= 0 && !Domains.Contains(address[(at + 1)..].Trim().ToLowerInvariant());

        /// <summary>A well-known folder's id (drafts, sentitems, outbox), or null if it can't be read.</summary>
        public string? Folder(string name)
        {
            if (!folders.TryGetValue(name, out var id))
            {
                try
                {
                    id = Reader.Request($"/me/mailFolders/{name}?$select=id", scopes: Mailbox.Scopes, headers: Immutable).Str("id");
                }
                catch (GraphRequestException)
                {
                    id = null;
                }
                folders[name] = id;
            }
            return id;
        }
    }

    private static List<Person> People(Context c, JsonNode? list) =>
        list.Objects().Select(r => r.Get("emailAddress")).Where(a => a.Str("address") is { Length: > 0 })
            .Select(a => new Person(a.Req("address").Trim(), a.Str("name")?.Trim(), c.External(a.Req("address")))).ToList();

    private static Draft FromJson(Context c, JsonObject m) => new()
    {
        Id = m.Req("id"), Subject = m.StrOr("subject"),
        From = m.Get("from").Get("emailAddress").Str("address"), FromName = m.Get("from").Get("emailAddress").Str("name"),
        Sender = m.Get("sender").Get("emailAddress").Str("address"),
        To = People(c, m.Get("toRecipients")), Cc = People(c, m.Get("ccRecipients")), Bcc = People(c, m.Get("bccRecipients")),
        ReplyTo = People(c, m.Get("replyTo")),
        BodyType = m.Get("body").Str("contentType") ?? "", BodyContent = m.Get("body").Str("content") ?? "", Text = Notes.Text(m.Get("body")),
        Importance = m.Str("importance") ?? "normal", ReadReceipt = m.Truthy("isReadReceiptRequested"), DeliveryReceipt = m.Truthy("isDeliveryReceiptRequested"),
        IsDraft = m.Truthy("isDraft"), Meeting = (m.Str("@odata.type") ?? "").Contains("eventMessage", StringComparison.OrdinalIgnoreCase),
        ParentFolderId = m.Str("parentFolderId"), ConversationId = m.Str("conversationId"), InternetMessageId = m.Str("internetMessageId"),
        LastModified = m.Str("lastModifiedDateTime"),
    };

    /// <summary>Its attachments from the attachments list (never hasAttachments, which is false for inline-only), without
    /// their content.</summary>
    private static List<Attachment> Attachments(Context c, string id) =>
        c.Reader.Pages($"/me/messages/{id}/attachments?$select=id,name,contentType,size,isInline,lastModifiedDateTime", Mailbox.Scopes, Immutable)
            .Select(a => new Attachment(a.StrOr("name"), a.Get("size") is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0, a.StrOr("contentType"),
                a.Truthy("isInline"), (a.Str("@odata.type") ?? "").Replace("#microsoft.graph.", ""))).ToList();

    /// <summary>One draft, read in full: the message, its body as text (for the preview only), its attachments.</summary>
    internal static Draft Read(Context c, string id)
    {
        var d = FromJson(c, c.Reader.Request($"/me/messages/{id}?$select={Fields}", scopes: Mailbox.Scopes, headers: Immutable));
        try
        {
            d.Text = c.Reader.Request($"/me/messages/{d.Id}?$select=body", scopes: Mailbox.Scopes, headers: ImmutableText).Get("body").Str("content") ?? d.Text;
        }
        catch (GraphRequestException)
        {
        }
        d.Attachments = Attachments(c, d.Id);
        return d;
    }

    /// <summary>The Drafts folder, newest change first (lazily, page by page).</summary>
    private static IEnumerable<JsonObject> DraftsFolder(Context c, string select = Fields) =>
        c.Reader.Pages($"/me/mailFolders/drafts/messages?{Url.Encode([("$select", select), ("$orderby", "lastModifiedDateTime desc"), ("$top", "50")])}",
            Mailbox.Scopes, Immutable);

    /// <summary>The other messages in the draft's conversation, newest first; null when it can't be read.</summary>
    private static List<JsonObject>? Thread(Context c, Draft d)
    {
        if (d.ConversationId is not { Length: > 0 } conversation)
            return [];
        try
        {
            var query = Url.Encode([("$filter", $"conversationId eq '{conversation.Replace("'", "''")}'"), ("$select", ThreadFields), ("$top", "25")]);
            return c.Reader.Pages($"/me/messages?{query}", Mailbox.Scopes, Immutable).Take(100)
                .Where(m => m.Str("id") != d.Id && !m.Truthy("isDraft"))
                .OrderByDescending(m => m.StrOr("receivedDateTime"), StringComparer.Ordinal).ToList();
        }
        catch (Exception error) when (error is not SignInNeededException and not WriteRefusedException)
        {
            return null;
        }
    }

    // ---- working it out -------------------------------------------------------------------------------------------

    internal static DateTime? Local(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? TimeZoneInfo.ConvertTimeFromUtc(utc, Clock.Provider.LocalTimeZone) : null;

    internal static string When(string? iso) => Local(iso) is { } t ? PyTime.Strftime(t, "%a %d %b %H:%M") : "an unknown time";

    private static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    /// <summary>The lines from the first quoted header on: Outlook's From:/Sent: block (or its one-line form), an
    /// "Original Message" line, or "On ... wrote:". A separator line just before it goes with it.</summary>
    internal static int QuotedFrom(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Notes.StartsQuote(lines, i))
                continue;
            while (i > 0 && lines[i - 1].Trim().Length > 0 && Furniture.IsMatch(lines[i - 1]))
                i--;
            return i;
        }
        return lines.Count;
    }

    private static string Strip(string line) => Notes.ImagePlaceholder.Replace(Notes.LinkTarget.Replace(line, ""), " ");

    /// <summary>Everything the preview says, the fingerprint, and why it can't be sent (if it can't).</summary>
    internal static View Analyse(Context c, Draft d)
    {
        var v = new View { Draft = d, Fingerprint = Fingerprint(d) };
        var lines = d.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        var at = QuotedFrom(lines);
        v.Written = lines.Take(at).ToList();
        v.Quoted = lines.Skip(at).ToList();
        while (v.Written.Count > 0 && Furniture.IsMatch(v.Written[^1]))
            v.Written.RemoveAt(v.Written.Count - 1);
        while (v.Written.Count > 0 && v.Written[0].Trim().Length == 0)
            v.Written.RemoveAt(0);
        while (v.Quoted.Count > 0 && v.Quoted[^1].Trim().Length == 0)
            v.Quoted.RemoveAt(v.Quoted.Count - 1);
        var written = string.Join("\n", v.Written.Select(Strip));
        var hasWritten = Substantive.IsMatch(written);

        // Kind, against the thread: the newest message in the conversation that isn't a draft is the one replied to.
        var thread = Thread(c, d);
        var owner = c.Owner.ToLowerInvariant();
        var ours = d.To.Concat(d.Cc).Select(p => p.Key).Where(a => a != owner).Distinct().ToList();
        var forward = Forwarded.IsMatch(d.Subject);
        if (thread is null)
        {
            (v.Kind, v.KindText) = forward ? ("forward", "forward (its thread couldn't be read)")
                : ("can't tell", "can't tell (the thread couldn't be read): the recipients are as listed");
            v.ThreadText = "couldn't be read, so whether anything newer arrived isn't known";
        }
        else if (thread.Count == 0)
        {
            (v.Kind, v.KindText) = forward ? ("forward", "forward") : ("new", "new message (nothing else in its thread)");
            v.ThreadText = "no other messages in its thread";
        }
        else
        {
            var last = thread[0];
            var sender = last.Get("from").Get("emailAddress");
            var from = (sender.Str("address") ?? "").ToLowerInvariant();
            var who = sender.Str("name") is { Length: > 0 } name ? name : from;
            var of = $"(from {who}, {When(last.Str("receivedDateTime"))})";
            var others = last.Get("toRecipients").Objects().Concat(last.Get("ccRecipients").Objects())
                .Select(r => (r.Get("emailAddress").Str("address") ?? "").ToLowerInvariant()).Where(a => a.Length > 0 && a != owner && a != from).Distinct().ToList();
            var theirs = (from.Length > 0 && from != owner ? [from] : new List<string>()).Concat(others).Distinct().ToList();
            var added = ours.Where(a => !theirs.Contains(a)).ToList();
            var dropped = theirs.Where(a => !ours.Contains(a)).ToList();
            string Changes() => (added.Count > 0 ? $"; ADDS {string.Join(", ", added)}" : "") + (dropped.Count > 0 ? $"; DROPS {string.Join(", ", dropped)}" : "");
            if (forward)
                (v.Kind, v.KindText) = ("forward", $"forward of the thread's last message {of}");
            else if (ours.Count > 0 && !ours.Any(theirs.Contains))
                (v.Kind, v.KindText) = ("can't tell", $"can't tell: none of these people were on the last message in the thread {of}");
            else if (others.Count > 0 && ours.Any(others.Contains))
                (v.Kind, v.KindText) = ("reply-all", added.Count + dropped.Count == 0
                    ? $"REPLY-ALL: the same {Plural(ours.Count, "person", "people")} as the last message in the thread {of}"
                    : $"REPLY-ALL to the last message in the thread {of}{Changes()}");
            else
                (v.Kind, v.KindText) = ("reply", $"reply to {who} only, the last message in the thread {of}" + (added.Count > 0 ? $"; ADDS {string.Join(", ", added)}" : ""));
            var changed = Local(d.LastModified);
            var newer = thread.Where(m => Local(m.Str("receivedDateTime")) is { } t && changed is { } ch && t > ch).ToList();
            v.Newer = newer.Count;
            v.ThreadText = newer.Count == 0
                ? $"nothing newer in the thread since this draft was last changed ({When(d.LastModified)})"
                : $"{Plural(newer.Count, "NEWER MESSAGE")} since this draft was last changed ({When(d.LastModified)}): "
                  + string.Join("; ", newer.Select(m => $"from {(m.Get("from").Get("emailAddress").Str("name") is { Length: > 0 } n ? n : m.Get("from").Get("emailAddress").StrOr("address"))}, {When(m.Str("receivedDateTime"))}"));
        }

        // Flags.
        var people = d.People;
        var external = people.Count(p => p.External);
        var config = c.Settings.SendDraft;
        if (external > 0)
            v.Flags.Add($"{external} EXTERNAL recipient{(external == 1 ? "" : "s")}");
        if (people.Count > config.WarnRecipients)
            v.Flags.Add($"{people.Count} PEOPLE: more than {config.WarnRecipients}");
        foreach (var pattern in config.Warn)
            if (PyRegex.Get(pattern, true).Search(written) is { } match)
                v.Flags.Add($"CHECK THE TEXT: \"{PyStr.Head(match.Value, 60)}\" (matches {Py.ReprString(pattern)})");
        if (v.Newer > 0)
            v.Flags.Add($"{Plural(v.Newer, "NEWER MESSAGE")} in the thread since this draft was last changed: the reply may be out of date");

        // Refusals.
        var drafts = c.Folder("drafts");
        if (!d.IsDraft)
            v.Refusals.Add("it isn't a draft (it's been sent, or it's someone's mail)");
        else if (drafts is null || d.ParentFolderId != drafts)
            v.Refusals.Add("it isn't in the Drafts folder");
        if (d.Meeting)
            v.Refusals.Add("it's a meeting message (an invitation, a response or a cancellation): those go through send-invite, respond or cancel");
        foreach (var address in new[] { d.From, d.Sender })
            if (address is { Length: > 0 } a && !string.Equals(a, c.Owner, StringComparison.OrdinalIgnoreCase))
            {
                v.Refusals.Add($"it's from {a}, not you ({c.Owner}): sending as someone else isn't in this version");
                break;
            }
        if (people.Count == 0)
            v.Refusals.Add("it has no recipients (to, cc or bcc)");
        else if (people.Count > config.MaxRecipients)
            v.Refusals.Add($"it has {people.Count} people, more than max_recipients ({config.MaxRecipients}): send a mailing like that from Outlook, deliberately");
        if (d.Subject.Trim().Length == 0)
            v.Refusals.Add("its subject is empty");
        if (!Substantive.IsMatch(Strip(d.Text)))
            v.Refusals.Add("its body is empty");
        else if (!hasWritten && v.Quoted.Count > 0 && v.Kind != "forward")
            v.Refusals.Add("nothing is written above the quoted thread");
        return v;
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} bytes" : bytes < 1024 * 1024 ? $"{(bytes + 512) / 1024} KB"
            : (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>"2 inline images (image001.png 41 KB, image002.png 18 KB); 1 file (plan.pdf 120 KB)".</summary>
    internal static string AttachedText(IReadOnlyList<Attachment> all)
    {
        static string Named(IEnumerable<Attachment> a) => string.Join(", ", a.Select(x => $"{(x.Name.Length > 0 ? x.Name : "(no name)")} {Size(x.Size)}"));
        var inline = all.Where(a => a.Inline).ToList();
        var files = all.Where(a => !a.Inline).ToList();
        var parts = new List<string>();
        if (inline.Count > 0)
            parts.Add($"{inline.Count} inline {(inline.All(a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) ? inline.Count == 1 ? "image" : "images" : inline.Count == 1 ? "item" : "items")} ({Named(inline)})");
        foreach (var (type, one, many) in new[] { ("fileAttachment", "file", "files"), ("itemAttachment", "attached item (an email or event)", "attached items"),
                     ("referenceAttachment", "link to a file", "links to files") })
        {
            var these = files.Where(a => a.Type == type || (type == "fileAttachment" && a.Type is not ("itemAttachment" or "referenceAttachment"))).ToList();
            if (these.Count > 0)
                parts.Add($"{these.Count} {(these.Count == 1 ? one : many)} ({Named(these)})");
        }
        if (parts.Count == 0)
            return "none";
        if (files.Count == 0)
            parts.Add("no files");
        return string.Join("; ", parts);
    }

    /// <summary>A short summary for the list and the records: "reply-all, 4 people (1 external), 2 inline images".</summary>
    private static string Counts(View v)
    {
        var people = v.Draft.People;
        var external = people.Count(p => p.External);
        var attached = v.Draft.Attachments.Count;
        return $"{Plural(people.Count, "person", "people")}" + (external > 0 ? $" ({external} external)" : "")
               + (attached > 0 ? $", {Plural(attached, "attachment")}" : "");
    }

    /// <summary>The preview: exactly what will go.</summary>
    internal static List<string> Preview(Context c, View v, bool full, bool? granted)
    {
        var d = v.Draft;
        var lines = new List<string> { "Would send this email from your mailbox (it can't be recalled once sent):" };
        void Field(string name, IEnumerable<string> values)
        {
            var first = true;
            foreach (var value in values)
            {
                lines.Add(first ? $"  {name,-9} {value}" : $"  {"",-9} {value}");
                first = false;
            }
            if (first)
                lines.Add($"  {name,-9} (none)");
        }
        IEnumerable<string> Shown(List<Person> people) =>
            people.Select(p => p.Shown + (p.Name is not { Length: > 0 } && !p.External ? " (no name: if it's a group, its members aren't counted)" : ""));
        var from = d.From is { Length: > 0 } f ? (d.FromName is { Length: > 0 } n && n != f ? $"{n} <{f}>" : f) : $"{c.Owner} (Exchange fills it in)";
        Field("From", [from]);
        Field("To", Shown(d.To));
        Field("Cc", Shown(d.Cc));
        Field("Bcc", Shown(d.Bcc));
        Field("Subject", [d.Subject]);
        Field("Kind", [v.KindText]);
        Field("Thread", [v.ThreadText]);
        var sendsAs = new List<string>
        {
            d.Importance.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "normal importance" : $"{d.Importance.ToUpperInvariant()} importance",
            d.ReadReceipt || d.DeliveryReceipt
                ? string.Join(" and ", new[] { d.ReadReceipt ? "READ" : null, d.DeliveryReceipt ? "DELIVERY" : null }.Where(x => x is not null)) + " RECEIPT requested"
                : "no read or delivery receipt",
            d.ReplyTo.Count > 0 ? $"REPLY-TO {string.Join(", ", d.ReplyTo.Select(p => p.Address))}" : "no reply-to",
        };
        Field("Sends as", [string.Join("; ", sendsAs)]);
        Field("Attached", [AttachedText(d.Attachments)]);
        Field("Flags", v.Flags.Count > 0 ? v.Flags : ["none"]);
        var written = v.Written.Count > 0 ? v.Written : [];
        lines.Add(v.Quoted.Count > 0 ? "  Body (what you wrote, then the quoted thread):" : "  Body:");
        foreach (var line in written)
            lines.Add(line.Trim().Length == 0 ? "    |" : $"    | {line}");
        if (v.Quoted.Count > 0)
        {
            if (written.Count > 0)
                lines.Add("    |");
            if (full)
                lines.AddRange(v.Quoted.Select(l => l.Trim().Length == 0 ? "    |" : $"    | {l}"));
            else
            {
                var head = string.Join("  ", v.Quoted.Where(l => !Furniture.IsMatch(l)).Take(2).Select(l => l.Trim()));
                lines.Add($"    | [quoted thread: {Plural(v.Quoted.Count, "line")}, from \"{PyStr.Head(head, 100)}\"; --full shows it]");
            }
        }
        Field("Permission", [granted switch
        {
            true => "Mail.Send is granted to this sign-in",
            false => "Mail.Send isn't granted to this sign-in: --yes would stop for a sign-in",
            null => "not checked (couldn't ask for a Mail.Send token silently)",
        }]);
        return lines;
    }

    // ---- the record -----------------------------------------------------------------------------------------------

    private static JsonObject LoadState(Settings settings) => StateFiles.LoadObject(settings.SentDraftsFile, () => []);

    /// <summary>A sent entry is kept KeepDays (60 days); a "sending" or "unconfirmed" one until it's settled.</summary>
    private static void SaveState(Settings settings, JsonObject state)
    {
        var cutoff = PyTime.Iso(Clock.Now - TimeSpan.FromDays(Triage.KeepDays), "seconds");
        foreach (var (k, value) in state.ToList())
            if (value is JsonObject o && o.Str("state") == "sent" && PyStr.CompareCodePoints(o.Str("when") ?? "", cutoff) < 0)
                state.Remove(k);
        StateFiles.Save(settings.SentDraftsFile, state);
    }

    private static JsonObject Entry(View v, string state) => new()
    {
        ["state"] = state, ["when"] = PyTime.Iso(Clock.Now, "seconds"), ["fingerprint"] = v.Fingerprint, ["graph_id"] = v.Draft.Id,
        ["message_id"] = v.Draft.InternetMessageId, ["subject"] = v.Draft.Subject, ["kind"] = v.Kind,
        ["to"] = J.Array(v.Draft.To.Select(p => p.Address)), ["cc"] = J.Array(v.Draft.Cc.Select(p => p.Address)),
        ["bcc"] = J.Array(v.Draft.Bcc.Select(p => p.Address)), ["external"] = v.Draft.People.Count(p => p.External),
        ["attachments"] = v.Draft.Attachments.Count, ["attachment_bytes"] = v.Draft.Attachments.Sum(a => a.Size),
    };

    /// <summary>"sent to 4 people (1 external), 2 attachments, reply-all": no addresses, no body.</summary>
    private static string Did(View v, string verb = "sent") => $"{verb} to {Counts(v)}, {v.Kind}";

    /// <summary>`deleted` lists it under "Emails sent" (never restorable); the field for its draft is `draft`, never `to`
    /// (which `restore` reads as "filed to").</summary>
    private static void RecordDeleted(Settings settings, View v, string did)
    {
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var items = record["items"] as JsonArray ?? throw new InvalidDataException($"{settings.DeletedFile} has no items list.");
        items.Add((JsonNode)new JsonObject
        {
            ["when"] = PyTime.Iso(Clock.Now, "microseconds"), ["folder"] = "Drafts", ["received"] = PyTime.Iso(Clock.Now, "seconds"),
            ["from"] = settings.Email, ["subject"] = v.Draft.Subject, ["rule"] = Heading, ["did"] = did, ["draft"] = v.Draft.Short,
        });
        StateFiles.Save(settings.DeletedFile, record);
    }

    /// <summary>An earlier unconfirmed send found in Sent Items: its `deleted` record says so now.</summary>
    private static void SettleDeleted(Settings settings, string shortId, string did)
    {
        var record = StateFiles.LoadObject(settings.DeletedFile, () => new JsonObject { ["items"] = new JsonArray() });
        var found = false;
        foreach (var item in record.Get("items").Objects().Where(i => i.Str("rule") == Heading && i.Str("draft") == shortId))
        {
            item["did"] = did;
            found = true;
        }
        if (found)
            StateFiles.Save(settings.DeletedFile, record);
    }

    // ---- where it is now ------------------------------------------------------------------------------------------

    internal enum Where
    {
        Sent,
        Outbox,
        Drafts,
        Gone,
        Unreadable,
    }

    /// <summary>Reads the message by its immutable id: no longer a draft means it went.</summary>
    private static (Where Where, JsonObject? Message) Locate(Context c, string id)
    {
        JsonObject m;
        try
        {
            m = c.Reader.Request($"/me/messages/{id}?$select=id,isDraft,parentFolderId,sentDateTime,internetMessageId", scopes: Mailbox.Scopes, headers: Immutable);
        }
        catch (GraphRequestException error) when ((int)error.Status is 404 or 410)
        {
            return (Where.Gone, null);
        }
        catch (Exception error) when (error is not SignInNeededException and not WriteRefusedException)
        {
            return (Where.Unreadable, null);
        }
        if (m.Str("parentFolderId") is { } folder && folder == c.Folder("outbox"))
            return (Where.Outbox, m);
        return (m.Has("isDraft") && !m.Truthy("isDraft") ? Where.Sent : Where.Drafts, m);
    }

    /// <summary>Up to four reads, five seconds apart, until it's no longer a draft.</summary>
    private static (Where Where, JsonObject? Message) ReadBack(Context c, string id, Action<TimeSpan> sleep)
    {
        (Where, JsonObject?) found = (Where.Unreadable, null);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
                sleep(TimeSpan.FromSeconds(5));
            found = Locate(c, id);
            if (found.Item1 == Where.Sent)
                break;
        }
        return found;
    }

    private static string Explain(Where where) => where switch
    {
        Where.Outbox => "it's in the Outbox",
        Where.Drafts => "it's still in Drafts (a slow send can't be ruled out)",
        Where.Gone => "it can't be found by its id (sent without a copy in Sent Items, or deleted)",
        _ => "it couldn't be read back",
    };

    private static string Folder(Context c, JsonObject? m) =>
        m?.Str("parentFolderId") is { } f && f == c.Folder("sentitems") ? "In Sent Items" : "Sent (no longer a draft)";

    // ---- the commands ---------------------------------------------------------------------------------------------

    /// <summary>A reference: a number from `drafts`, a short id, or a full Graph id. (graph id, short id, why not found).</summary>
    private static (string? Id, string? Short, string? Missing) Resolve(Context c, JsonObject state, string reference)
    {
        if (reference.Length > 0 && reference.All(char.IsAsciiDigit))
        {
            var n = int.Parse(reference, CultureInfo.InvariantCulture);
            var found = n < 1 ? null : DraftsFolder(c, "id,lastModifiedDateTime").Skip(n - 1).FirstOrDefault();
            return found is null ? (null, null, $"No draft numbered {reference}: take the number from a fresh `mail-triage drafts`.")
                : (found.Req("id"), ShortId(found.Req("id")), null);
        }
        if (PyRegex.Get("^[0-9a-f]{12}$").FullMatch(reference))
        {
            if (state[reference] is JsonObject known && known.Str("graph_id") is { } graphId)
                return (graphId, reference, null);
            var found = DraftsFolder(c, "id").FirstOrDefault(m => ShortId(m.Req("id")) == reference);
            return found is null ? (null, null, $"No draft has the id {reference}: run `mail-triage drafts --all`.") : (found.Req("id"), reference, null);
        }
        return (reference, state.ContainsKey(ShortId(reference)) ? ShortId(reference) : null, null);
    }

    /// <summary>`mail-triage drafts [--all] [--json]`: the Drafts folder, newest change first, numbered. Reads only.</summary>
    internal static int List(Settings settings, IGraph graph, bool all, bool json, CliHost host)
    {
        void Out(string text) => host.Out.WriteLine(Py.Lines(text));
        var c = new Context(settings, graph);
        var cutoff = Clock.Now - TimeSpan.FromDays(settings.SendDraft.ListDays);
        var state = LoadState(settings);
        var rows = new List<(int N, View View, string? WhyNot, string? State)>();
        var n = 0;
        foreach (var m in DraftsFolder(c))
        {
            n++;
            if (!all && (Local(m.Str("lastModifiedDateTime")) is not { } changed || changed < cutoff))
                break;
            var d = FromJson(c, m);
            try
            {
                d.Attachments = Attachments(c, d.Id);
            }
            catch (GraphRequestException)
            {
            }
            var v = Analyse(c, d);
            var prior = state[d.Short] as JsonObject;
            var whyNot = prior?.Str("state") is "sending" or "unconfirmed" ? "an earlier send may have gone: check Sent Items"
                : v.Refusals.Count > 0 ? string.Join("; ", v.Refusals) : null;
            rows.Add((n, v, whyNot, prior?.Str("state")));
        }
        if (json)
        {
            Out(PyJson.Dump(new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["n"] = r.N, ["id"] = r.View.Draft.Short, ["graph_id"] = r.View.Draft.Id, ["subject"] = r.View.Draft.Subject,
                ["modified"] = r.View.Draft.LastModified, ["to"] = r.View.Draft.To.Count, ["cc"] = r.View.Draft.Cc.Count, ["bcc"] = r.View.Draft.Bcc.Count,
                ["external"] = r.View.Draft.People.Count(p => p.External), ["attachments"] = r.View.Draft.Attachments.Count, ["kind"] = r.View.Kind,
                ["sendable"] = r.WhyNot is null, ["why_not"] = r.WhyNot, ["state"] = r.State,
            }).ToArray()), 2, false));
            return 0;
        }
        var span = all ? "" : $" changed in the last {settings.SendDraft.ListDays} days";
        if (rows.Count == 0)
        {
            Out($"No drafts{span}." + (all ? "" : " `mail-triage drafts --all` lists every draft."));
            return 0;
        }
        Out($"{Plural(rows.Count, "draft")}{span}:");
        foreach (var (number, v, whyNot, _) in rows)
        {
            var d = v.Draft;
            var attached = d.Attachments.Count == 0 ? "" : ", " + AttachedText(d.Attachments).Replace("; no files", "");
            Out($"  {number,2}. {d.Short}  {When(d.LastModified)}  {PyStr.Head(d.Subject.Length > 0 ? d.Subject : "(no subject)", 40),-24}  "
                + (whyNot is null ? $"{v.Kind}, {Plural(d.People.Count, "person", "people")}" + (d.People.Any(p => p.External) ? $" ({d.People.Count(p => p.External)} external)" : "") + attached
                    : $"can't be sent: {whyNot}"));
        }
        Out("`mail-triage send-draft <n>` shows exactly what would go (nothing is sent without --yes).");
        return 0;
    }

    /// <summary>`mail-triage send-draft <n|id> [--full] [--yes --seen HASH]`. 0: a preview, sent, or already sent; 1: the
    /// answer was lost and couldn't be settled, Microsoft refused it, or an earlier send may have gone; 2: refused before
    /// anything was sent.</summary>
    internal static int Run(Cli.Args args, Settings settings, IGraph graph, WriteDoor door, Action<string> log, CliHost host)
    {
        void Err(string text) => host.Err.WriteLine(Py.Lines(text));
        if (settings.Email is null)
        {
            Err($"send-draft needs email (or login_hint) in {settings.Path}: it only sends from your own address. Nothing sent.");
            return 2;
        }
        var yes = args.Flag("--yes");
        if (yes && args.Value("--seen") is null)
        {
            Err($"--yes needs --seen with the fingerprint its preview printed: run `mail-triage send-draft {args.Positional[0]}` first. Nothing sent.");
            return 2;
        }
        if (!yes)
            return Send(args, settings, graph, door, log, host);
        using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))   // the watcher can't act meanwhile
            return Send(args, settings, graph, door, log, host);
    }

    private static int Send(Cli.Args args, Settings settings, IGraph graph, WriteDoor door, Action<string> log, CliHost host)
    {
        void Out(string text) => host.Out.WriteLine(Py.Lines(text));
        void Err(string text) => host.Err.WriteLine(Py.Lines(text));
        var reference = args.Positional[0];
        var yes = args.Flag("--yes");
        var c = new Context(settings, graph);
        var state = LoadState(settings);
        var (id, shortId, missing) = Resolve(c, state, reference);
        if (missing is not null)
        {
            Err(missing + " Nothing sent.");
            return 2;
        }

        // A draft already sent, or whose send may have gone: settled by reading it, never sent again.
        int? Earlier(string key)
        {
            if (state[key] is not JsonObject prior)
                return null;
            var subject = prior.StrOr("subject");
            if (prior.Str("state") == "sent")
            {
                Out($"\"{subject}\" was sent on {prior.StrOr("when").Replace('T', ' ')}: nothing to do.");
                return 0;
            }
            var (where, message) = Locate(c, prior.Str("graph_id") ?? id!);
            if (where == Where.Sent)
            {
                prior["state"] = "sent";
                prior["settled"] = PyTime.Iso(Clock.Now, "seconds");
                SaveState(settings, state);
                UnconfirmedWrites.Clear(settings, UnconfirmedWrites.Key("send-draft", key), "it went: no longer a draft", log);
                SettleDeleted(settings, key, $"sent (its answer was lost; found {(Folder(c, message) == "In Sent Items" ? "in Sent Items" : "sent")} later)");
                log($"draft send settled: it went  {subject}");
                Out($"An earlier send of \"{subject}\" on {prior.StrOr("when").Replace('T', ' ')} went: it's no longer a draft"
                    + (Folder(c, message) == "In Sent Items" ? ", it's in Sent Items" : "") + ". Nothing to do: it isn't sent again.");
                return 0;
            }
            log($"send-draft skipped: an earlier send may have gone  {subject}");
            Err($"{MayHaveGone} An earlier send of \"{subject}\" on {prior.StrOr("when").Replace('T', ' ')} had its answer lost, and {Explain(where)}. "
                + "If it didn't go, send it from Outlook.");
            return 1;
        }
        if (shortId is not null && Earlier(shortId) is { } done)
            return done;

        Draft d;
        try
        {
            d = Read(c, id!);
        }
        catch (GraphRequestException error) when ((int)error.Status is 404 or 400)
        {
            Err($"No draft {reference} in your mailbox ({(int)error.Status}): take one from `mail-triage drafts`. Nothing sent.");
            return 2;
        }
        if (d.Short != shortId && Earlier(d.Short) is { } settled)
            return settled;
        var v = Analyse(c, d);

        var fingerprint = v.Fingerprint;
        var again = $"mail-triage send-draft {reference} --yes --seen {fingerprint}";
        if (v.Refusals.Count > 0 || !yes)
        {
            foreach (var line in Preview(c, v, args.Flag("--full"), yes ? null : graph.Granted(SendScopes)))
                Out(line);
            if (v.Refusals.Count > 0)
            {
                Err($"Can't send it: {string.Join("; ", v.Refusals)}. Nothing sent.");
                return 2;
            }
            Out($"Nothing sent. To send exactly this: {again}");
            return 0;
        }
        var seen = args.Value("--seen")!.Trim().ToLowerInvariant();
        if (seen != fingerprint)
        {
            Err($"Not sent: this isn't what that preview showed (--seen {seen}, it's {fingerprint} now): {Changed(seen, fingerprint)}. As it is now:");
            foreach (var line in Preview(c, v, args.Flag("--full"), null))
                Out(line);
            Out($"Nothing sent. To send exactly this: {again}");
            return 2;
        }

        var live = door.Mode == WriteMode.Live;
        var entry = Entry(v, "sending");
        var stateKey = d.Short;
        if (live)
        {
            state[stateKey] = entry;   // before the send: if its answer is lost, it's never sent again
            SaveState(settings, state);
        }
        void Forget()
        {
            if (!live)
                return;
            state.Remove(stateKey);
            SaveState(settings, state);
        }
        string? lost = null;
        try
        {
            door.Mail.SendDraft(d.Id);
        }
        catch (GraphRequestException error) when ((int)error.Status is >= 400 and < 500)
        {
            Forget();
            log($"draft send refused ({(int)error.Status})  {d.Subject}");
            Err($"Microsoft refused the send ({(int)error.Status}): nothing was sent. {PyStr.Head(error.Message, 200)}");
            return 1;
        }
        catch (Exception error) when (error is SignInNeededException or WriteRefusedException)
        {
            Forget();   // the token comes before the request, and a refused write was never made
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lost = $"{error.GetType().Name}: {error.Message}";
        }
        if (!live)
        {
            Out($"Journalled, not sent: \"{d.Subject}\", {Did(v, "to go").Replace("to go to ", "to ")}. Nothing recorded.");
            return 0;
        }

        var (where, back) = ReadBack(c, d.Id, host.Sleep);
        if (lost is not null && where != Where.Sent)
        {
            log($"draft send failed, may have gone: {d.Subject}: {lost}");
            entry["state"] = "unconfirmed";
            SaveState(settings, state);
            UnconfirmedWrites.Record(settings, UnconfirmedWrites.Key("send-draft", stateKey), new JsonObject
            {
                ["kind"] = "send-draft", ["command"] = "send-draft", ["verb"] = "send", ["event"] = null, ["subject"] = d.Subject,
                ["change"] = $"to {Plural(d.People.Count, "person", "people")}", ["message_id"] = d.InternetMessageId, ["until"] = null,
            }, lost, log);
            RecordDeleted(settings, v, $"MAY HAVE GONE: check Sent Items ({Did(v, "the send")}; the answer was lost, {Explain(where)}; not resent)");
            Activity.Write(settings, log, "send-draft", ActivityJournal.By.Hand, d.Subject, "by hand",
                $"may have gone: {Did(v, "sent")}; the answer was lost", d.InternetMessageId, null, unconfirmed: true);
            Err($"{MayHaveGone} Microsoft's answer was lost, and {Explain(where)}. If it didn't go, send it from Outlook.");
            return 1;
        }

        entry["state"] = "sent";
        SaveState(settings, state);
        var did = Did(v) + (lost is null ? "" : " (the answer was lost; found sent)");
        RecordDeleted(settings, v, did);
        Activity.Write(settings, log, "send-draft", ActivityJournal.By.Hand, d.Subject, "by hand", did, d.InternetMessageId, null);
        log($"draft sent  {d.Subject}: to {d.People.Count} ({d.People.Count(p => p.External)} external)");
        var to = $"to {Plural(d.People.Count, "person", "people")}" + (d.People.Any(p => p.External) ? $" ({d.People.Count(p => p.External)} external)" : "");
        if (lost is not null)
            Out($"The answer was lost, but it's no longer a draft: sent {to}. {Folder(c, back)}" + (back?.Str("sentDateTime") is { } at ? $", {When(at)}." : "."));
        else if (where == Where.Sent)
            Out($"Sent {to}. {Folder(c, back)}" + (back?.Str("sentDateTime") is { } at ? $", {When(at)}." : "."));
        else
            Out($"Sent {to}: Microsoft accepted it, but it isn't in Sent Items yet. Check Outlook.");
        return 0;
    }

    internal const string ListHelp = """
        mail-triage drafts [--all] [--json]
            Your Drafts folder, newest change first, numbered: changed in the last list_days ([send_draft], default 14);
            --all for every draft. Each line: number, short id (stable), last changed, subject, kind (new, reply,
            reply-all, forward), how many people (and how many outside internal_domains), attachments, or why it can't
            be sent. Numbers are positions, so take one from a fresh `drafts`. Reads only.
            --json: an array of n, id, graph_id, subject, modified, to, cc, bcc (counts), external, attachments, kind,
            sendable, why_not, state.
        """;

    internal const string SendHelp = """
        mail-triage send-draft <n | short id | Graph id> [--full]
        mail-triage send-draft <n | short id | Graph id> --yes --seen FINGERPRINT
            Without --yes: shows exactly what would go (from, to, cc, bcc, subject, kind, thread, attachments, flags,
            the body; --full shows the quoted thread too) and a fingerprint of it. Sends nothing, records nothing.
            With --yes --seen: reads the draft again and sends it, once, only if it's still exactly what that preview
            showed; otherwise it's refused and the new preview shown. Never sent twice; a send whose answer was lost
            is read back and, if that can't settle it, recorded and never sent again from here (MAY HAVE GONE: check
            Sent Items in Outlook). There is no undo: once sent, it's gone to people.
            Exit codes: 0 a preview, sent, or already sent; 1 the answer was lost, Microsoft refused it, an earlier
            send may have gone, or a sign-in is needed; 2 refused before anything was sent.
        """;
}
