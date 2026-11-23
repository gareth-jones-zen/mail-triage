using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage.Tests;

/// <summary>`mail-triage hold` (1.18.0): a hold in your own calendar for the find-a-slot skill, never with attendees.</summary>
public sealed class HoldTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly DateTime now = new(2026, 11, 23, 9, 0, 0);   // Mon 23 Nov 2026
    private int sleeps;

    private const string Rooms = """

        [hold]
        rooms = [
            { name = "MTG Room 3.A.2", address = "MTGRoom3.A.2@example.com" },
            "MTGRoom2.B.1@example.com",
        ]
        """;

    public HoldTests() => dir.Settings(Make.InviteSettings);

    public void Dispose() => dir.Dispose();

    /// <summary>A Graph that answers the create with an id, and the read-back with a webLink and, when the hold booked a
    /// room, the room's answers in turn (the last repeated).</summary>
    private sealed class Graph : IGraph
    {
        public List<(string Method, string Url, JsonNode? Body)> Calls { get; } = [];
        public string[] RoomAnswers { get; init; } = ["accepted"];
        private int reads;

        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) => [];

        public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls.Add((method, url, body?.DeepClone()));
            if (method == "POST" && url == "/me/events")
                return new JsonObject { ["id"] = "hold-1" };
            if (method == "GET" && url.StartsWith("/me/events/hold-1", StringComparison.Ordinal))
            {
                var back = new JsonObject { ["id"] = "hold-1", ["webLink"] = "https://outlook.example/hold-1" };
                var created = Calls.First(c => c.Method == "POST").Body as JsonObject;
                if (created?["attendees"] is JsonArray { Count: > 0 } attendees)
                {
                    var answer = RoomAnswers[Math.Min(reads++, RoomAnswers.Length - 1)];
                    var room = attendees[0]!.DeepClone();
                    room["status"] = new JsonObject { ["response"] = answer };
                    back["attendees"] = new JsonArray(room);
                }
                return back;
            }
            return [];
        }
    }

    private (int Code, string Out) Run(Graph graph, params string[] args)
    {
        var output = new StringWriter();
        int code;
        using (Clock.Use(new FixedTime(now)))
            code = Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), .. args],
                new CliHost { Out = output, Err = output, Echo = false, Graph = (_, _) => graph, Sleep = _ => sleeps++ });
        return (code, output.ToString());
    }

    [Fact]
    public void without_yes_it_shows_the_hold_and_writes_nothing()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS in FibreHub", "--body", "Paul, Jake, Dan");
        Assert.Equal(0, code);
        Assert.Contains("Would create in your calendar: \"Hold: ITS in FibreHub\", Wed 25 Nov 16:15-17:00, busy, no attendees.", output);
        Assert.Contains("Nothing written.", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void with_yes_it_creates_an_event_with_no_attendees_and_records_it()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS in FibreHub", "--online", "--location", "Teams", "--yes");
        Assert.Equal(0, code);
        var create = Assert.Single(graph.Calls, c => c.Method == "POST");
        Assert.Equal("/me/events", create.Url);
        var body = (JsonObject)create.Body!;
        Assert.Empty((JsonArray)body["attendees"]!);
        Assert.Equal("busy", body.Req("showAs"));
        Assert.Equal("normal", body.Req("sensitivity"));
        Assert.Equal("2026-11-25T16:15:00", body["start"]!.Req("dateTime"));
        Assert.Equal("2026-11-25T17:00:00", body["end"]!.Req("dateTime"));
        Assert.True(body["isOnlineMeeting"]!.GetValue<bool>());
        Assert.Contains("Held: \"Hold: ITS in FibreHub\"", output);
        Assert.Contains("https://outlook.example/hold-1", output);
        var settings = Settings.Load(Path.Combine(dir.Path, "config.toml"));
        var held = Assert.Single(Triage.Deleted(settings));
        Assert.Equal("Holds", held.Req("rule"));
        Assert.Contains("held Wed 25/11 16:15-17:00, no attendees", held.Req("did"));
        var (_, listed) = Run(graph, "deleted");
        Assert.Contains("Holds (1)", listed);
    }

    [Fact]
    public void a_hold_with_attendees_is_refused_by_the_writer()
    {
        var body = Holds.Body("x", now.AddDays(1), now.AddDays(1).AddHours(1), "GMT Standard Time");
        ((JsonArray)body["attendees"]!).Add((JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = "a@example.com" } });
        var graph = new Graph();
        Assert.Throws<InvalidOperationException>(() => WriteDoor.Live(graph).Calendar.Hold(body));
        Assert.Empty(graph.Calls);
    }

    [Fact]
    public void the_command_line_has_no_way_to_add_attendees() =>
        Assert.Throws<UsageException>(() => Cli.Parse(["hold", "2026-11-25T16:15", "17:00", "x", "--attendee", "a@example.com"]));

    [Theory]
    [InlineData("2026-11-20T10:00", "11:00", "is in the past")]
    [InlineData("2026-11-25T16:15", "16:00", "must be after the start")]
    public void past_starts_and_ends_before_starts_are_refused(string start, string end, string message)
    {
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", start, end, "x", "--yes");
        Assert.Equal(2, code);
        Assert.Contains(message, output);
        Assert.DoesNotContain(graph.Calls, c => c.Method == "POST");
    }

    [Fact]
    public void body_and_body_file_together_are_refused() =>
        Assert.Throws<UsageException>(() => Cli.Parse(["hold", "2026-11-25T16:15", "17:00", "x", "--body", "a", "--body-file", "b.txt"]));

    // --room (1.23.0): one room from [hold] rooms, as a resource attendee; never a person.

    [Fact]
    public void with_a_listed_room_the_preview_says_it_books_the_room_and_no_people()
    {
        dir.Settings(Make.InviteSettings + Rooms);
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--room", "mtg room 3.a.2");
        Assert.Equal(0, code);
        Assert.Contains("Would create in your calendar: \"Hold: ITS\", Wed 25 Nov 16:15-17:00, busy, books MTG Room 3.A.2; no people invited.", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void with_a_listed_room_it_adds_only_the_room_as_a_resource_and_reports_it_accepted()
    {
        dir.Settings(Make.InviteSettings + Rooms);
        var graph = new Graph { RoomAnswers = ["none", "accepted"] };
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--room", "MTGRoom3.A.2@example.com", "--yes");
        Assert.Equal(0, code);
        var create = Assert.Single(graph.Calls, c => c.Method == "POST");
        var body = (JsonObject)create.Body!;
        var attendee = Assert.Single((JsonArray)body["attendees"]!)!;
        Assert.Equal("resource", attendee.Req("type"));
        Assert.Equal("MTGRoom3.A.2@example.com", attendee["emailAddress"]!.Req("address"));
        Assert.Equal("MTG Room 3.A.2", body["location"]!.Req("displayName"));
        Assert.Equal("MTGRoom3.A.2@example.com", body["location"]!.Req("locationEmailAddress"));
        Assert.Contains("books MTG Room 3.A.2; no people invited", output);
        Assert.Contains("room MTG Room 3.A.2 accepted: it's booked.", output);
        Assert.Equal(1, sleeps);   // read back until the room answered; created once
        var held = Assert.Single(Triage.Deleted(Settings.Load(Path.Combine(dir.Path, "config.toml"))));
        Assert.Contains("room MTG Room 3.A.2 (accepted), no people invited", held.Req("did"));
        var (_, listed) = Run(graph, "deleted");
        Assert.Contains("Holds (1)", listed);
        Assert.Contains("MTG Room 3.A.2", listed);
    }

    [Fact]
    public void a_room_that_declines_is_reported_and_not_retried()
    {
        dir.Settings(Make.InviteSettings + Rooms);
        var graph = new Graph { RoomAnswers = ["declined"] };
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--room", "MTG Room 3.A.2", "--yes");
        Assert.Equal(0, code);
        Assert.Single(graph.Calls, c => c.Method == "POST");
        Assert.Contains("room MTG Room 3.A.2 DECLINED", output);
        Assert.Contains("Not retried", output);
        Assert.Equal(0, sleeps);
        var held = Assert.Single(Triage.Deleted(Settings.Load(Path.Combine(dir.Path, "config.toml"))));
        Assert.Contains("room MTG Room 3.A.2 (declined)", held.Req("did"));
    }

    [Fact]
    public void a_room_that_never_answers_is_reported_after_a_few_reads()
    {
        dir.Settings(Make.InviteSettings + Rooms);
        var graph = new Graph { RoomAnswers = ["none"] };
        var (_, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--room", "MTGRoom2.B.1@example.com", "--yes");
        Assert.Single(graph.Calls, c => c.Method == "POST");
        Assert.Equal(3, sleeps);
        Assert.Contains("room MTGRoom2.B.1@example.com hasn't answered yet", output);
    }

    [Theory]
    [InlineData("alex.colleague@example.com")]
    [InlineData("MTGRoom9.Z.9@example.com")]
    public void an_address_not_in_hold_rooms_is_refused_before_anything_is_written(string address)
    {
        dir.Settings(Make.InviteSettings + Rooms);
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--room", address, "--yes");
        Assert.Equal(2, code);
        Assert.Contains("isn't one of [hold] rooms (MTG Room 3.A.2, MTGRoom2.B.1@example.com)", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method == "POST");
    }

    [Fact]
    public void without_hold_rooms_no_room_can_be_booked()
    {
        var graph = new Graph();
        var (code, output) = Run(graph, "hold", "2026-11-25T16:15", "17:00", "Hold: ITS", "--room", "MTGRoom3.A.2@example.com", "--yes");
        Assert.Equal(2, code);
        Assert.Contains("there's no [hold] rooms in the settings", output);
        Assert.DoesNotContain(graph.Calls, c => c.Method == "POST");
    }

    [Fact]
    public void the_writer_refuses_a_person_even_alongside_an_allowed_room()
    {
        var room = new HoldRoom("MTG Room 3.A.2", "MTGRoom3.A.2@example.com");
        var allowed = new[] { room.Address };
        var person = Holds.Body("x", now.AddDays(1), now.AddDays(1).AddHours(1), "GMT Standard Time", room: room);
        ((JsonArray)person["attendees"]!).Add((JsonNode)new JsonObject
        {
            ["emailAddress"] = new JsonObject { ["address"] = "a@example.com" }, ["type"] = "required",
        });
        var graph = new Graph();
        Assert.Throws<InvalidOperationException>(() => WriteDoor.Live(graph).Calendar.Hold(person, allowed));

        // A person dressed up as a resource is still refused: only an allowed room's address passes.
        var disguised = Holds.Body("x", now.AddDays(1), now.AddDays(1).AddHours(1), "GMT Standard Time",
            room: new HoldRoom("Alex", "alex@example.com"));
        Assert.Throws<InvalidOperationException>(() => WriteDoor.Live(graph).Calendar.Hold(disguised, allowed));

        // And an allowed room passes only with the allow-list given.
        var booked = Holds.Body("x", now.AddDays(1), now.AddDays(1).AddHours(1), "GMT Standard Time", room: room);
        Assert.Throws<InvalidOperationException>(() => WriteDoor.Live(graph).Calendar.Hold(booked));
        Assert.Empty(graph.Calls);
    }

    [Fact]
    public void room_and_location_together_are_refused() =>
        Assert.Throws<UsageException>(() => Cli.Parse(["hold", "2026-11-25T16:15", "17:00", "x", "--room", "r@example.com", "--location", "y"]));

    [Theory]
    [InlineData("""
        [hold]
        rooms = [{ name = "MTG Room 3.A.1", address = "MTGRoom3.A.1@example.com" }, { name = "MTG Room 3.A.2", address = "MTGRoom3.A.2@example.com" }]
        """, 2)]
    [InlineData("""
        [hold]
        rooms = ["MTGRoom3.A.1@example.com"]
        """, 1)]
    public void hold_rooms_are_read_as_tables_or_addresses(string toml, int count) =>
        Assert.Equal(count, dir.Settings(Make.InviteSettings + "\n" + toml).HoldRooms.Count);

    [Theory]
    [InlineData("rooms = [\"not an address\"]")]
    [InlineData("rooms = [{ name = \"X\" }]")]
    [InlineData("rooms = [\"a@example.com\", \"A@example.com\"]")]
    [InlineData("room = [\"a@example.com\"]")]
    public void bad_hold_rooms_are_refused(string line) =>
        Assert.Throws<SettingsException>(() => dir.Settings(Make.InviteSettings + "\n[hold]\n" + line + "\n"));
}
