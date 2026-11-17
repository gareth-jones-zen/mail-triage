using System.Text.Json.Nodes;

namespace MailTriage.Tests;

/// <summary>1.6.2: the folder pass leaves a new-time proposal to the proposals sweep (it had been queuing it as "no
/// rule" since the Inbox was triaged, and the sweep then skipped it as already raised).</summary>
public sealed class ProposalFirstTests
{
    private sealed class Replies : IGraph
    {
        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) => [];

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null) => url.Split('/')[3] switch
        {
            "proposal" => new JsonObject { ["proposedNewTime"] = new JsonObject { ["start"] = new JsonObject { ["dateTime"] = "2026-11-20T12:00:00" } } },
            "decline" => new JsonObject { ["subject"] = "Declined: catch-up" },
            _ => throw new M365Graph.GraphRequestException(System.Net.HttpStatusCode.NotFound, method, url, ""),
        };
    }

    [Fact]
    public void Only_a_reply_proposing_a_new_time_is_a_proposal()
    {
        var graph = new Replies();
        Assert.True(Proposals.IsProposal(graph, "proposal"));
        Assert.False(Proposals.IsProposal(graph, "decline"));   // a decline still goes to GJ as before
        Assert.False(Proposals.IsProposal(graph, "gone"));      // can't be read: not treated as one
    }

    [Fact]
    public void The_real_proposal_shape_is_recognised()
    {
        var box = new ProposalGraph(("2026-11-20T12:00", "2026-11-20T12:30"));
        Assert.True(Proposals.IsProposal(box, "p1"));
    }
}
