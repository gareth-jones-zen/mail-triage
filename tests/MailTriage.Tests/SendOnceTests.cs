using System.Net;
using System.Text.Json.Nodes;
using M365Graph;

namespace MailTriage.Tests;

/// <summary>The writes through the real adapter and M365Graph, over a fake handler: a write that may have reached Microsoft
/// is sent once (a 5xx or a timeout after sending is "may have gone"), and reads, getSchedule and DELETEs still retry.</summary>
public class SendOnceTests
{
    private sealed class Handler(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(answers[Math.Min(Seen.Count, answers.Length) - 1](request));
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode status, string body = "{}") =>
        new(status) { Content = new StringContent(body) };

    private static Func<HttpRequestMessage, HttpResponseMessage> TimedOut =>
        _ => throw new TaskCanceledException("timed out", new TimeoutException());

    private static (WriteDoor Door, M365GraphAdapter Graph) Live(Handler handler)
    {
        var client = new GraphClient(new GraphClientOptions { Log = _ => { } }, handler, token: (_, _) => Task.FromResult("t"),
            delay: (_, _) => Task.CompletedTask);
        var graph = new M365GraphAdapter(client);
        return (WriteDoor.Live(graph), graph);
    }

    public static TheoryData<string> Writes => new() { "respond", "move", "cancel", "forward", "file", "hold" };

    private static void Write(WriteDoor door, string which)
    {
        switch (which)
        {
            case "respond": door.Calendar.Respond("e1", "decline", "On leave", true); break;
            case "move": door.Calendar.Move("e1", new DateTime(2026, 12, 1, 10, 0, 0), new DateTime(2026, 12, 1, 11, 0, 0), "UTC"); break;
            case "cancel": door.Calendar.Cancel("e1", "Sorry"); break;
            case "forward": door.Mail.Forward("m1", ["a@example.com"], "fyi"); break;
            case "file": door.Mail.File("m1", "f1"); break;
            case "hold": door.Calendar.Hold(new JsonObject { ["subject"] = "Hold: x" }); break;
        }
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public void a_write_answered_with_a_5xx_is_sent_once_and_may_have_gone(string which)
    {
        var handler = new Handler(_ => Status(HttpStatusCode.ServiceUnavailable), _ => Status(HttpStatusCode.OK));
        var error = Assert.Throws<GraphDeliveryUnknownException>(() => Write(Live(handler).Door, which));
        Assert.Single(handler.Seen);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.Status);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public void a_write_that_timed_out_after_sending_is_sent_once_and_may_have_gone(string which)
    {
        var handler = new Handler(TimedOut, _ => Status(HttpStatusCode.OK));
        var error = Assert.Throws<GraphDeliveryUnknownException>(() => Write(Live(handler).Door, which));
        Assert.Single(handler.Seen);
        Assert.Null(error.Status);
    }

    [Fact]
    public void an_invitation_answered_with_a_5xx_is_sent_once_and_may_have_gone()
    {
        var handler = new Handler(_ => Status(HttpStatusCode.BadGateway), _ => Status(HttpStatusCode.Created));
        Assert.Throws<GraphDeliveryUnknownException>(() => Live(handler).Graph.RequestOnce("/me/events", "POST", new JsonObject()));
        Assert.Single(handler.Seen);
    }

    [Fact]
    public void a_read_and_getschedule_still_retry_a_5xx()
    {
        var handler = new Handler(_ => Status(HttpStatusCode.BadGateway), _ => Status(HttpStatusCode.OK, "{\"value\": []}"));
        Live(handler).Graph.Request("/me/messages/m1");
        Assert.Equal(["GET /v1.0/me/messages/m1", "GET /v1.0/me/messages/m1"], handler.Seen);

        var schedule = new Handler(_ => Status(HttpStatusCode.ServiceUnavailable), _ => Status(HttpStatusCode.OK, "{\"value\": []}"));
        Live(schedule).Graph.Request(ReadOnlyGraph.SchedulePath, "POST", new JsonObject());
        Assert.Equal(2, schedule.Seen.Count);
    }

    [Fact]
    public void a_remove_is_retried_and_finding_it_gone_counts_as_done()
    {
        var handler = new Handler(TimedOut, _ => Status(HttpStatusCode.NotFound));
        Live(handler).Door.Calendar.Remove("e1");
        Assert.Equal(["DELETE /v1.0/me/events/e1", "DELETE /v1.0/me/events/e1"], handler.Seen);
    }
}
