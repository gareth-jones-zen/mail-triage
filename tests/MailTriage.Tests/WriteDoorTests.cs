using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage.Tests;

/// <summary>The write door and the read-only fence: the safety design, tested before anything else.</summary>
public sealed class WriteDoorTests
{
    /// <summary>Records every call; reads return an empty page.</summary>
    private sealed class Recorder : IGraph
    {
        public readonly List<string> Calls = [];

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add($"{method} {url} {PyJson.Dump(body, null, false)} {string.Join(",", scopes ?? [])}");
            return [];
        }

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add($"PAGES {url} {string.Join(",", headers?.Values ?? [])}");
            return [];
        }
    }

    private static void EveryWrite(IMailWriter mail, ICalendarWriter calendar)
    {
        mail.Delete("m1");
        mail.File("m2", "f1");
        calendar.Respond("e1", "accept", "fine", true);
        calendar.Respond("e2", "decline", "ignored when silent", false);
        calendar.Respond("e3", "tentative", "", true);
        calendar.Move("e4", new DateTime(2026, 11, 13, 12, 0, 0), new DateTime(2026, 11, 13, 12, 30, 0), "GMT Standard Time");
        calendar.Cancel("e5", "Cancelling as I'm on leave");
    }

    [Fact]
    public void live_sends_exactly_the_python_calls()
    {
        var graph = new Recorder();
        var door = WriteDoor.Live(graph);
        EveryWrite(door.Mail, door.Calendar);
        Assert.Equal(
        [
            """PATCH /me/messages/m1 {"isRead": true} Mail.ReadWrite""",
            """POST /me/messages/m1/move {"destinationId": "deleteditems"} Mail.ReadWrite""",
            """POST /me/messages/m2/move {"destinationId": "f1"} Mail.ReadWrite""",
            """POST /me/events/e1/accept {"sendResponse": true, "comment": "fine"} Calendars.ReadWrite""",
            """POST /me/events/e2/decline {"sendResponse": false} Calendars.ReadWrite""",
            """POST /me/events/e3/tentativelyAccept {"sendResponse": true} Calendars.ReadWrite""",
            """PATCH /me/events/e4 {"start": {"dateTime": "2026-11-13T12:00:00", "timeZone": "GMT Standard Time"}, "end": {"dateTime": "2026-11-13T12:30:00", "timeZone": "GMT Standard Time"}} Calendars.ReadWrite""",
            """POST /me/events/e5/cancel {"comment": "Cancelling as I'm on leave"} Calendars.ReadWrite""",
        ], graph.Calls);
    }

    [Fact]
    public void journal_records_the_same_calls_and_sends_nothing()
    {
        var door = WriteDoor.Journaling();
        EveryWrite(door.Mail, door.Calendar);
        Assert.Equal(WriteMode.Journal, door.Mode);
        Assert.Equal(8, door.Journal.Count);
        Assert.Equal("""POST /me/messages/m1/move {"destinationId": "deleteditems"}""", door.Journal[1].Text);
    }

    [Fact]
    public void refuse_throws_on_every_write_and_sends_nothing()
    {
        var door = WriteDoor.Refusing();
        Assert.Throws<WriteRefusedException>(() => door.Mail.Delete("m1"));
        Assert.Throws<WriteRefusedException>(() => door.Mail.File("m1", "f"));
        Assert.Throws<WriteRefusedException>(() => door.Calendar.Respond("e", "accept", "", true));
        Assert.Throws<WriteRefusedException>(() => door.Calendar.Move("e", DateTime.Now, DateTime.Now, "UTC"));
        Assert.Throws<WriteRefusedException>(() => door.Calendar.Cancel("e", ""));
    }

    [Theory]
    [InlineData("POST", "/me/messages/m1/move")]
    [InlineData("PATCH", "/me/messages/m1")]
    [InlineData("POST", "/me/events/e1/accept")]
    [InlineData("POST", "/me/events/e1/cancel")]
    [InlineData("DELETE", "/me/events/e1")]
    [InlineData("POST", "/me/calendar/getSchedule/x")]
    [InlineData("PUT", "/me/calendar/getSchedule")]
    public void the_read_only_fence_refuses_anything_but_reads(string method, string url)
    {
        var inner = new Recorder();
        Assert.Throws<WriteRefusedException>(() => new ReadOnlyGraph(inner).Request(url, method));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void the_read_only_fence_lets_reads_and_get_schedule_through()
    {
        var inner = new Recorder();
        var fence = new ReadOnlyGraph(inner);
        fence.Request("/me/messages/x?$select=body");
        fence.Request("/me/calendar/getSchedule", "POST", new JsonObject());
        _ = fence.Pages("/me/mailFolders").ToList();
        Assert.Equal(3, inner.Calls.Count);
    }

    [Fact]
    public void mail_and_calendar_read_only_through_the_fence()
    {
        var inner = new Recorder();
        var mailbox = new Mailbox(inner, WriteDoor.Refusing().Mail);
        Assert.IsType<ReadOnlyGraph>(mailbox.Graph);
        // The mailbox's read graph refuses a write even if code reached for it directly.
        Assert.Throws<WriteRefusedException>(() => mailbox.Graph.Request("/me/messages/m1/move", "POST"));
        Assert.Throws<WriteRefusedException>(() => mailbox.Delete("m1"));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void a_dry_run_from_the_command_line_never_builds_a_live_door()
    {
        using var dir = new TempDir();
        dir.Write("config.toml", TriageBase.SettingsText);
        var graph = new FakeGraph([TriageBase.Message(1, "a@news.example.com"), TriageBase.Message(2, "boss@example.com")]);
        var output = new StringWriter();
        var code = Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), "run", "--dry-run"],
            new CliHost { Out = output, Err = output, Graph = (_, _) => graph });
        Assert.Equal(0, code);
        Assert.Contains("would delete", output.ToString());
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void journal_mode_from_the_command_line_sends_no_writes()
    {
        using var dir = new TempDir();
        dir.Write("config.toml", TriageBase.SettingsText);
        var graph = new FakeGraph([TriageBase.Message(1, "a@news.example.com")]);
        var journal = Path.Combine(dir.Path, "journal.log");
        var output = new StringWriter();
        var code = Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), "--writes", "journal", "--journal", journal, "run"],
            new CliHost { Out = output, Err = output, Graph = (_, _) => graph });
        Assert.Equal(0, code);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
        Assert.Contains("POST /me/messages/graph-1/move", File.ReadAllText(journal));
    }

    [Fact]
    public void the_one_to_ones_preview_refuses_writes()
    {
        var calendar = new Calendar(new FakeGraph([]), WriteDoor.Refusing().Calendar);
        Assert.Throws<WriteRefusedException>(() => calendar.Cancel("e1", "x"));
        Assert.Throws<WriteRefusedException>(() => calendar.Move("e1", DateTime.Now, DateTime.Now));
    }
}
