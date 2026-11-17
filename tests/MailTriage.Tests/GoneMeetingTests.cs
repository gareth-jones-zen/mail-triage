using System.Net;
using System.Text.Json.Nodes;

namespace MailTriage.Tests;

/// <summary>1.6.1: when a cancelled meeting has already been deleted, Graph answers the expanded read of its
/// cancellation notice with 404 for the whole email (17 Nov 2026: seven notices, and the run stopped at the first).
/// The notice is read again without the expansion, and dealt with as a meeting that's gone.</summary>
public sealed class GoneMeetingTests
{
    private sealed class GoneGraph : IGraph
    {
        private const string Msg = "#microsoft.graph.eventMessage";
        private const string Req = "#microsoft.graph.eventMessageRequest";

        public List<string> Reads { get; } = [];

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) =>
        [
            new() { ["@odata.type"] = Msg, ["id"] = "c1", ["subject"] = "Canceled: Architecture Catch-Up" },
            new() { ["@odata.type"] = Req, ["id"] = "r1", ["subject"] = "Invite whose meeting has gone" },
            new() { ["@odata.type"] = Msg, ["id"] = "x1", ["subject"] = "Canceled: deleted in Outlook meanwhile" },
        ];

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Reads.Add(url);
            var id = url.Split('/')[3].Split('?')[0];
            if (url.Contains("$expand", StringComparison.Ordinal) || id == "x1")
                throw new M365Graph.GraphRequestException(HttpStatusCode.NotFound, "GET", url, "{\"error\":{\"code\":\"ErrorItemNotFound\"}}");
            return id == "c1"
                ? new JsonObject { ["id"] = "c1", ["meetingMessageType"] = "meetingCancelled" }
                : new JsonObject { ["id"] = id, ["meetingMessageType"] = "meetingRequest" };
        }
    }

    [Fact]
    public void A_notice_whose_meeting_is_gone_is_read_again_and_dealt_with()
    {
        var graph = new GoneGraph();
        var found = Proposals.AnsweredRequests(graph, "folder");
        var cancelled = Assert.Single(found, m => m.Req("id") == "c1");
        Assert.Equal("cancelled, already off the calendar", cancelled.Str("answer"));
        Assert.False(cancelled.Has("remove"));
        var request = Assert.Single(found, m => m.Req("id") == "r1");
        Assert.Equal("gone", request.Str("answer"));
        Assert.DoesNotContain(found, m => m.Req("id") == "x1");   // the email itself gone: skipped, not a failure
        Assert.Contains(graph.Reads, u => u == "/me/messages/c1");   // the second read, without the expansion
    }
}
