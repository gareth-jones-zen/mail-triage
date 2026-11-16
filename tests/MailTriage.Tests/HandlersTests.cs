using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>Handlers: a rule with action = "handle" gives the email to a command, then does what the handler says.</summary>
public sealed class HandlersTests : IDisposable
{
    private const string Text = """
        queue_file = "queue.json"
        state_file = "raised.json"
        deleted_file = "deleted.json"
        handled_file = "handled.json"
        log_file = "triage.log"

        [[folder]]
        name = "Triage"

        [[handler]]
        name = "draft"
        command = ['python', 'draft.py']
        timeout = 30
        max_attempts = 3

        [[handler]]
        name = "keep-it"
        command = ['keep.exe']
        then = "leave"

        [[handler]]
        name = "look"
        command = ['look.exe']
        then = "review"

        [[handler]]
        name = "file-it"
        command = ['file.exe']
        then = "file"
        to = "Inbox/Triage"

        [[rule]]
        name = "Marketing"
        sender = 'john@example\.com'
        match = 'Marketing update'
        action = "handle"
        handler = "draft"

        [[rule]]
        name = "Keep"
        sender = 'keep@example\.com'
        action = "handle"
        handler = "keep-it"

        [[rule]]
        name = "Look"
        sender = 'look@example\.com'
        action = "handle"
        handler = "look"

        [[rule]]
        name = "File"
        sender = 'file@example\.com'
        action = "handle"
        handler = "file-it"

        """;

    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly List<string> lines = [];
    private readonly List<(string Handler, JsonObject Input)> calls = [];
    private readonly Func<Handler, string, (int, string, bool)> saved = Handlers.Process;

    public HandlersTests() => config = dir.Settings(Text);

    public void Dispose()
    {
        Handlers.Process = saved;
        dir.Dispose();
    }

    private void Answer(params (int Exit, string Output, bool TimedOut)[] answers)
    {
        var queue = new Queue<(int, string, bool)>(answers);
        Handlers.Process = (handler, input) =>
        {
            calls.Add((handler.Name, (JsonObject)JsonNode.Parse(input)!));
            return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        };
    }

    private static JsonObject Update(int n = 1, string from = "john@example.com") =>
        TriageBase.Message(n, from, "FW: ExCo Marketing update - 13/11");

    private RunReport Run(FakeGraph graph, bool live = true, bool dryRun = false) =>
        Triage.Run(config, new Mailbox(graph, WriteDoor.Live(graph).Mail, dryRun), null, dryRun, lines.Add, live ? new Handlers(config) : null);

    private List<JsonObject> Gone() => Triage.Deleted(config, everything: true);

    [Fact]
    public void settings_read_handlers_and_a_handle_rule_must_name_one()
    {
        var draft = config.Handlers["draft"];
        Assert.Equal(["python", "draft.py"], draft.Command);
        Assert.Equal("delete", draft.Then);
        Assert.Equal(PyPath.Resolve(dir.Path), draft.Cwd);
        Assert.Equal((30L, 3L), (draft.Timeout, draft.MaxAttempts));
        Assert.Equal("draft", config.Rules[0].Handler);
        var bad = Assert.Throws<SettingsException>(() => dir.Settings(Text.Replace("handler = \"draft\"", "handler = \"nope\""), "bad.toml"));
        Assert.Contains("needs handler", bad.Message);
        bad = Assert.Throws<SettingsException>(() => dir.Settings(Text.Replace("then = \"leave\"", "then = \"shred\""), "bad2.toml"));
        Assert.Contains("use delete, file, leave or review", bad.Message);
        bad = Assert.Throws<SettingsException>(() => dir.Settings(Text.Replace("command = ['keep.exe']", "command = []"), "bad3.toml"));
        Assert.Contains("needs command", bad.Message);
        bad = Assert.Throws<SettingsException>(() => dir.Settings(Text.Replace("to = \"Inbox/Triage\"\n", ""), "bad4.toml"));
        Assert.Contains("no `to` folder", bad.Message);
    }

    [Fact]
    public void a_kind_of_table_split_by_another_is_refused_not_half_read()
    {
        // The TOML reader keeps only the [[rule]]s after the break: 16 Nov 2026, a [[handler]] among the rules.
        var split = Text.Replace("[[rule]]\nname = \"Keep\"", "[[handler]]\nname = \"late\"\ncommand = ['x']\n\n[[rule]]\nname = \"Keep\"");
        var error = Assert.Throws<SettingsException>(() => dir.Settings(split, "split.toml"));
        Assert.Contains("tables, but only", error.Message);
        Assert.Contains("keep each kind together", error.Message);
    }

    [Fact]
    public void handled_runs_then_deletes_and_records_it_with_the_handler_note()
    {
        Answer((0, "draft queued for 13/11\nmore detail", false));
        var graph = new FakeGraph([Update()], body: "From 13/11: the update");
        var report = Run(graph);
        Assert.Equal((1, 1, 0), (report.Handled, report.Deleted, report.New));
        var (name, input) = Assert.Single(calls);
        Assert.Equal("draft", name);
        Assert.Equal("graph-1", input.Str("id"));
        Assert.Equal("<1@example.com>", input.Str("key"));
        Assert.Equal("Triage", input.Str("folder"));
        Assert.Equal("john@example.com", input.Str("from"));
        Assert.Equal("FW: ExCo Marketing update - 13/11", input.Str("subject"));
        Assert.Equal("From 13/11: the update", input.Str("body"));
        Assert.Equal("Marketing", input.Str("rule"));
        Assert.True(graph.Has("POST", "/me/messages/graph-1/move", new JsonObject { ["destinationId"] = "deleteditems" }));
        Assert.Equal("handled by draft: draft queued for 13/11", Assert.Single(Gone()).Str("did"));
        Assert.Empty(Triage.Queue(config));
    }

    [Fact]
    public void try_again_later_leaves_the_email_alone_then_raises_it_when_tries_run_out()
    {
        Answer((Handlers.TryAgain, "not yet", false));
        var graph = new FakeGraph([Update()]);
        Run(graph);
        Run(graph);
        Assert.Equal(2, calls.Count);
        Assert.Empty(Triage.Queue(config));
        Assert.DoesNotContain(graph.Calls, c => c.Method == "POST");
        Run(graph);   // the third of three
        Assert.Equal(3, calls.Count);
        var item = Assert.Single(Triage.Queue(config));
        Assert.Equal("draft still not ready after 3 tries: not yet", item.Str("check"));
        Run(graph);
        Assert.Equal(3, calls.Count);   // raised once, never handed again
    }

    [Fact]
    public void a_failure_or_a_timeout_is_raised_once_and_never_retried()
    {
        Answer((1, "", false));
        var graph = new FakeGraph([Update(1), Update(2)]);
        Handlers.Process = (handler, input) =>
        {
            var n = ((JsonObject)JsonNode.Parse(input)!).Str("id");
            calls.Add((handler.Name, new JsonObject()));
            return n == "graph-1" ? (2, "Traceback: boom", false) : (-1, "half done", true);
        };
        Run(graph);
        Run(graph);
        Assert.Equal(2, calls.Count);
        var queue = Triage.Queue(config);
        Assert.Equal(["draft failed (exit 2): Traceback: boom", "draft timed out after 30s: half done"], queue.Select(i => i.Str("check")));
        Assert.DoesNotContain(graph.Calls, c => c.Method == "POST");
    }

    [Fact]
    public void dry_runs_and_runs_that_may_not_write_never_run_a_handler()
    {
        Answer((0, "done", false));
        var graph = new FakeGraph([Update()]);
        Run(graph, dryRun: true);
        Run(graph, live: false);
        Assert.Empty(calls);
        Assert.Empty(Triage.Queue(config));
        Assert.Contains(lines, l => l.StartsWith("would hand to draft, then delete", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void once_handled_a_failed_delete_is_retried_without_the_handler()
    {
        Answer((0, "done", false));
        var record = new JsonObject
        {
            ["<1@example.com>"] = new JsonObject
            {
                ["handler"] = "draft", ["result"] = "done", ["note"] = "earlier", ["attempts"] = 1, ["when"] = PyTime.Iso(Clock.Now, "seconds"),
            },
        };
        File.WriteAllText(config.HandledFile, record.ToJsonString());
        var graph = new FakeGraph([Update()]);
        var report = Run(graph);
        Assert.Empty(calls);
        Assert.Equal(1, report.Deleted);
        Assert.Equal("handled by draft: earlier", Assert.Single(Gone()).Str("did"));
    }

    [Fact]
    public void then_leave_review_and_file()
    {
        Answer((0, "ok", false));
        var graph = new FakeGraph([Update(1, "keep@example.com"), Update(2, "look@example.com"), Update(3, "file@example.com")]);
        var report = Run(graph);
        Assert.Equal((3, 1, 1), (report.Handled, report.Filed, report.New));
        var item = Assert.Single(Triage.Queue(config));
        Assert.Equal(("graph-2", "handled by look: ok"), (item.Str("id"), item.Str("check")));
        Assert.True(graph.Has("POST", "/me/messages/graph-3/move", new JsonObject { ["destinationId"] = "f1" }));
        Assert.DoesNotContain(graph.Calls, c => c.Url.StartsWith("/me/messages/graph-1/", StringComparison.Ordinal) && c.Method != "GET");
        Assert.Equal(["handled by keep-it: ok, left in the folder", "handled by file-it: ok"], Gone().Select(i => i.Str("did")));
        Run(graph);
        Assert.Equal(3, calls.Count);   // the one left in the folder isn't handed again
    }

    [Fact]
    public void by_hand_by_graph_id_using_its_rule()
    {
        Answer((0, "draft queued", false));
        var graph = new MessageGraph(new FakeGraph([Update(7)]));
        var mailbox = new Mailbox(graph, WriteDoor.Live(graph).Mail);
        Assert.Equal(0, Triage.HandleByHand(config, mailbox, new Handlers(config), "graph-7", null, lines.Add));
        Assert.Equal("draft", Assert.Single(calls).Handler);
        Assert.True(graph.Inner.Has("POST", "/me/messages/graph-7/move", new JsonObject { ["destinationId"] = "deleteditems" }));
        Assert.Equal("handled by draft: draft queued", Assert.Single(Gone()).Str("did"));
        var none = Assert.Throws<LookupException>(() =>
            Triage.HandleByHand(config, mailbox, new Handlers(config), "graph-8", null, lines.Add));
        Assert.Contains("name one with --handler", none.Message);
    }

    [Fact]
    public void a_real_process_gets_the_input_and_its_exit_code_output_and_timeout_come_back()
    {
        Handler With(string args, long timeout = 30) => new() { Name = "t", Command = ["cmd.exe", "/c", args], Cwd = dir.Path, Timeout = timeout };
        Assert.Equal((0, "{\"a\": 1}", false), (saved(With("more"), "{\"a\": 1}") is var r ? (r.Item1, r.Item2.Trim(), r.Item3) : default));
        Assert.Equal((75, false), saved(With("exit 75"), "{}") is var t ? (t.Item1, t.Item3) : default);
        Assert.Equal("oops", saved(With("echo oops 1>&2 & exit 3"), "{}").Item2.Trim());
        Assert.True(saved(With("ping -n 6 127.0.0.1 >nul", timeout: 1), "{}").Item3);
        var missing = saved(new Handler { Name = "m", Command = ["no-such-handler.exe"], Cwd = dir.Path }, "{}");
        Assert.Equal(-1, missing.Item1);
        Assert.StartsWith("couldn't start no-such-handler.exe", missing.Item2);
    }

    /// <summary>A FakeGraph that also answers a GET for one message by its id.</summary>
    private sealed class MessageGraph(FakeGraph inner) : IGraph
    {
        public FakeGraph Inner { get; } = inner;

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) =>
            Inner.Pages(url, scopes, headers);

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            if (method == "GET" && url.Contains("?$select=id,", StringComparison.Ordinal))
            {
                var id = url.Split('/')[3].Split('?')[0];
                return Inner.Messages.FirstOrDefault(m => m.Str("id") == id) is { } m ? (JsonObject)m.DeepClone()
                    : TriageBase.Message(8, "nobody@example.com", "Something else");
            }
            return Inner.Request(url, method, body, scopes, headers);
        }
    }
}
