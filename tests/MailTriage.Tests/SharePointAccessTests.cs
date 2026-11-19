using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>sharepoint-access (1.12.0): a SharePoint access request only matters if no other owner has dealt with it.
/// The real case: Stu Cowley asked for the Architecture Practice site at 13:58 on 19 Nov 2026, and Fahad approved it at
/// 14:43, while the email sat in GJ's queue. Names and ids here are made up.</summary>
[Collection("Checks.Gh")]   // Checks.Graph is static, as Checks.Gh is
public sealed class SharePointAccessTests : IDisposable
{
    private const string Rule = """
        [[rule]]
        name = "SharePoint access requests"
        sender = 'no-reply@sharepointonline\.com'
        match = "wants to access '"
        action = "check"
        check = "sharepoint-access"

        """;

    // The shape of the real email's body (the links shortened).
    private const string Body = """
        <div><a name="customMessage">Robin Hall</a></div><div><a href="https://example.sharepoint.com/sites/TeamSite/Access%20Requests/pendingreq.aspx?ApproveAccessRequest=true&amp;AccessRequestID=%7b2188CE05%7d">Accept</a> or
        <a href="https://example.sharepoint.com/x">Decline</a> this request <div style="font-size:14px">Requested resource: <a href="https://example.sharepoint.com/sites/TeamSite?web=1&amp;xsdata=abc">https://example.sharepoint.com/sites/TeamSite?web=1</a> </div>
        <div style="font-size:14px">Requested by: Robin Hall(robin.hall@example.com) </div></div>
        """;

    private readonly TempDir dir = new();
    private readonly List<string> lines = [];
    private readonly IGraph? saved = Checks.Graph;

    public void Dispose()
    {
        Checks.Graph = saved;
        dir.Dispose();
    }

    private static JsonObject Request() =>
        TriageBase.Message(9, "no-reply@sharepointonline.com", "Robin Hall wants to access 'Team Site'", "Robin Hall Accept or Decline this request");

    private static JsonObject Item(string name, string asked, string status, string by = "Jordan Lee", string changed = "2026-11-10T08:45:00Z") => new()
    {
        ["id"] = "25", ["lastModifiedDateTime"] = changed,
        ["lastModifiedBy"] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = by } },
        ["fields"] = new JsonObject { ["StatusDisp"] = status, ["RequestedForDisplayNameDisp"] = name, ["RequestedByDisplayNameDisp"] = name, ["RequestDate"] = asked },
    };

    /// <summary>The mailbox's fake, plus a site with an Access Requests list.</summary>
    private sealed class SiteGraph(FakeGraph mail) : IGraph
    {
        public FakeGraph Mail { get; } = mail;
        public List<JsonObject> Items { get; set; } = [];
        public Exception? Fails { get; set; }
        public List<string> SiteCalls { get; } = [];

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            if (!url.StartsWith("/sites/", StringComparison.Ordinal))
                return Mail.Pages(url, scopes, headers);
            SiteCalls.Add(url);
            Assert.Equal(Checks.SiteScopes, scopes);
            Assert.StartsWith("/sites/site-1/lists/list-1/items?$expand=fields", url);
            return Fails is { } e ? throw e : Items.Select(i => (JsonObject)i.DeepClone()).ToList();
        }

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            if (!url.StartsWith("/sites/", StringComparison.Ordinal))
                return Mail.Request(url, method, body, scopes, headers);
            SiteCalls.Add(url);
            Assert.Equal("GET", method);
            if (url == "/sites/example.sharepoint.com:/sites/TeamSite?$select=id")
                return new JsonObject { ["id"] = "site-1" };
            if (url == "/sites/site-1/lists/Access%20Requests?$select=id")
                return new JsonObject { ["id"] = "list-1" };
            throw new InvalidOperationException($"unexpected {url}");
        }
    }

    private static (bool, string) Check(SiteGraph graph, JsonObject? message = null, string body = Body)
    {
        Checks.Graph = graph;
        return Checks.CheckSharePointAccess(message ?? Request(), body, null!);
    }

    [Fact]
    public void reads_the_resource_and_who_asked_from_the_email()
    {
        Assert.Equal(("https://example.sharepoint.com/sites/TeamSite?web=1", "Robin Hall", "robin.hall@example.com"), Checks.ParseAccessRequest(Body));
        Assert.Null(Checks.ParseAccessRequest("<p>Someone shared a file with you</p>"));
    }

    [Theory]
    [InlineData("https://example.sharepoint.com/sites/TeamSite?web=1", "/sites/example.sharepoint.com:/sites/TeamSite")]
    [InlineData("https://example.sharepoint.com/teams/Squad/Shared%20Documents/a.docx", "/sites/example.sharepoint.com:/teams/Squad")]
    [InlineData("https://example-my.sharepoint.com/personal/robin_example_com/Documents/x", "/sites/example-my.sharepoint.com:/personal/robin_example_com")]
    [InlineData("https://example.sharepoint.com/SitePages/Home.aspx", "/sites/example.sharepoint.com")]
    [InlineData("https://example.com/sites/TeamSite", null)]
    public void the_site_comes_from_the_resource(string url, string? path) => Assert.Equal(path, Checks.SitePath(url));

    [Fact]
    public void approved_by_another_owner_passes_and_says_who()
    {
        var graph = new SiteGraph(new FakeGraph([])) { Items = [Item("Robin Hall", "2026-11-10T07:59:59Z", "1"), Item("Robin Hall", "2026-09-01T10:00:00Z", "1", "Old Owner")] };
        var (fine, why) = Check(graph);
        Assert.True(fine);
        Assert.StartsWith("already approved by Jordan Lee (", why);
    }

    [Theory]
    [InlineData("0", "still waiting for approval")]
    [InlineData("3", "its status in the site's access requests is 3 (not approved), changed by Jordan Lee")]
    public void anything_but_approved_is_raised(string status, string why) =>
        Assert.Equal((false, why), Check(new SiteGraph(new FakeGraph([])) { Items = [Item("Robin Hall", "2026-11-10T08:00:01Z", status)] }));

    [Fact]
    public void the_request_is_matched_by_who_and_when_not_just_who()
    {
        // Robin's earlier request was approved; this one, an hour later than any in the list, can't be told apart.
        var graph = new SiteGraph(new FakeGraph([])) { Items = [Item("Robin Hall", "2026-11-10T06:30:00Z", "1"), Item("Jordan Lee", "2026-11-10T08:00:00Z", "1")] };
        var (fine, why) = Check(graph);
        Assert.False(fine);
        Assert.Equal("Robin Hall's request isn't in the site's access requests, so can't tell whether it's been dealt with", why);
    }

    [Fact]
    public void an_error_reading_the_site_never_passes()
    {
        var graph = new SiteGraph(new FakeGraph([])) { Fails = new HttpRequestException("403 Forbidden") };
        var (fine, why) = Check(graph);
        Assert.False(fine);
        Assert.StartsWith("couldn't read the site's access requests (403 Forbidden)", why);
        Assert.Equal((false, "couldn't read the requested resource and who asked from the email"), Check(graph, body: "<p>nothing</p>"));
    }

    [Fact]
    public void a_request_queued_before_the_rule_existed_is_checked_once_there_is_one()
    {
        // Queued with no rule, as Stu's was; then the rule is added and another owner approves it.
        var without = dir.Settings(TriageBase.SettingsText);
        var graph = new SiteGraph(new FakeGraph([Request()], Body)) { Items = [Item("Robin Hall", "2026-11-10T07:59:59Z", "0")] };
        Triage.Run(without, new Mailbox(graph, WriteDoor.Live(graph).Mail), null, false, lines.Add);
        Assert.Equal("no rule matched", Triage.Queue(without).Single().Req("rule"));
        Assert.Empty(graph.SiteCalls);

        var with = dir.Settings(TriageBase.SettingsText.Replace("[[flow_check]]", Rule + "[[flow_check]]"));
        var report = Triage.Run(with, new Mailbox(graph, WriteDoor.Live(graph).Mail), null, false, lines.Add);
        Assert.Equal((0, 1), (report.Deleted, report.Waiting));   // still waiting for approval: stays
        Assert.Equal("still waiting for approval", Triage.Queue(with).Single().Req("check"));

        graph.Items = [Item("Robin Hall", "2026-11-10T07:59:59Z", "1")];
        report = Triage.Run(with, new Mailbox(graph, WriteDoor.Live(graph).Mail), null, false, lines.Add);
        Assert.Equal((1, 0), (report.Deleted, report.Waiting));
        Assert.True(graph.Mail.Has("POST", "/me/messages/graph-9/move", new JsonObject { ["destinationId"] = "deleteditems" }));
        Assert.Empty(Triage.Queue(with));
        Assert.Contains(lines, l => l.StartsWith("deleted  [SharePoint access requests] no-reply@sharepointonline.com: ", StringComparison.Ordinal)
                                     && l.Contains("(already approved by Jordan Lee ("));
    }

    [Fact]
    public void a_new_request_already_approved_is_deleted_on_arrival()
    {
        var with = dir.Settings(TriageBase.SettingsText.Replace("[[flow_check]]", Rule + "[[flow_check]]"));
        var graph = new SiteGraph(new FakeGraph([Request()], Body)) { Items = [Item("Robin Hall", "2026-11-10T07:59:59Z", "1")] };
        var report = Triage.Run(with, new Mailbox(graph, WriteDoor.Live(graph).Mail), null, false, lines.Add);
        Assert.Equal((1, 0), (report.Deleted, report.New));
    }
}
