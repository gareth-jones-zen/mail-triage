using System.Text.Json.Nodes;
using static MailTriage.Tests.Make;

namespace MailTriage.Tests;

/// <summary>1.31.0: the note in an email answered without asking is quoted in `deleted`, and a [[note_rule]] raises it.</summary>
public sealed class NoteTextTests
{
    private const string TeamsBlock = """
        ________________________________________________________________________________
        Microsoft Teams meeting
        Join on your computer, mobile app or room device
        Click here to join the meeting<https://teams.example.com/l/meetup-join/abc>
        Meeting ID: 123 456 789
        Passcode: xyz
        Download Teams<https://example.com/download> | Join on the web<https://example.com/web>
        ________________________________________________________________________________
        """;

    [Fact]
    public void A_proposals_comment_is_its_note()
    {
        Assert.Equal("Can we push to 4pm? Running late from the dentist.",
            Notes.Comment("Can we push to 4pm?\r\nRunning late from the dentist.\r\n"));
    }

    [Fact]
    public void Boilerplate_is_stripped()
    {
        var body = $"""
            CAUTION: This email originated from outside of the organisation. Do not click links unless you recognise the sender.
            You don't often get email from pat@example.com. Learn why this is important<https://aka.example.com/why>
            Could we make it 30 minutes later? I've got a clash with the <https://intranet.example.com/x>board prep.

            {TeamsBlock}
            -----Original Appointment-----
            From: Alex Smith
            Sent: 20 November 2026 09:00
            Subject: Weekly 1-2-1 - Alex
            """;
        Assert.Equal("Could we make it 30 minutes later? I've got a clash with the board prep.", Notes.Comment(body));
    }

    [Fact]
    public void Quoted_history_after_a_reply_header_is_dropped()
    {
        var body = "Sounds good, see you there.\n\nFrom: Alex Smith <alex@example.com>\nSent: Monday, 23 November 2026 10:00\nTo: Pat Lee\nSubject: Planning\n\nThe agenda";
        Assert.Equal("Sounds good, see you there.", Notes.Comment(body));
        Assert.Equal("Fine by me", Notes.Comment("Fine by me\n\nOn Mon, 23 Nov 2026 at 10:00, Alex Smith wrote:\n> The agenda"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\r\n  ")]
    [InlineData(TeamsBlock)]
    [InlineData("[EXTERNAL]\n________________\nSent from Outlook for iOS")]
    public void Empty_or_boilerplate_only_has_no_note(string body) => Assert.Null(Notes.Comment(body));

    [Fact]
    public void An_html_body_is_read_as_text()
    {
        var body = new JsonObject
        {
            ["contentType"] = "html",
            ["content"] = "<html><head><style>p { margin: 0 }</style></head><body><p>Thanks &amp; see you then</p><div>"
                          + "<a href=\"https://example.com\">Microsoft Teams meeting</a></div><p>Meeting ID: 123</p></body></html>",
        };
        Assert.Equal("Thanks & see you then", Notes.Comment(Notes.Text(body)));
    }

    [Fact]
    public void A_long_note_is_one_line_of_about_300_characters()
    {
        var long_ = string.Join("\n\n", Enumerable.Repeat("This is a long explanation of why the time has to change.", 12));
        var note = Notes.Comment(long_)!;
        Assert.DoesNotContain('\n', note);
        Assert.EndsWith("…", note);
        Assert.InRange(note.Length, 200, Notes.Max + 1);
    }

    [Fact]
    public void What_changed_in_a_description()
    {
        Assert.Equal("Please read the plan first.", Notes.Changed("Agenda: roadmap\nAll welcome", "Agenda: roadmap\nPlease read the plan first.\nAll welcome"));
        Assert.Equal("removed: All welcome", Notes.Changed("Agenda: roadmap\nAll welcome", "Agenda: roadmap"));
        Assert.Null(Notes.Changed("Agenda: roadmap", "Agenda: roadmap"));
    }

    [Theory]
    [InlineData("New Time Proposed: Weekly 1-2-1 - Alex", "Weekly 1-2-1 - Alex")]
    [InlineData("Canceled: Planning", "Planning")]
    [InlineData("Accepted: RE: Planning", "Planning")]
    [InlineData("Planning: next steps", "Planning: next steps")]
    public void The_meeting_subject_without_its_prefix(string email, string meeting) => Assert.Equal(meeting, Notes.MeetingSubject(email));
}

public sealed class NoteSettingsTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private const string Base = """
        queue_file = "queue.json"
        [[folder]]
        name = "Triage"

        """;

    [Fact]
    public void Quoting_is_on_unless_switched_off()
    {
        Assert.True(dir.Settings(Base).Notes.QuoteInDeleted);
        Assert.False(dir.Settings(Base + "[notes]\nquote_in_deleted = false\n").Notes.QuoteInDeleted);
        Assert.False(dir.Settings(Base + "[notes]\nquote_in_deleted = false\n").Notes.Wanted);
    }

    [Fact]
    public void Note_rules_are_read()
    {
        var notes = dir.Settings(Base + """
            [[note_rule]]
            name = "My 1:1"
            subject = '^Weekly 1-2-1 - Alex$'
            sender = 'pat@example\.com'
            why = "Pat's notes on changes to our 1:1"

            """).Notes;
        var rule = Assert.Single(notes.Rules);
        Assert.Equal(("My 1:1", "^Weekly 1-2-1 - Alex$", "pat@example\\.com", null), (rule.Name, rule.Subject, rule.Sender, rule.Organiser));
    }

    [Theory]
    [InlineData("[notes]\nquote = true\n", "it takes quote_in_deleted")]
    [InlineData("[notes]\nquote_in_deleted = \"yes\"\n", "should be true or false")]
    [InlineData("[[note_rule]]\nname = \"x\"\nwhy = \"y\"\n", "at least one of subject, organiser, sender")]
    [InlineData("[[note_rule]]\nsubject = \"x\"\nfrom = \"y\"\n", "'from' isn't a setting")]
    [InlineData("[[note_rule]]\nsender = \"(\"\n", "isn't a valid regular expression")]
    [InlineData("[[note_rule]]\nname = \"a\"\nsubject = \"x\"\n[[rule]]\nsender = \"x\"\naction = \"review\"\n[[note_rule]]\nname = \"b\"\nsubject = \"y\"\n",
        "keep each kind together")]
    public void Mistakes_are_refused(string text, string message)
    {
        var error = Assert.Throws<SettingsException>(() => dir.Settings(Base + text));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void A_rule_needs_every_condition_it_gives()
    {
        var rule = new NoteRule { Name = "r", Subject = "^Weekly 1-2-1 - Alex$", Sender = "pat@example" };
        Assert.True(rule.Matches("Weekly 1-2-1 - Alex", "New Time Proposed: Weekly 1-2-1 - Alex", ("Alex Smith", Me), ("Pat Lee", "pat@example.com")));
        Assert.False(rule.Matches("Weekly 1-2-1 - Alex", "New Time Proposed: Weekly 1-2-1 - Alex", ("Alex Smith", Me), ("Sam Jones", "sam@example.com")));
        Assert.False(rule.Matches("Planning", "New Time Proposed: Planning", ("Alex Smith", Me), ("Pat Lee", "pat@example.com")));
        var byName = new NoteRule { Name = "n", Sender = "^Pat Lee\\b", Organiser = "alex@" };
        Assert.True(byName.Matches("Anything", "Anything", ("Alex Smith", Me), ("Pat Lee", "pat@example.com")));
        Assert.False(byName.Matches("Anything", "Anything", ("", ""), ("Pat Lee", "pat@example.com")));   // organiser unknown: no match
    }
}

/// <summary>A mailbox with an Inbox (replies, proposals) and an Invites folder (invitations, cancellations): each message
/// has a body, and (for a meeting message) the meeting it's about. Moves take a message out of its folder.</summary>
internal sealed class NotesGraph : IGraph
{
    public readonly Dictionary<string, List<JsonObject>> Folders = new() { ["Inbox"] = [], ["Invites"] = [] };
    public readonly Dictionary<string, JsonObject> Full = new(StringComparer.Ordinal);
    public readonly List<string> BodyReads = [];
    public readonly List<string> Moved = [];

    public void Add(string folder, JsonObject message, JsonObject full)
    {
        Folders[folder].Add(message);
        Full[message.Req("id")] = full;
    }

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (url.StartsWith("/me/mailFolders?", StringComparison.Ordinal))
            return Folders.Keys.Select(f => new JsonObject { ["id"] = f, ["displayName"] = f, ["childFolderCount"] = 0 }).ToList();
        var folder = url.Split('/')[3];
        return Folders[folder].Select(m => (JsonObject)m.DeepClone()).ToList();
    }

    public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var id = url.Split('/')[3].Split('?')[0];
        if (method == "POST" && url.EndsWith("/move", StringComparison.Ordinal))
        {
            foreach (var list in Folders.Values)
                list.RemoveAll(m => m.Str("id") == id);
            Moved.Add(id);
            return [];
        }
        if (method != "GET")
            return [];
        if (!Full.TryGetValue(id, out var full))
            throw new M365Graph.GraphRequestException(System.Net.HttpStatusCode.NotFound, method, url, "");
        if (url.Contains("$select=body", StringComparison.Ordinal))
        {
            Assert.Equal("outlook.body-content-type=\"text\"", headers?["Prefer"]);
            BodyReads.Add(id);
            return new JsonObject { ["body"] = full["body"]!.DeepClone() };
        }
        return (JsonObject)full.DeepClone();
    }
}

public sealed class NoteRunTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 12, 9, 30, 0);
    private static readonly (string, string, string)[] WithPat = [("Alex Smith", Me, "required"), ("Pat Lee", "pat@example.com", "required")];

    private const string Rule = """

        [[note_rule]]
        name = "Notes on my 1:1 with Pat"
        subject = '^Weekly 1-2-1 - Alex$'
        sender = 'pat@example\.com'
        why = "Pat's notes on changes to our 1:1 (invented example)"

        """;

    public void Dispose() => dir.Dispose();

    private Settings Config(string extra = Rule) => dir.Settings("notes_file = \"notes.json\"\n" + InviteSettings.Replace("[invites]",
        "[invites]\nproposals_folders = ['Inbox']\naccept_proposals = true\nrequests_folders = ['Invites']") + extra
        + "\n[[rule]]\nname = \"Meeting acceptances\"\nsender = '@example\\.com$'\nmatch = '^Accepted: '\naction = \"delete\"\nfolders = [\"Inbox\"]\n"
        + "\n[[folder]]\nname = \"Inbox\"\nunmatched = \"leave\"\n");

    private static JsonObject From(string name, string address) => new() { ["emailAddress"] = new JsonObject { ["name"] = name, ["address"] = address } };

    private static JsonObject Text(string text) => new() { ["contentType"] = "text", ["content"] = text };

    private static JsonObject Utc(string t) => new() { ["dateTime"] = $"{t}:00.0000000", ["timeZone"] = "UTC" };

    /// <summary>Pat proposes Fri 13 Nov 12:00-12:30 for Thursday's 1:1, with a note.</summary>
    private static void Proposal(NotesGraph graph, string body, string id = "p1")
    {
        graph.Add("Inbox", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.eventMessageResponse", ["id"] = id, ["internetMessageId"] = $"<{id}@example.com>",
            ["subject"] = "New Time Proposed: Weekly 1-2-1 - Alex", ["receivedDateTime"] = "2026-11-12T08:00:00Z", ["from"] = From("Pat Lee", "pat@example.com"),
        }, new JsonObject
        {
            ["id"] = id, ["proposedNewTime"] = new JsonObject { ["start"] = Utc("2026-11-13T12:00"), ["end"] = Utc("2026-11-13T12:30") },
            ["startDateTime"] = Utc("2026-11-12T12:00"), ["endDateTime"] = Utc("2026-11-12T12:30"),
            ["event"] = new JsonObject { ["id"] = "one", ["isOrganizer"] = true, ["subject"] = "Weekly 1-2-1 - Alex" },
            ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = $"<html><body><p>{body}</p></body></html>" },
        });
    }

    /// <summary>An invitation (or update) to Sam's planning meeting, already accepted, in the Invites folder.</summary>
    private static void Invitation(NotesGraph graph, string id, string received, string body, string eventId = "plan", string answer = "accepted",
        string subject = "Planning")
    {
        graph.Add("Invites", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.eventMessageRequest", ["id"] = id, ["subject"] = subject, ["receivedDateTime"] = received,
            ["from"] = From("Sam Jones", "sam@example.com"),
        }, new JsonObject
        {
            ["id"] = id, ["body"] = Text(body),
            ["event"] = eventId.Length == 0 ? new JsonObject() : new JsonObject
            {
                ["id"] = eventId, ["subject"] = "Planning", ["responseStatus"] = new JsonObject { ["response"] = answer },
                ["organizer"] = From("Sam Jones", "sam@example.com"),
            },
        });
    }

    private RunReport Run(Settings config, NotesGraph graph, List<string>? log = null)
    {
        var calendar = new FakeCalendar(
        [
            Ev("one-12", "Weekly 1-2-1 - Alex", "2026-11-12", "12:00", "12:30", organiser: "Alex Smith", orgAddr: Me, attendees: WithPat,
                type: "occurrence", seriesMasterId: "one"),
        ]);
        using (Clock.Use(new FixedTime(now, TimeZoneInfo.Utc)))
            return Triage.Run(config, new Mailbox(graph, WriteDoor.Live(graph).Mail), calendar, false, (log ?? []).Add);
    }

    private static List<JsonObject> Deleted(Settings config) => Triage.Deleted(config, everything: true);

    [Fact]
    public void An_accepted_proposal_keeps_its_note_and_the_rule_raises_it_once()
    {
        var config = Config();
        var graph = new NotesGraph();
        Proposal(graph, "Running late from another meeting - can we start at 12?<br>________________<br>Microsoft Teams meeting<br>Join the meeting now");
        Run(config, graph);
        Assert.Equal(["p1"], graph.Moved);   // accepted and deleted as before
        var record = Assert.Single(Deleted(config), r => r.Str("rule") == "Accepted: new time proposed, and free");
        Assert.Equal(("Running late from another meeting - can we start at 12?", "Pat Lee wrote"), (record.Str("note"), record.Str("note_by")));
        Assert.Empty(graph.BodyReads);   // the proposal was read whole already

        var item = Assert.Single(Triage.Queue(config));
        Assert.Equal((Notes.Type, "Notes on my 1:1 with Pat", "pat@example.com"), (item.Str("type"), item.Str("rule"), item.Str("from")));
        Assert.Equal("WORTH A LOOK: Accepted Pat Lee's new time for 'Weekly 1-2-1 - Alex', Fri 13 Nov 12:00–12:30. "
                     + "Pat Lee wrote: \"Running late from another meeting - can we start at 12?\"", item.Str("check"));

        // The next run: the proposal has gone, the note waits; never raised twice.
        Run(config, graph);
        Assert.Single(Triage.Queue(config));
        Triage.Keep(config, ["1"], Quiet);
        Assert.Empty(Triage.Queue(config));
        Run(config, graph);
        Assert.Empty(Triage.Queue(config));

        var output = new StringWriter();
        Cli.Run(["--config", config.Path, "deleted", "--all"], new CliHost { Out = output, Err = output });
        Assert.Contains("      Pat Lee wrote: \"Running late from another meeting - can we start at 12?\"", output.ToString());
    }

    [Fact]
    public void A_note_is_raised_once_even_if_its_email_comes_round_again()
    {
        var config = Config();
        var raised = new JsonObject();
        var graph = new NotesGraph();
        Proposal(graph, "See you then");
        var notes = NoteTaker.For(config, new Mailbox(graph, WriteDoor.Live(graph).Mail), raised, false, Quiet)!;
        var message = graph.Folders["Inbox"][0];
        var email = new NoteTaker.Email(message, "Inbox", NoteTaker.Kind.Reply, "Accepted.", "Weekly 1-2-1 - Alex", ("", Me));
        Assert.Equal("See you then", notes.Take(email)!.Text);
        Assert.Equal("See you then", notes.Take(email)!.Text);   // quoted each time...
        Assert.Single(notes.Items);                              // ...raised once
        Assert.True(raised.ContainsKey("note:<p1@example.com>"));
    }

    [Fact]
    public void Someone_else_s_note_is_quoted_but_not_raised()
    {
        var config = Config(Rule.Replace(@"pat@example\.com", @"lee@example\.com"));   // the rule names someone else
        var graph = new NotesGraph();
        Proposal(graph, "Can we do Friday instead?");
        Run(config, graph);
        Assert.Equal("Can we do Friday instead?", Assert.Single(Deleted(config)).Str("note"));
        Assert.Empty(Triage.Queue(config));
    }

    [Fact]
    public void Off_nothing_is_quoted_or_read()
    {
        var config = Config(extra: "\n[notes]\nquote_in_deleted = false\n");
        var graph = new NotesGraph();
        Proposal(graph, "Running late");
        Invitation(graph, "i1", "2026-11-10T08:00:00Z", "Agenda: roadmap");
        Run(config, graph);
        Assert.All(Deleted(config), r => Assert.False(r.Has("note")));
        Assert.Empty(graph.BodyReads);
        Assert.False(File.Exists(config.NotesFile));
        Assert.Empty(Triage.Queue(config));
    }

    [Fact]
    public void Off_a_rule_still_raises_its_note_but_deleted_doesnt_quote_it()
    {
        var config = Config(extra: Rule + "\n[notes]\nquote_in_deleted = false\n");
        var graph = new NotesGraph();
        Proposal(graph, "Running late");
        Run(config, graph);
        Assert.False(Assert.Single(Deleted(config)).Has("note"));
        Assert.Contains("Pat Lee wrote: \"Running late\"", Assert.Single(Triage.Queue(config)).Str("check"));
    }

    [Fact]
    public void An_update_shows_what_changed_in_the_description()
    {
        var config = Config();
        var graph = new NotesGraph();
        const string teams = "\n________________\nMicrosoft Teams meeting\nMeeting ID: 1";
        Invitation(graph, "i1", "2026-11-10T08:00:00Z", "Agenda: roadmap\nAll welcome" + teams);
        Run(config, graph);
        var first = Assert.Single(Deleted(config));
        Assert.Equal("Answered invitations", first.Str("rule"));
        Assert.False(first.Has("note"));   // the first version: nothing to compare with, so no note (never the whole body)

        Invitation(graph, "i2", "2026-11-11T08:00:00Z", "Agenda: roadmap\nPlease read the plan first.\nAll welcome" + teams.Replace("1", "2"),
            subject: "Updated: Planning");
        Run(config, graph);
        var update = Deleted(config).Last();
        Assert.Equal(("Please read the plan first.", "description changed"), (update.Str("note"), update.Str("note_by")));
    }

    [Fact]
    public void Several_versions_in_one_run_are_compared_oldest_first()
    {
        var config = Config();
        var graph = new NotesGraph();
        Invitation(graph, "i2", "2026-11-11T08:00:00Z", "Agenda: roadmap\nNow in room 2", subject: "Updated: Planning");
        Invitation(graph, "i1", "2026-11-10T08:00:00Z", "Agenda: roadmap");
        Run(config, graph);
        Assert.Equal([null, "Now in room 2"], Deleted(config).Select(r => r.Str("note")));
    }

    [Fact]
    public void An_update_with_no_earlier_version_or_no_meeting_has_no_note()
    {
        var config = Config();
        var graph = new NotesGraph();
        Invitation(graph, "i1", "2026-11-10T08:00:00Z", "Agenda: changed", subject: "Updated: Planning");
        Invitation(graph, "i2", "2026-11-10T09:00:00Z", "Declined and gone", eventId: "", subject: "Retro");
        Run(config, graph);
        Assert.Equal(2, Deleted(config).Count);
        Assert.All(Deleted(config), r => Assert.False(r.Has("note")));
    }

    [Fact]
    public void An_acceptance_a_rule_deletes_keeps_its_comment()
    {
        var config = Config();
        var graph = new NotesGraph();
        graph.Add("Inbox", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.eventMessageResponse", ["id"] = "a1", ["internetMessageId"] = "<a1@example.com>",
            ["subject"] = "Accepted: Weekly 1-2-1 - Alex", ["receivedDateTime"] = "2026-11-12T08:30:00Z", ["from"] = From("Pat Lee", "pat@example.com"),
            ["bodyPreview"] = "I'll bring the draft",
        }, new JsonObject { ["id"] = "a1", ["body"] = Text("I'll bring the draft\n\n[EXTERNAL]") });
        Run(config, graph);
        var record = Assert.Single(Deleted(config));
        Assert.Equal(("Meeting acceptances", "I'll bring the draft"), (record.Str("rule"), record.Str("note")));
        Assert.Equal(["a1"], graph.BodyReads);
        Assert.Contains("WORTH A LOOK: Deleted Pat Lee's reply 'Accepted: Weekly 1-2-1 - Alex' (rule 'Meeting acceptances'). Pat Lee wrote:",
            Assert.Single(Triage.Queue(config)).Str("check"));
    }

    [Fact]
    public void A_note_item_is_kept_not_deleted_or_filed()
    {
        var config = Config();
        var graph = new NotesGraph();
        Proposal(graph, "Running late");
        Run(config, graph);
        Assert.Contains("already deleted", Assert.Throws<LookupException>(() => Triage.Delete(config, null, ["1"], Quiet)).Message);
        Assert.Contains("keep", Assert.Throws<LookupException>(() => Triage.File(config, null, ["1"], "Inbox/Somewhere", Quiet)).Message);
    }
}
