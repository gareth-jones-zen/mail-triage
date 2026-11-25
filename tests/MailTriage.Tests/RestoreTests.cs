using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;
using WinBackground;

namespace MailTriage.Tests;

/// <summary>1.32.0: `restore` moves an email mail-triage deleted back to the Inbox, once you've seen the preview, and no
/// rule deletes it again; `deleted` numbers its records and says which can be restored (--json for the UI). The emails are
/// made up. "Now" is Mon 23 Nov 2026, 09:00.</summary>
public sealed partial class RestoreTests : IDisposable
{
    private const string SettingsText = """
        queue_file = "queue.json"
        state_file = "raised.json"
        deleted_file = "deleted.json"
        log_file = "log.txt"
        unconfirmed_file = "unconfirmed.json"

        [[folder]]
        name = "Newsletters"

        [[folder]]
        name = "Inbox"
        unmatched = "leave"

        [[rule]]
        name = "News"
        sender = '@news\.example\.com$'
        action = "delete"
        folders = ["Newsletters", "Inbox"]

        """;

    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly MailGraph graph = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);

    public RestoreTests() => config = dir.Settings(SettingsText);

    public void Dispose() => dir.Dispose();

    /// <summary>A mailbox with Newsletters, the Inbox and Deleted Items. A move takes the message to its destination with
    /// a new id (as Graph's does), and can be made to lose its answer, having gone or not.</summary>
    private sealed partial class MailGraph : IGraph
    {
        public readonly Dictionary<string, List<JsonObject>> Folders = new() { ["newsletters"] = [], ["inbox"] = [], ["deleteditems"] = [] };
        public readonly List<(string Method, string Url, JsonNode? Body)> Calls = [];
        public bool LoseMoves { get; set; }
        public bool LostMovesGo { get; set; } = true;
        private int moves;

        public List<(string Method, string Url, JsonNode? Body)> Moves => Calls.Where(c => c.Url.EndsWith("/move", StringComparison.Ordinal)).ToList();

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add(("GET", url, null));
            if (url.StartsWith("/me/mailFolders?", StringComparison.Ordinal))
                return [new JsonObject { ["id"] = "newsletters", ["displayName"] = "Newsletters", ["childFolderCount"] = 0 },
                        new JsonObject { ["id"] = "inbox", ["displayName"] = "Inbox", ["childFolderCount"] = 0 },
                        new JsonObject { ["id"] = "deleteditems", ["displayName"] = "Deleted Items", ["childFolderCount"] = 0 }];
            var folder = url.Split('/')[3];
            var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => Decode(p[0]), p => Decode(p[1])) : [];
            IEnumerable<JsonObject> found = Folders[folder];
            if (query.GetValueOrDefault("$filter") is { } filter)
            {
                if (ById().Match(filter) is { Success: true } byKey)
                    found = found.Where(m => m.Str("internetMessageId") == byKey.Groups[1].Value.Replace("''", "'"));
                else if (ByTime().Match(filter) is { Success: true } byTime)
                    found = found.Where(m => m.Str("receivedDateTime") == byTime.Groups[1].Value);
            }
            return found.Select(m => (JsonObject)m.DeepClone()).ToList();
        }

        private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        [GeneratedRegex("^internetMessageId eq '(.*)'$")]
        private static partial Regex ById();

        [GeneratedRegex(@"^receivedDateTime eq (\S+)$")]
        private static partial Regex ByTime();

        private (List<JsonObject> Folder, JsonObject Message)? Locate(string id) =>
            Folders.Values.SelectMany(f => f.Select(m => (f, m))).Where(x => x.m.Str("id") == id).Select(x => ((List<JsonObject>, JsonObject)?)x).FirstOrDefault();

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add((method, url, body?.DeepClone()));
            var id = url.Split('/')[3].Split('?')[0];
            var at = Locate(id);
            if (method == "PATCH" && at is { } patched)
            {
                patched.Message["isRead"] = body!["isRead"]!.GetValue<bool>();
                return [];
            }
            if (method == "POST" && url.EndsWith("/move", StringComparison.Ordinal) && at is { } moving)
            {
                if (LoseMoves && !LostMovesGo)
                    throw new M365Graph.GraphDeliveryUnknownException(method, url, HttpStatusCode.GatewayTimeout, "lost");
                moving.Folder.Remove(moving.Message);
                var moved = (JsonObject)moving.Message.DeepClone();
                moved["id"] = $"{id}~{++moves}";
                Folders[body!["destinationId"]!.GetValue<string>()].Add(moved);
                if (LoseMoves)
                    throw new M365Graph.GraphDeliveryUnknownException(method, url, HttpStatusCode.GatewayTimeout, "lost");
                return (JsonObject)moved.DeepClone();
            }
            if (method == "GET" && at is { } read)
                return (JsonObject)read.Message.DeepClone();
            throw new M365Graph.GraphRequestException(HttpStatusCode.NotFound, method, url, "not found");
        }
    }

    private static JsonObject Mail(string id, string from, string subject, bool isRead = false, string received = "2026-11-20T08:00:00Z") => new()
    {
        ["id"] = id, ["internetMessageId"] = $"<{id}@mail.example.com>", ["subject"] = subject, ["bodyPreview"] = "", ["webLink"] = "x",
        ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = from } }, ["receivedDateTime"] = received, ["isRead"] = isRead,
    };

    private (int Code, string Out) Cli(params string[] args)
    {
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now)))
            code = MailTriage.Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), .. args],
                new CliHost { Out = output, Err = output, Echo = true, Graph = (_, _) => graph, Sleep = _ => { } });
        return (code, output.ToString());
    }

    private JsonArray Json(params string[] args) => (JsonArray)PyJson.Parse(Cli(args).Out)!;

    private void Records(params JsonObject[] items) => StateFiles.Save(config.DeletedFile, new JsonObject { ["items"] = new JsonArray(items) });

    private JsonObject Raised => StateFiles.LoadObject(config.StateFile, () => []);

    private JsonObject Unconfirmed => StateFiles.LoadObject(config.UnconfirmedFile, () => []);

    [Fact]
    public void A_rule_deleted_email_is_numbered_previewed_restored_once_and_never_deleted_again()
    {
        graph.Folders["newsletters"].Add(Mail("n1", "offers@news.example.com", "Weekly offers"));
        Assert.Equal(0, Cli("run").Code);
        Assert.True(Assert.Single(graph.Folders["deleteditems"]).Truthy("isRead"));   // a delete marks it read

        var (_, listing) = Cli("deleted");
        Assert.Contains("    1. 2026-11-20  offers@news.example.com: Weekly offers", listing);
        Assert.Contains("`mail-triage restore <n>`", listing);
        var entry = (JsonObject)Assert.Single(Json("deleted", "--json"))!;
        Assert.Matches("^[0-9a-f]{12}$", entry.Req("id"));
        Assert.Equal((1L, "<n1@mail.example.com>", "News", true), ((long)entry["n"]!, entry.Str("message_id"), entry.Str("rule"), (bool)entry["restorable"]!));
        Assert.Null(entry["restored"]);

        var writes = graph.Calls.Count(c => c.Method != "GET");
        var (code, preview) = Cli("restore", "1");
        Assert.Equal(0, code);
        Assert.Contains("Would move this email from Deleted Items back to the Inbox:", preview);
        Assert.Contains("  From      offers@news.example.com", preview);
        Assert.Contains("  Subject   Weekly offers", preview);
        Assert.Contains("  Received  2026-11-20 08:00:00 UTC", preview);
        Assert.Contains("  Deleted   2026-11-23 09:00, rule \"News\"", preview);
        Assert.Contains("  Goes to   the Inbox, read as it is", preview);
        Assert.Contains("Nothing moved. Run again with --yes", preview);
        Assert.Equal(writes, graph.Calls.Count(c => c.Method != "GET"));   // the preview writes nothing

        (code, var done) = Cli("restore", entry.Req("id"), "--yes");   // by id, as the UI does
        Assert.Equal(0, code);
        Assert.Contains("Restored to the Inbox (read).", done);
        var move = graph.Moves[^1];
        Assert.Equal(2, graph.Moves.Count);
        Assert.Equal("inbox", move.Body!["destinationId"]!.GetValue<string>());
        Assert.Single(graph.Calls, c => c.Method == "PATCH");   // only the delete's: its read state is left as it is
        Assert.Empty(graph.Folders["deleteditems"]);
        Assert.Equal("Weekly offers", Assert.Single(graph.Folders["inbox"]).Str("subject"));

        entry = (JsonObject)Assert.Single(Json("deleted", "--json"))!;
        Assert.Equal(("restored", false), (entry.Str("restored"), (bool)entry["restorable"]!));
        Assert.Equal("2026-11-23T09:00:00", entry.Str("restored_at"));
        Assert.Contains("[restored to the Inbox 2026-11-23 09:00]", Cli("deleted").Out);

        // The next pass leaves it alone (the rule names the Inbox too), and doesn't queue it; a new one is still deleted.
        graph.Folders["inbox"].Add(Mail("n2", "offers@news.example.com", "More offers"));
        Assert.Equal(0, Cli("run").Code);
        Assert.Equal(["Weekly offers"], graph.Folders["inbox"].Select(m => m.Str("subject")));
        Assert.Equal(3, graph.Moves.Count);
        Assert.Empty(Triage.Queue(config));

        (code, done) = Cli("restore", "1", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("was restored to the Inbox on 2026-11-23 09:00:00: nothing to do", done);
        Assert.Equal(3, graph.Moves.Count);
    }

    [Fact]
    public void A_move_whose_answer_was_lost_is_recorded_kept_from_the_rules_and_settled_by_reading_the_inbox()
    {
        graph.Folders["newsletters"].Add(Mail("n1", "offers@news.example.com", "Weekly offers"));
        Cli("run");
        var id = ((JsonObject)Json("deleted", "--json")[0]!).Req("id");
        graph.LoseMoves = true;
        var (code, output) = Cli("restore", "1", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("Microsoft didn't confirm the move, so it may or may not have gone", output);
        var record = (JsonObject)Unconfirmed[$"restore:{id}"]!;
        Assert.Equal(("restore", "<n1@mail.example.com>"), (record.Str("verb"), record.Str("message_id")));
        Assert.Equal("unconfirmed", ((JsonObject)Json("deleted", "--json")[0]!).Str("restored"));
        Assert.True(Raised.ContainsKey("<n1@mail.example.com>") && Raised.ContainsKey(Restores.Marker + "<n1@mail.example.com>"));

        graph.LoseMoves = false;
        Cli("run");   // it went: the rule doesn't delete it again meanwhile
        Assert.Single(graph.Folders["inbox"]);
        Assert.Equal(2, graph.Moves.Count);

        (code, output) = Cli("restore", "1");   // read again, it's there: settled, never sent again
        Assert.Equal(0, code);
        Assert.Contains("An earlier restore of \"Weekly offers\" (to the Inbox) on Mon 23 Nov 09:00 went: it's there now", output);
        Assert.Equal(2, graph.Moves.Count);
        Assert.False(Unconfirmed.ContainsKey($"restore:{id}"));
        Assert.Equal("restored", ((JsonObject)Json("deleted", "--json")[0]!).Str("restored"));
    }

    [Fact]
    public void A_lost_move_that_did_not_land_is_skipped_until_again()
    {
        graph.Folders["newsletters"].Add(Mail("n1", "offers@news.example.com", "Weekly offers"));
        Cli("run");
        graph.LoseMoves = true;
        graph.LostMovesGo = false;
        Assert.Equal(1, Cli("restore", "1", "--yes").Code);
        graph.LoseMoves = false;

        var (code, output) = Cli("restore", "1", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("may not have landed: check Outlook.", output);
        Assert.Contains("Not moved again.", output);
        Assert.Equal(2, graph.Moves.Count);   // the delete, and the one lost

        (code, output) = Cli("restore", "1");
        Assert.Equal(0, code);
        Assert.Contains("--again --yes", output);

        (code, output) = Cli("restore", "1", "--again", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Restored to the Inbox", output);
        Assert.Equal(3, graph.Moves.Count);
        Assert.Empty(Unconfirmed);
    }

    [Fact]
    public void An_older_record_is_found_by_time_sender_and_subject_and_an_unread_email_stays_unread()
    {
        Records(new JsonObject
        {
            ["when"] = "2026-11-20T09:00:00.000001", ["folder"] = "Newsletters", ["received"] = "2026-11-20T08:00:00Z",
            ["from"] = "offers@news.example.com", ["subject"] = "Weekly offers", ["rule"] = "News",
        });
        graph.Folders["deleteditems"].Add(Mail("d1", "offers@news.example.com", "Weekly offers"));
        graph.Folders["deleteditems"].Add(Mail("d2", "offers@news.example.com", "Monthly offers"));   // same sender and time
        Assert.Contains("the Inbox, unread as it is", Cli("restore", "1").Out);
        var (code, output) = Cli("restore", "1", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Restored to the Inbox (unread).", output);
        Assert.Equal("/me/messages/d1/move", Assert.Single(graph.Moves).Url);
        Assert.DoesNotContain(graph.Calls, c => c.Method == "PATCH");
        Assert.False(Assert.Single(graph.Folders["inbox"]).Truthy("isRead"));
        Assert.True(Raised.ContainsKey("<d1@mail.example.com>") && Raised.ContainsKey(Restores.Marker + "<d1@mail.example.com>"));
    }

    [Fact]
    public void An_email_deleted_by_hand_is_listed_as_by_hand_and_can_be_restored()
    {
        graph.Folders["newsletters"].Add(Mail("q1", "someone@elsewhere.example.org", "A question"));
        Cli("run");
        Assert.Equal(0, Cli("delete", "1").Code);
        Assert.Contains("Emails deleted by hand (1)", Cli("deleted").Out);
        var entry = (JsonObject)Assert.Single(Json("deleted", "--json"))!;
        Assert.Equal(("by hand", "<q1@mail.example.com>", "Newsletters", true),
            (entry.Str("rule"), entry.Str("message_id"), entry.Str("folder"), (bool)entry["restorable"]!));
        Assert.Contains("by hand (`mail-triage delete`)", Cli("restore", "1").Out);
        Assert.Equal(0, Cli("restore", "1", "--yes").Code);
        Assert.Equal("A question", Assert.Single(graph.Folders["inbox"]).Str("subject"));
    }

    [Fact]
    public void A_journal_run_moves_and_records_nothing()
    {
        graph.Folders["newsletters"].Add(Mail("n1", "offers@news.example.com", "Weekly offers"));
        Cli("run");
        var (code, output) = Cli("--writes", "journal", "restore", "1", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Journal only: nothing moved, and nothing recorded.", output);
        Assert.Single(graph.Moves);
        Assert.Null(((JsonObject)Json("deleted", "--json")[0]!)["restored"]);
        Assert.False(Raised.ContainsKey(Restores.Marker + "<n1@mail.example.com>"));
    }

    [Fact]
    public void What_isnt_a_deleted_email_or_isnt_there_any_more_is_refused()
    {
        Records(
            new JsonObject
            {
                ["when"] = "2026-11-20T09:00:00.000001", ["folder"] = "Newsletters", ["received"] = "2026-11-20T08:00:00Z",
                ["from"] = "plans@example.com", ["subject"] = "Plan", ["rule"] = "Plans", ["to"] = "Inbox/Projects",
            },
            new JsonObject
            {
                ["when"] = "2026-11-20T09:00:00.000002", ["folder"] = "Calendar", ["received"] = "2026-11-24T10:00:00",
                ["from"] = "sam@example.com", ["subject"] = "Planning", ["rule"] = "Declined: on leave", ["did"] = "declined",
            },
            new JsonObject
            {
                ["when"] = "2026-11-20T09:00:00.000003", ["folder"] = "Newsletters", ["received"] = "2026-11-20T08:00:00Z",
                ["from"] = "offers@news.example.com", ["subject"] = "Gone", ["rule"] = "News", ["key"] = "<gone@mail.example.com>",
            });
        var (code, output) = Cli("restore", "1");
        Assert.Equal(2, code);
        Assert.Contains("Can't restore 1. plans@example.com: \"Plan\": it was filed, not deleted.", output);
        Assert.Contains("it's a calendar change, not an email", Cli("restore", "2").Out);
        (code, output) = Cli("restore", "3", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("it isn't in Deleted Items any more", output);
        (code, output) = Cli("restore", "99");
        Assert.Equal(2, code);
        Assert.Contains("Nothing in `mail-triage deleted` is '99'", output);
        Assert.Empty(graph.Moves);

        var json = Json("deleted", "--json").Cast<JsonObject>().ToList();
        Assert.Equal([false, false, true], json.Select(e => (bool)e["restorable"]!));
        Assert.Equal("it was filed, not deleted", json[0].Str("why_not"));
    }

    [Fact]
    public void The_json_is_valid_when_empty_and_list_json_keeps_its_shape()
    {
        Assert.Equal("[]", Cli("deleted", "--json").Out.Trim());
        Assert.Equal("[]", Cli("list", "--json").Out.Trim());
        graph.Folders["newsletters"].Add(Mail("q1", "someone@elsewhere.example.org", "Café menu ☕"));
        Cli("run");
        var item = (JsonObject)Assert.Single(Json("list", "--json"))!;
        Assert.Equal((1L, "q1", "<q1@mail.example.com>", "someone@elsewhere.example.org", "Café menu ☕", "2026-11-20T08:00:00Z", "no rule matched"),
            ((long)item["n"]!, item.Str("id"), item.Str("key"), item.Str("from"), item.Str("subject"), item.Str("received"), item.Str("rule")));
    }

    [Fact]
    public void Numbers_stay_put_when_old_records_are_pruned()
    {
        JsonObject Entry(string when, string subject) => new()
        {
            ["when"] = when, ["folder"] = "Newsletters", ["received"] = "2026-09-01T08:00:00Z", ["from"] = "offers@news.example.com",
            ["subject"] = subject, ["rule"] = "News", ["key"] = $"<{subject}@mail.example.com>",
        };
        Records(Entry("2026-09-01T09:00:00.000001", "old"), Entry("2026-11-20T09:00:00.000001", "a"), Entry("2026-11-21T09:00:00.000001", "b"));
        var before = Json("deleted", "--all", "--json").Cast<JsonObject>().Select(e => ((long)e["n"]!, e.Req("id"), e.Req("subject"))).ToList();
        Assert.Equal([1L, 2L, 3L], before.Select(e => e.Item1));
        Cli("run");   // prunes what's older than 60 days
        var after = Json("deleted", "--all", "--json").Cast<JsonObject>().Select(e => ((long)e["n"]!, e.Req("id"), e.Req("subject"))).ToList();
        Assert.Equal(before[1..], after);
        Assert.Equal(1L, (long)StateFiles.LoadObject(config.DeletedFile, () => [])[Restores.PrunedField]!);
        Assert.Contains("\"b\"", Cli("restore", "3").Out);
    }
}
