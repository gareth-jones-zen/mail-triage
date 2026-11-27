using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using M365Graph;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>1.36.0: `drafts` lists the Drafts folder; `send-draft` previews a draft exactly, with a fingerprint, and sends it
/// once with `--yes --seen` that fingerprint, never twice and never again after a lost answer. The people and mail are made
/// up. "Now" is Fri 27 Nov 2026, 15:00 (UTC is local).</summary>
public sealed partial class SendDraftTests : IDisposable
{
    private const string SettingsText = """
        email = "alex@example.com"
        queue_file = "queue.json"
        state_file = "raised.json"
        deleted_file = "deleted.json"
        log_file = "log.txt"
        unconfirmed_file = "unconfirmed.json"
        sent_drafts_file = "sent-drafts.json"
        journal_file = "journal.jsonl"

        [[folder]]
        name = "Newsletters"

        """;

    private readonly TempDir dir = new();
    private Settings config;
    private readonly DraftGraph graph = new();
    private readonly DateTime now = new(2026, 11, 27, 15, 0, 0);

    public SendDraftTests() => config = dir.Settings(SettingsText);

    public void Dispose() => dir.Dispose();

    /// <summary>A mailbox with Drafts, Sent Items, the Outbox and the Inbox. Reads take immutable ids as they are; a send
    /// moves the draft to Sent Items (the same id, as with immutable ids), and can lose its answer before or after the move,
    /// or be refused.</summary>
    private sealed partial class DraftGraph : IGraph
    {
        public readonly List<JsonObject> Messages = [];
        public readonly Dictionary<string, List<JsonObject>> AttachmentsOf = [];
        public readonly Dictionary<string, string> TextOf = [];
        public readonly List<(string Url, IReadOnlyList<string>? Scopes, IReadOnlyDictionary<string, string>? Headers)> Sends = [];
        public readonly List<string> Reads = [];
        public bool? Granted { get; set; } = true;
        public bool Refuse { get; set; }
        public bool LoseBeforeMove { get; set; }
        public bool LoseAfterMove { get; set; }

        bool? IGraph.Granted(IReadOnlyList<string> scopes) => Granted;

        private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        private static Dictionary<string, string> Query(string url) =>
            url.Contains('?') ? url[(url.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Decode(p[0]), p => Decode(p[1])) : [];

        [GeneratedRegex("^conversationId eq '(.*)'$")]
        private static partial Regex ByConversation();

        public JsonObject? Find(string id) => Messages.FirstOrDefault(m => m.Str("id") == id);

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Reads.Add(url);
            Assert.Equal(SendDrafts.ImmutableId, headers?["Prefer"]);
            if (url.StartsWith("/me/mailFolders/drafts/messages?", StringComparison.Ordinal))
                return Messages.Where(m => m.Str("parentFolderId") == "drafts-id")
                    .OrderByDescending(m => m.StrOr("lastModifiedDateTime"), StringComparer.Ordinal).Select(m => (JsonObject)m.DeepClone()).ToList();
            if (url.Contains("/attachments?"))
                return AttachmentsOf.GetValueOrDefault(url.Split('/')[3], []).Select(a => (JsonObject)a.DeepClone()).ToList();
            if (url.StartsWith("/me/messages?", StringComparison.Ordinal))
            {
                var conversation = ByConversation().Match(Query(url)["$filter"]).Groups[1].Value;
                return Messages.Where(m => m.Str("conversationId") == conversation).Select(m => (JsonObject)m.DeepClone()).ToList();
            }
            throw new InvalidOperationException($"unexpected pages {url}");
        }

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            if (method != "GET")
                throw new InvalidProgramException($"{method} {url} went through the retrying Request");
            Reads.Add(url);
            Assert.StartsWith(SendDrafts.ImmutableId, headers?["Prefer"]);
            if (url.StartsWith("/me/mailFolders/", StringComparison.Ordinal))
                return new JsonObject { ["id"] = url.Split('/')[3].Split('?')[0] + "-id" };
            var id = url.Split('/')[3].Split('?')[0];
            var m = Find(id) ?? throw new GraphRequestException(HttpStatusCode.NotFound, method, url, "not found");
            var copy = (JsonObject)m.DeepClone();
            if (headers!["Prefer"].Contains("body-content-type=\"text\"", StringComparison.Ordinal))
                copy["body"] = new JsonObject { ["contentType"] = "text", ["content"] = TextOf.GetValueOrDefault(id, Notes.Text(m["body"])) };
            return copy;
        }

        public JsonObject RequestOnce(string url, string method, JsonNode? body, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Assert.Equal("POST", method);
            Assert.Null(body);
            Sends.Add((url, scopes, headers));
            if (Refuse)
                throw new GraphRequestException(HttpStatusCode.Forbidden, method, url, "ErrorAccessDenied");
            if (LoseBeforeMove)
                throw new GraphDeliveryUnknownException(method, url, HttpStatusCode.GatewayTimeout, "lost");
            var m = Find(url.Split('/')[3])!;
            m["isDraft"] = false;
            m["parentFolderId"] = "sentitems-id";
            m["sentDateTime"] = "2026-11-27T15:00:05Z";
            if (LoseAfterMove)
                throw new GraphDeliveryUnknownException(method, url, HttpStatusCode.GatewayTimeout, "lost");
            return [];
        }
    }

    private static JsonArray Recipients(params string[] people) => new(people.Select(p =>
    {
        var parts = p.Split('|');
        var address = new JsonObject { ["address"] = parts[0] };
        if (parts.Length > 1)
            address["name"] = parts[1];
        return (JsonNode)new JsonObject { ["emailAddress"] = address };
    }).ToArray());

    private JsonObject AddDraft(string id, string subject, string[] to, string[]? cc = null, string[]? bcc = null, string text = "Thanks both - yes, that works for me.",
        string conversation = "c1", string modified = "2026-11-27T14:02:00Z", string from = "alex@example.com")
    {
        var m = new JsonObject
        {
            ["id"] = id, ["subject"] = subject, ["isDraft"] = true, ["parentFolderId"] = "drafts-id", ["conversationId"] = conversation,
            ["internetMessageId"] = $"<{id}@mail.example.com>", ["lastModifiedDateTime"] = modified, ["changeKey"] = "ck1", ["hasAttachments"] = false,
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = from, ["name"] = "Alex Smith" } },
            ["toRecipients"] = Recipients(to), ["ccRecipients"] = Recipients(cc ?? []), ["bccRecipients"] = Recipients(bcc ?? []), ["replyTo"] = new JsonArray(),
            ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = "<html><body>" + string.Concat(text.Split('\n').Select(l => $"<p>{l}</p>")) + "</body></html>" },
            ["importance"] = "normal", ["isReadReceiptRequested"] = false, ["isDeliveryReceiptRequested"] = false,
        };
        graph.Messages.Add(m);
        return m;
    }

    private void AddReceived(string id, string from, string[] to, string[] cc, string received, string conversation = "c1", string subject = "Migration plan") =>
        graph.Messages.Add(new JsonObject
        {
            ["id"] = id, ["subject"] = subject, ["isDraft"] = false, ["parentFolderId"] = "inbox-id", ["conversationId"] = conversation,
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = from.Split('|')[0], ["name"] = from.Split('|').ElementAtOrDefault(1) } },
            ["toRecipients"] = Recipients(to), ["ccRecipients"] = Recipients(cc), ["receivedDateTime"] = received,
        });

    /// <summary>The design's example: a reply-all to four people, one outside, with two inline images.</summary>
    private void ReplyAll()
    {
        AddReceived("r1", "sam@example.com|Sam Jones", ["alex@example.com", "pat@partner.example"], ["kim@example.com|Kim Lee", "jo@example.com|Jo Park"],
            "2026-11-27T11:15:00Z");
        AddDraft("d1", "RE: Migration plan", ["sam@example.com|Sam Jones", "pat@partner.example"], ["kim@example.com|Kim Lee", "jo@example.com|Jo Park"],
            text: "Thanks both - yes, that works for me.\n\n________________________________\nFrom: Sam Jones\nSent: 27 November 2026 11:15\nTo: Alex Smith\nSubject: Migration plan\n\nCan we go with plan B?");
        graph.AttachmentsOf["d1"] =
        [
            new JsonObject { ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = "image001.png", ["contentType"] = "image/png", ["size"] = 41_000L, ["isInline"] = true },
            new JsonObject { ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = "image002.png", ["contentType"] = "image/png", ["size"] = 18_000L, ["isInline"] = true },
        ];
    }

    private (int Code, string Out) Cli(params string[] args)
    {
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now, TimeZoneInfo.Utc)))
            code = MailTriage.Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), .. args],
                new CliHost { Out = output, Err = output, Echo = false, Graph = (_, _) => graph, Sleep = _ => { } });
        return (code, output.ToString());
    }

    private static string Seen(string preview) => Regex.Matches(preview, "--seen ([0-9a-f]{12})")[^1].Groups[1].Value;

    private JsonObject State => StateFiles.LoadObject(config.SentDraftsFile, () => []);
    private JsonObject Unconfirmed => StateFiles.LoadObject(config.UnconfirmedFile, () => []);
    private string JournalText => File.Exists(config.JournalFile!) ? File.ReadAllText(config.JournalFile!) : "";
    private JsonArray DeletedJson => (JsonArray)PyJson.Parse(Cli("deleted", "--json").Out)!;

    [Fact]
    public void A_preview_shows_everything_and_sends_nothing()
    {
        ReplyAll();
        var (code, preview) = Cli("send-draft", "1");
        Assert.Equal(0, code);
        Assert.Contains("Would send this email from your mailbox (it can't be recalled once sent):", preview);
        Assert.Contains("  From      Alex Smith <alex@example.com>", preview);
        Assert.Contains("  To        Sam Jones <sam@example.com>\n            pat@partner.example (external)", preview.Replace("\r\n", "\n"));
        Assert.Contains("  Cc        Kim Lee <kim@example.com>\n            Jo Park <jo@example.com>", preview.Replace("\r\n", "\n"));
        Assert.Contains("  Bcc       (none)", preview);
        Assert.Contains("  Subject   RE: Migration plan", preview);
        Assert.Contains("  Kind      REPLY-ALL: the same 4 people as the last message in the thread (from Sam Jones, Fri 27 Nov 11:15)", preview);
        Assert.Contains("  Thread    nothing newer in the thread since this draft was last changed (Fri 27 Nov 14:02)", preview);
        Assert.Contains("  Sends as  normal importance; no read or delivery receipt; no reply-to", preview);
        Assert.Contains("  Attached  2 inline images (image001.png 40 KB, image002.png 18 KB); no files", preview);   // hasAttachments is false
        Assert.Contains("  Flags     1 EXTERNAL recipient", preview);
        Assert.Contains("    | Thanks both - yes, that works for me.", preview);
        Assert.Contains("[quoted thread: 7 lines, from \"From: Sam Jones  Sent: 27 November 2026 11:15\"; --full shows it]", preview);
        Assert.DoesNotContain("Can we go with plan B?", preview);
        Assert.Contains("Can we go with plan B?", Cli("send-draft", "1", "--full").Out);
        Assert.Contains("  Permission Mail.Send is granted to this sign-in", preview);
        Assert.Matches("Nothing sent. To send exactly this: mail-triage send-draft 1 --yes --seen [0-9a-f]{12}", preview);
        Assert.Empty(graph.Sends);
        Assert.False(File.Exists(config.SentDraftsFile));
        Assert.False(File.Exists(config.DeletedFile));
        Assert.Equal("", JournalText);

        graph.Granted = false;
        Assert.Contains("Mail.Send isn't granted to this sign-in: --yes would stop for a sign-in", Cli("send-draft", "1").Out);
    }

    [Fact]
    public void Yes_without_seen_is_refused()
    {
        ReplyAll();
        var (code, output) = Cli("send-draft", "1", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("--yes needs --seen", output);
        Assert.Empty(graph.Sends);
    }

    [Fact]
    public void Yes_with_the_fingerprint_sends_once_and_records()
    {
        ReplyAll();
        var seen = Seen(Cli("send-draft", "1").Out);
        var (code, output) = Cli("send-draft", "1", "--yes", "--seen", seen);
        Assert.Equal(0, code);
        Assert.Contains("Sent to 4 people (1 external). In Sent Items, Fri 27 Nov 15:00.", output);
        var (url, scopes, headers) = Assert.Single(graph.Sends);
        Assert.Equal("/me/messages/d1/send", url);
        Assert.Equal(["Mail.Send"], scopes);
        Assert.Equal(SendDrafts.ImmutableId, headers!["Prefer"]);
        Assert.Equal("0", headers["Content-Length"]);

        var entry = (JsonObject)State[SendDrafts.ShortId("d1")]!;
        Assert.Equal(("sent", "reply-all", seen, "d1"), (entry.Str("state"), entry.Str("kind"), entry.Str("fingerprint"), entry.Str("graph_id")));
        Assert.Equal(["sam@example.com", "pat@partner.example"], entry["to"].Strings());
        Assert.DoesNotContain("Thanks both", File.ReadAllText(config.SentDraftsFile));   // never the body

        var record = (JsonObject)Assert.Single(DeletedJson)!;
        Assert.Equal(("Emails sent", "sent to 4 people (1 external), 2 attachments, reply-all", false), (record.Str("rule"), record.Str("did"), (bool)record["restorable"]!));
        Assert.Equal("it was sent, not deleted: a sent email can't be brought back", record.Str("why_not"));
        Assert.DoesNotContain("sam@example.com", File.ReadAllText(config.DeletedFile));
        var (restore, said) = Cli("restore", "1", "--yes");
        Assert.Equal(2, restore);
        Assert.Contains("a sent email can't be brought back", said);

        var line = (JsonObject)PyJson.Parse(Assert.Single(JournalText.Split('\n', StringSplitOptions.RemoveEmptyEntries)))!;
        Assert.Equal(("send-draft", "hand", "RE: Migration plan", "<d1@mail.example.com>", "private"),
            (line.Str("what"), line.Str("by"), line.Str("subject"), line.Str("item"), line.Str("sensitivity")));
        Assert.Null(line["undo"]);
        Assert.DoesNotContain("sam@example.com", JournalText);
    }

    [Theory]
    [InlineData("body", "the body")]
    [InlineData("subject", "the subject or how it's sent")]
    [InlineData("recipient", "the recipients")]
    [InlineData("attachment", "the attachments")]
    public void An_edit_after_the_preview_is_refused(string what, string named)
    {
        ReplyAll();
        var seen = Seen(Cli("send-draft", "1").Out);
        var d = graph.Find("d1")!;
        switch (what)
        {
            case "body": d["body"]!["content"] = "<p>Actually, no.</p>"; break;
            case "subject": d["subject"] = "RE: Migration plan (v2)"; break;
            case "recipient": ((JsonArray)d["ccRecipients"]!).RemoveAt(1); break;
            case "attachment": graph.AttachmentsOf["d1"].Add(new JsonObject { ["name"] = "plan.pdf", ["contentType"] = "application/pdf", ["size"] = 9000L, ["isInline"] = false }); break;
        }
        var (code, output) = Cli("send-draft", "1", "--yes", "--seen", seen);
        Assert.Equal(2, code);
        Assert.Contains($"Not sent: this isn't what that preview showed (--seen {seen}", output);
        Assert.Contains($"changed: {named}.", output);
        Assert.NotEqual(seen, Seen(output));
        Assert.Empty(graph.Sends);
        Assert.False(File.Exists(config.SentDraftsFile));
    }

    [Fact]
    public void A_resave_with_nothing_changed_still_sends()
    {
        ReplyAll();
        var seen = Seen(Cli("send-draft", "1").Out);
        graph.Find("d1")!["changeKey"] = "ck2";
        graph.Find("d1")!["lastModifiedDateTime"] = "2026-11-27T14:30:00Z";
        Assert.Equal(seen, Seen(Cli("send-draft", "1").Out));
        Assert.Equal(0, Cli("send-draft", "1", "--yes", "--seen", seen).Code);
        Assert.Single(graph.Sends);
    }

    [Fact]
    public void The_same_draft_is_never_sent_twice()
    {
        ReplyAll();
        var shortId = SendDrafts.ShortId("d1");
        var seen = Seen(Cli("send-draft", "1").Out);
        Assert.Equal(0, Cli("send-draft", shortId, "--yes", "--seen", seen).Code);
        var (code, output) = Cli("send-draft", shortId, "--yes", "--seen", seen);
        Assert.Equal(0, code);
        Assert.Contains("\"RE: Migration plan\" was sent on 2026-11-27 15:00:00: nothing to do.", output);
        Assert.Equal(0, Cli("send-draft", "d1", "--yes", "--seen", seen).Code);   // by Graph id too: nothing to do
        Assert.Single(graph.Sends);
    }

    [Fact]
    public void A_lost_answer_that_went_is_settled_by_the_read()
    {
        ReplyAll();
        graph.LoseAfterMove = true;
        var (code, output) = Cli("send-draft", "1", "--yes", "--seen", Seen(Cli("send-draft", "1").Out));
        Assert.Equal(0, code);
        Assert.Contains("The answer was lost, but it's no longer a draft: sent to 4 people (1 external). In Sent Items", output);
        Assert.Single(graph.Sends);
        Assert.Equal("sent", State[SendDrafts.ShortId("d1")].Str("state"));
        Assert.Empty(Unconfirmed);
    }

    [Fact]
    public void A_lost_answer_that_cant_be_settled_is_unconfirmed_and_never_resent()
    {
        ReplyAll();
        var shortId = SendDrafts.ShortId("d1");
        graph.LoseBeforeMove = true;
        var seen = Seen(Cli("send-draft", "1").Out);
        var (code, output) = Cli("send-draft", "1", "--yes", "--seen", seen);
        Assert.Equal(1, code);
        Assert.Contains("MAY HAVE GONE: check Sent Items in Outlook; this tool won't send it again.", output);
        Assert.Contains("it's still in Drafts", output);
        Assert.Equal("unconfirmed", State[shortId].Str("state"));
        Assert.Equal("send-draft", Unconfirmed[$"send-draft:{shortId}"].Str("kind"));
        Assert.StartsWith("MAY HAVE GONE: check Sent Items", ((JsonObject)Assert.Single(DeletedJson)!).Str("did"));
        Assert.Contains("\"outcome\":\"unconfirmed\"", JournalText);

        // Never again from here: the preview and --yes both say so, and nothing is sent.
        graph.LoseBeforeMove = false;
        foreach (var args in new[] { new[] { "send-draft", "1" }, ["send-draft", shortId, "--yes", "--seen", seen] })
        {
            (code, output) = Cli(args);
            Assert.Equal(1, code);
            Assert.Contains("MAY HAVE GONE", output);
        }
        Assert.Single(graph.Sends);
        Assert.Contains("unrecognized arguments: --again", Cli("send-draft", shortId, "--again", "--yes", "--seen", seen).Out);   // there is no --again

        // It turns up in Sent Items (a slow send): the next one settles it.
        graph.Find("d1")!["isDraft"] = false;
        graph.Find("d1")!["parentFolderId"] = "sentitems-id";
        (code, output) = Cli("send-draft", shortId);
        Assert.Equal(0, code);
        Assert.Contains("went: it's no longer a draft, it's in Sent Items. Nothing to do", output);
        Assert.Empty(Unconfirmed);
        Assert.Equal("sent", State[shortId].Str("state"));
        Assert.StartsWith("sent (its answer was lost; found in Sent Items later)", ((JsonObject)Assert.Single(DeletedJson)!).Str("did"));
        Assert.Single(graph.Sends);
    }

    [Fact]
    public void A_4xx_sends_nothing_and_forgets()
    {
        ReplyAll();
        graph.Refuse = true;
        var seen = Seen(Cli("send-draft", "1").Out);
        var (code, output) = Cli("send-draft", "1", "--yes", "--seen", seen);
        Assert.Equal(1, code);
        Assert.Contains("Microsoft refused the send (403): nothing was sent.", output);
        Assert.Empty(State);
        Assert.False(File.Exists(config.DeletedFile));
        graph.Refuse = false;
        Assert.Equal(0, Cli("send-draft", "1", "--yes", "--seen", seen).Code);
        Assert.Equal(2, graph.Sends.Count);
    }

    [Theory]
    [InlineData("not in Drafts", "it isn't in the Drafts folder")]
    [InlineData("not a draft", "it isn't a draft")]
    [InlineData("no recipients", "it has no recipients")]
    [InlineData("empty subject", "its subject is empty")]
    [InlineData("empty body", "its body is empty")]
    [InlineData("nothing written", "nothing is written above the quoted thread")]
    [InlineData("meeting", "it's a meeting message")]
    [InlineData("other from", "it's from boss@example.com, not you")]
    [InlineData("too many", "more than max_recipients (50)")]
    public void Each_refusal_sends_nothing(string what, string said)
    {
        var d = AddDraft("d1", "A question", ["sam@example.com"]);
        switch (what)
        {
            case "not in Drafts": d["parentFolderId"] = "inbox-id"; break;
            case "not a draft": d["isDraft"] = false; break;
            case "no recipients": d["toRecipients"] = new JsonArray(); break;
            case "empty subject": d["subject"] = " "; break;
            case "empty body": d["body"]!["content"] = "<p> </p>"; break;
            case "nothing written": d["body"]!["content"] = "<p></p><p>From: Sam Jones</p><p>Sent: 27 November 2026 11:15</p><p>Hello</p>"; break;
            case "meeting": d["@odata.type"] = "#microsoft.graph.eventMessageResponse"; break;
            case "other from": d["from"]!["emailAddress"]!["address"] = "boss@example.com"; break;
            case "too many": d["toRecipients"] = Recipients(Enumerable.Range(1, 51).Select(i => $"p{i}@example.com").ToArray()); break;
        }
        var reference = what is "not in Drafts" or "not a draft" ? "d1" : "1";
        foreach (var args in new[] { new[] { "send-draft", reference }, ["send-draft", reference, "--yes", "--seen", "000000000000"] })
        {
            var (code, output) = Cli(args);
            Assert.Equal(2, code);
            Assert.Contains("Can't send it: ", output);
            Assert.Contains(said, output);
        }
        Assert.Empty(graph.Sends);
    }

    [Fact]
    public void The_flags_external_by_internal_domains_many_people_a_gap_and_a_newer_message()
    {
        File.WriteAllText(Path.Combine(dir.Path, "config.toml"), SettingsText + """
            [send_draft]
            internal_domains = ["example.com", "example.co.uk", "exampledev.onmicrosoft.com"]
            warn_recipients = 3

            """);
        config = dir.Settings(File.ReadAllText(Path.Combine(dir.Path, "config.toml")));
        AddReceived("r1", "sam@example.com|Sam Jones", ["alex@example.com"], [], "2026-11-27T11:15:00Z");
        AddReceived("r2", "kim@example.co.uk|Kim Lee", ["alex@example.com"], [], "2026-11-27T14:30:00Z");
        AddDraft("d1", "RE: Migration plan", ["sam@example.com", "kim@example.co.uk", "room@exampledev.onmicrosoft.com", "pat@sub.example.com"],
            text: "Yes, from [date] — TBC.");
        var output = Cli("send-draft", "1").Out;
        Assert.Contains("pat@sub.example.com (external)", output);   // a subdomain is outside
        Assert.DoesNotContain("kim@example.co.uk (external)", output);
        Assert.Contains("1 EXTERNAL recipient", output);
        Assert.Contains("4 PEOPLE: more than 3", output);
        Assert.Contains("CHECK THE TEXT: \"[date]\"", output);
        Assert.Contains("CHECK THE TEXT: \"TBC\"", output);
        Assert.Contains("CHECK THE TEXT: \"—\"", output);
        Assert.Contains("1 NEWER MESSAGE in the thread since this draft was last changed", output);
        Assert.Contains("  Thread    1 NEWER MESSAGE since this draft was last changed (Fri 27 Nov 14:02): from Kim Lee, Fri 27 Nov 14:30", output);
    }

    [Theory]
    [InlineData("reply", "RE: Plan", "sam@example.com", "reply to Sam Jones only, the last message in the thread (from Sam Jones, Fri 27 Nov 11:15)")]
    [InlineData("reply-all", "RE: Plan", "sam@example.com,kim@example.com", "REPLY-ALL: the same 2 people as the last message")]
    [InlineData("reply-all dropping", "RE: Plan", "sam@example.com,kim@example.com,lee@example.com", "REPLY-ALL to the last message in the thread (from Sam Jones, Fri 27 Nov 11:15); ADDS lee@example.com; DROPS jo@example.com")]
    [InlineData("forward", "FW: Plan", "new@example.org", "forward of the thread's last message (from Sam Jones, Fri 27 Nov 11:15)")]
    [InlineData("new", "Plan", "sam@example.com", "new message (nothing else in its thread)")]
    [InlineData("can't tell", "RE: Plan", "zed@example.com", "can't tell: none of these people were on the last message")]
    public void Kind_is_worked_out_from_the_thread(string what, string subject, string to, string kind)
    {
        if (what != "new")
            AddReceived("r1", "sam@example.com|Sam Jones", ["alex@example.com"],
                what == "reply" ? [] : what == "reply-all dropping" ? ["kim@example.com", "jo@example.com"] : ["kim@example.com"], "2026-11-27T11:15:00Z");
        AddDraft("d1", subject, to.Split(','), conversation: "c1");
        Assert.Contains($"  Kind      {kind}", Cli("send-draft", "1").Out);
    }

    [Fact]
    public void A_journal_run_sends_nothing_and_records_nothing()
    {
        ReplyAll();
        var (code, output) = Cli("--writes", "journal", "--journal", Path.Combine(dir.Path, "writes.log"), "send-draft", "1", "--yes", "--seen",
            Seen(Cli("send-draft", "1").Out));
        Assert.Equal(0, code);
        Assert.Contains("Journalled, not sent", output);
        Assert.Contains("POST /me/messages/d1/send", File.ReadAllText(Path.Combine(dir.Path, "writes.log")));
        Assert.Empty(graph.Sends);
        Assert.False(File.Exists(config.SentDraftsFile));
        Assert.False(File.Exists(config.DeletedFile));
        Assert.Equal("", JournalText);
    }

    [Fact]
    public void Drafts_lists_numbers_stable_ids_and_why_one_cant_be_sent()
    {
        Assert.Equal("[]", Cli("drafts", "--json").Out.Trim());
        Assert.Contains("No drafts changed in the last 14 days.", Cli("drafts").Out);
        ReplyAll();
        AddDraft("d2", "Notes for next week", [], modified: "2026-11-26T09:40:00Z", conversation: "c2");
        AddDraft("d3", "Old one", ["sam@example.com"], modified: "2026-10-01T09:40:00Z", conversation: "c3");
        var output = Cli("drafts").Out;
        Assert.Contains("2 drafts changed in the last 14 days:", output);
        Assert.Contains($"   1. {SendDrafts.ShortId("d1")}  Fri 27 Nov 14:02  RE: Migration plan        reply-all, 4 people (1 external), 2 inline images", output);
        Assert.Contains($"   2. {SendDrafts.ShortId("d2")}  Thu 26 Nov 09:40  Notes for next week       can't be sent: it has no recipients", output);
        Assert.DoesNotContain("Old one", output);
        Assert.Contains("   3. ", Cli("drafts", "--all").Out);
        var json = (JsonArray)PyJson.Parse(Cli("drafts", "--json").Out)!;
        Assert.Equal(2, json.Count);
        var first = (JsonObject)json[0]!;
        Assert.Equal((1L, SendDrafts.ShortId("d1"), "d1", 2L, 2L, 1L, 2L, "reply-all", true),
            ((long)first["n"]!, first.Str("id"), first.Str("graph_id"), (long)first["to"]!, (long)first["cc"]!, (long)first["external"]!, (long)first["attachments"]!,
                first.Str("kind"), (bool)first["sendable"]!));
        Assert.Equal((false, "it has no recipients (to, cc or bcc)"), ((bool)json[1]!["sendable"]!, json[1].Str("why_not")));
        Assert.Matches("^[0-9a-f]{12}$", first.Str("id")!);
        Assert.Equal(first.Str("id"), ((JsonArray)PyJson.Parse(Cli("drafts", "--json").Out)!)[0].Str("id"));   // stable
        Assert.Contains("can't be recalled", Cli("send-draft", SendDrafts.ShortId("d2")).Out);   // by its short id
        Assert.Equal(2, Cli("send-draft", "9").Code);
        Assert.Equal(2, Cli("send-draft", "aaaaaaaaaaaa").Code);
    }

    [Fact]
    public void The_fingerprint_ignores_order_and_case_and_covers_each_field()
    {
        SendDrafts.Draft Make(Action<SendDrafts.Draft>? change = null, List<SendDrafts.Person>? to = null)
        {
            var d = new SendDrafts.Draft
            {
                Id = "d1", Subject = "S", From = "alex@example.com", BodyType = "html", BodyContent = "<p>x</p>",
                To = to ?? [new("a@example.com", null, false), new("b@example.com", null, false)],
                Attachments = [new("a.png", 10, "image/png", true, "fileAttachment")],
            };
            change?.Invoke(d);
            return d;
        }
        var base_ = SendDrafts.Fingerprint(Make());
        Assert.Matches("^[0-9a-f]{12}$", base_);
        Assert.Equal(base_, SendDrafts.Fingerprint(Make(to: [new("B@Example.com", "B", false), new("a@example.com", null, false)])));
        var changes = new Action<SendDrafts.Draft>[]
        {
            d => d.Cc.Add(new("c@example.com", null, false)), d => d.Bcc.Add(new("c@example.com", null, false)), d => d.ReplyTo.Add(new("c@example.com", null, false)),
            d => d.Attachments = [new("a.png", 11, "image/png", true, "fileAttachment")], d => d.Attachments = [new("a.png", 10, "image/png", false, "fileAttachment")],
        };
        foreach (var change in changes)
            Assert.NotEqual(base_, SendDrafts.Fingerprint(Make(change)));
        foreach (var other in new[]
                 {
                     new SendDrafts.Draft { Id = "d2", Subject = "S", From = "alex@example.com", BodyType = "html", BodyContent = "<p>x</p>", To = Make().To, Attachments = Make().Attachments },
                     new SendDrafts.Draft { Id = "d1", Subject = "T", From = "alex@example.com", BodyType = "html", BodyContent = "<p>x</p>", To = Make().To, Attachments = Make().Attachments },
                     new SendDrafts.Draft { Id = "d1", Subject = "S", From = "alex@example.com", BodyType = "html", BodyContent = "<p>y</p>", To = Make().To, Attachments = Make().Attachments },
                     new SendDrafts.Draft { Id = "d1", Subject = "S", From = "alex@example.com", BodyType = "text", BodyContent = "<p>x</p>", To = Make().To, Attachments = Make().Attachments },
                     new SendDrafts.Draft { Id = "d1", Subject = "S", From = "alex@example.com", BodyType = "html", BodyContent = "<p>x</p>", To = Make().To, Attachments = Make().Attachments, Importance = "high" },
                     new SendDrafts.Draft { Id = "d1", Subject = "S", From = "alex@example.com", BodyType = "html", BodyContent = "<p>x</p>", To = Make().To, Attachments = Make().Attachments, ReadReceipt = true },
                     new SendDrafts.Draft { Id = "d1", Subject = "S", From = "alex@example.com", BodyType = "html", BodyContent = "<p>x</p>", To = Make().To, Attachments = Make().Attachments, DeliveryReceipt = true },
                 })
            Assert.NotEqual(base_, SendDrafts.Fingerprint(other!));
    }

    [Theory]
    [InlineData("[send_draft]\nwarn_recipient = 3\n", "has 'warn_recipient'")]
    [InlineData("[send_draft]\nwarn = ['(unclosed']\n", "isn't a valid regular expression")]
    [InlineData("[send_draft]\nwarn_recipients = 20\nmax_recipients = 10\n", "is below warn_recipients")]
    [InlineData("[send_draft]\ninternal_domains = ['@example.com']\n", "should be a list of domains")]
    public void Bad_send_draft_settings_are_an_error(string extra, string said)
    {
        var error = Assert.Throws<SettingsException>(() => dir.Settings(SettingsText + extra));
        Assert.Contains(said, error.Message);
    }

    [Fact]
    public void Settings_take_a_list_of_internal_domains()
    {
        var s = dir.Settings(SettingsText + "[send_draft]\ninternal_domains = ['Example.com', 'example.co.uk', 'exampledev.onmicrosoft.com']\nlist_days = 7\n");
        Assert.Equal(["example.com", "example.co.uk", "exampledev.onmicrosoft.com"], s.SendDraft.InternalDomains);
        Assert.Equal((10L, 50L, 7L, 3), (s.SendDraft.WarnRecipients, s.SendDraft.MaxRecipients, s.SendDraft.ListDays, s.SendDraft.Warn.Count));
        Assert.Equal(Path.Combine(dir.Path, "sent-drafts.json"), s.SentDraftsFile);
    }

    [Fact]
    public void Each_command_prints_its_help_without_settings()
    {
        var output = new StringWriter();
        Assert.Equal(0, MailTriage.Cli.Run(["--config", Path.Combine(dir.Path, "missing.toml"), "send-draft", "--help"], new CliHost { Out = output, Err = output }));
        Assert.Contains("--yes --seen FINGERPRINT", output.ToString());
        output = new StringWriter();
        Assert.Equal(0, MailTriage.Cli.Run(["drafts", "--help"], new CliHost { Out = output, Err = output }));
        Assert.Contains("mail-triage drafts [--all] [--json]", output.ToString());
    }

    /// <summary>Through the real adapter and M365Graph: the send carries Content-Length: 0 and the immutable-id header, and a
    /// 5xx is never resent.</summary>
    [Fact]
    public void The_send_has_content_length_zero_and_is_sent_once()
    {
        var seen = new List<(string Line, long? Length, string? Prefer)>();
        var answers = new Queue<HttpStatusCode>([HttpStatusCode.Accepted, HttpStatusCode.BadGateway, HttpStatusCode.OK]);
        var client = new GraphClient(new GraphClientOptions { Log = _ => { } }, new Handler(r =>
            {
                seen.Add(($"{r.Method} {r.RequestUri!.AbsolutePath}", r.Content?.Headers.ContentLength,
                    r.Headers.TryGetValues("Prefer", out var p) ? string.Join(",", p) : null));
                return new HttpResponseMessage(answers.Dequeue()) { Content = new StringContent("") };
            }), token: (_, _) => Task.FromResult("t"), delay: (_, _) => Task.CompletedTask);
        var door = WriteDoor.Live(new M365GraphAdapter(client));
        door.Mail.SendDraft("m1");
        Assert.Equal(("POST /v1.0/me/messages/m1/send", (long?)0, SendDrafts.ImmutableId), seen[0]);
        Assert.Throws<GraphDeliveryUnknownException>(() => door.Mail.SendDraft("m1"));
        Assert.Equal(2, seen.Count);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => Task.FromResult(answer(request));
    }
}
