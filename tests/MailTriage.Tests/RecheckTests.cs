using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>Queued `check` items are checked again on each run (1.9.0). The real case: a failed build email checked
/// while its fixing run was still going sat in the queue saying "still failing" for two days.</summary>
[Collection("Checks.Gh")]
public sealed class RecheckTests : IDisposable
{
    private const string GitHubRule = """
        [[rule]]
        name = "GitHub Actions failures"
        sender = 'notifications@github\.com'
        match = '^\[[^\]]+\] Run failed: '
        action = "check"
        check = "github-runs"

        """;

    private const string Failure = """[{"status":"completed","conclusion":"failure","headSha":"aab7acd000","createdAt":"2026-11-17T16:59:22Z"}]""";
    private const string StillRunning = """[{"status":"in_progress","conclusion":"","headSha":"c7b2b16000"},""" + """{"status":"completed","conclusion":"failure","headSha":"aab7acd000","createdAt":"2026-11-17T16:59:22Z"}]""";
    private const string Passed = """[{"status":"completed","conclusion":"success","headSha":"c7b2b16000","createdAt":"2026-11-17T17:01:22Z"},""" + """{"status":"completed","conclusion":"failure","headSha":"aab7acd000","createdAt":"2026-11-17T16:59:22Z"}]""";

    private readonly TempDir dir = new();
    private readonly Settings config;
    private readonly List<string> lines = [];
    private readonly Func<IReadOnlyList<string>, (int, string)> saved = Checks.Gh;
    private readonly List<IReadOnlyList<string>> asked = [];
    private Func<(int, string)> answer = () => (0, Failure);

    public RecheckTests()
    {
        config = dir.Settings(TriageBase.SettingsText.Replace("[[flow_check]]", GitHubRule + "[[flow_check]]"));
        Checks.Gh = args =>
        {
            asked.Add(args);
            return answer();
        };
    }

    public void Dispose()
    {
        Checks.Gh = saved;
        dir.Dispose();
    }

    private static JsonObject BuildFailed() =>
        TriageBase.Message(7, "notifications@github.com", "[gareth-jones-zen/background-tools-lib] Run failed: build - main (aab7acd)");

    private RunReport Run(FakeGraph graph, bool dryRun = false) =>
        Triage.Run(config, new Mailbox(graph, WriteDoor.Live(graph).Mail, dryRun), null, dryRun, lines.Add);

    private bool Deleted(FakeGraph graph) => graph.Has("POST", "/me/messages/graph-7/move", new JsonObject { ["destinationId"] = "deleteditems" });

    [Fact]
    public void a_queued_failure_that_has_passed_since_is_deleted_on_a_later_run()
    {
        var graph = new FakeGraph([BuildFailed(), TriageBase.Message(2, "boss@example.com")]);
        answer = () => (0, StillRunning);   // checked while the fixing run was still going
        Assert.Equal(2, Run(graph).New);
        Assert.Contains("still failing", Triage.Queue(config)[0].Req("check"));

        answer = () => (0, Passed);
        var report = Run(graph);
        Assert.Equal((1, 0, 1), (report.Deleted, report.New, report.Waiting));
        Assert.True(Deleted(graph));
        Assert.Equal(["Boss"], Triage.Queue(config).Select(i => i.Req("rule")));
        var entry = Triage.Deleted(config).Single();
        Assert.Equal("GitHub Actions failures", entry.Req("rule"));
        Assert.StartsWith("it has passed since: the latest build run on main (c7b2b16", entry.Req("check"));
        Assert.Contains(lines, l => l.StartsWith("deleted  [GitHub Actions failures] notifications@github.com: ", StringComparison.Ordinal)
                                     && l.Contains("(it has passed since:"));
        Assert.Equal(2, asked.Count);   // once on arrival, once on the later run, never for the review item
        Run(graph);
        Assert.Equal(2, asked.Count);   // gone from the queue: not checked again
    }

    [Fact]
    public void still_failing_stays_queued_with_the_reason_brought_up_to_date()
    {
        var graph = new FakeGraph([BuildFailed()]);
        Run(graph);
        answer = () => (0, """[{"status":"completed","conclusion":"failure","headSha":"d00d1e5000","createdAt":"2026-11-18T09:00:00Z"}]""");
        var report = Run(graph);
        Assert.Equal((0, 0, 1), (report.Deleted, report.New, report.Waiting));
        Assert.False(Deleted(graph));
        Assert.Contains("(d00d1e5, 2026-11-18T09:00:00Z) ended 'failure'", Triage.Queue(config).Single().Req("check"));
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public void a_check_that_errors_never_removes_anything()
    {
        var graph = new FakeGraph([BuildFailed()]);
        Run(graph);
        var before = Triage.Queue(config).Single().Req("check");

        answer = () => throw new InvalidOperationException("gh blew up");
        Assert.Equal((0, 1), (Run(graph).Deleted, Run(graph).Waiting));
        Assert.Equal(before, Triage.Queue(config).Single().Req("check"));    // left as it was

        answer = () => (1, "");                                               // gh not signed in: can't tell
        Run(graph);
        Assert.Single(Triage.Queue(config));
        Assert.False(Deleted(graph));
    }

    [Fact]
    public void a_dry_run_only_says_it_would_delete()
    {
        var graph = new FakeGraph([BuildFailed()]);
        Run(graph);
        answer = () => (0, Passed);
        Assert.Equal(1, Run(graph, dryRun: true).Deleted);
        Assert.Contains(lines, l => l.StartsWith("would delete  [GitHub Actions failures]", StringComparison.Ordinal));
        Assert.False(Deleted(graph));
        Assert.Single(Triage.Queue(config));
    }

    [Fact]
    public void one_deleted_in_outlook_drops_off_without_a_check()
    {
        var graph = new FakeGraph([BuildFailed()]);
        Run(graph);
        graph.Messages = [];
        Assert.Equal(0, Run(graph).Waiting);
        Assert.Empty(Triage.Queue(config));
        Assert.Single(asked);
    }
}
