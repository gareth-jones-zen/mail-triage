using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using M365Graph;
using MailTriage.Interim;

namespace MailTriage.Tests;

/// <summary>The Python behaviours the port reproduces: the regex dialect and its guard, isoformat, strftime,
/// code points, state files, locks, time zones and the Graph adapter.</summary>
public sealed class PyCompatTests
{
    [Theory]
    [InlineData("(?P<name>x)", "named group")]
    [InlineData("(?P=name)", "named group")]
    [InlineData("(?<name>x)", "named group")]
    [InlineData(@"\p{L}", @"\p")]
    [InlineData("[a-z-[aeiou]]", "subtraction")]
    [InlineData("x{,3}", "{,n}")]
    [InlineData("a*+", "possessive")]
    [InlineData("a{2}+", "possessive")]
    [InlineData("abc(?i)", "middle")]
    [InlineData("(?x)a b", "inline flag x")]
    [InlineData("(?a)x", "inline flag a")]
    [InlineData("(?u)x", "inline flag u")]
    [InlineData("(?L)x", "inline flag L")]
    [InlineData(@"(a)\1", "backreference")]
    [InlineData(@"\G", @"\G")]
    [InlineData(@"\z", @"\z")]
    [InlineData(@"\N{EM DASH}", @"\N")]
    [InlineData(@"\U0001F600", @"\U")]
    [InlineData("(?>a)", "atomic")]
    [InlineData("[[:alpha:]]", "POSIX")]
    [InlineData(@"[\W]", @"\W inside")]
    public void the_guard_refuses_what_python_and_dotnet_read_differently(string pattern, string why)
    {
        var error = Assert.Throws<PyRegexUnsupportedException>(() => PyRegex.Translate(pattern));
        Assert.Contains(why, error.Message);
    }

    [Theory]
    [InlineData(@"@news\.example\.com$")]
    [InlineData(@"^[^\n]*\bsprint (\d+|notes)\b")]
    [InlineData(@"@([\w-]+\.)*salesforce\.com$")]
    [InlineData(@"^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b")]
    [InlineData("(?is)<tr.*?</tr>")]
    [InlineData("(?i:abc)def")]
    [InlineData(@"you.re invited|connect:")]
    [InlineData(@"[]a]")]
    [InlineData(@"\Z")]
    [InlineData(@"Jordan\ Lee\s+(annual leave|on leave)\b")]
    public void the_patterns_in_use_are_accepted(string pattern) => Assert.Null(PyRegex.Problem(pattern));

    [Theory]
    [InlineData(@"\w", "\u0301", false)]        // a combining mark: not a word character in Python
    [InlineData(@"\w", "\u2160", true)]         // Roman numeral one (Nl): a word character in Python
    [InlineData(@"\w", "\u00b2", true)]         // superscript two (No)
    [InlineData(@"\w", "\u203f", false)]        // undertie (Pc): only "_" in Python
    [InlineData(@"\s", "\u001c", true)]         // a file separator is whitespace in Python
    [InlineData(@"x\Z", "x\n", false)]          // \Z is the very end in Python
    [InlineData(@"x$", "x\n", true)]            // $ also matches before a final newline, in both
    [InlineData(@"\bcafe\b", "cafe\u0301", true)]
    [InlineData(@"\bé", "x\u00e9", false)]
    public void translated_patterns_behave_as_pythons(string pattern, string text, bool expected) =>
        Assert.Equal(expected, PyRegex.IsMatch(pattern, text));

    [Fact]
    public void match_and_fullmatch_anchor_as_python_does()
    {
        Assert.NotNull(PyRegex.Get("bank holiday\\b", true).MatchStart("Bank holiday (England)"));
        Assert.Null(PyRegex.Get("bank holiday\\b", true).MatchStart("The bank holiday"));
        Assert.True(PyRegex.Get(PyRegex.Escape("Weekly 1-2-1 - Dan"), true).FullMatch("weekly 1-2-1 - dan"));
        Assert.False(PyRegex.Get(PyRegex.Escape("Weekly 1-2-1 - Dan"), true).FullMatch("Weekly 1-2-1 - Dan\n"));
        Assert.True(PyRegex.Get("a|ab").FullMatch("ab"));
    }

    [Fact]
    public void re_escape_is_pythons() =>
        Assert.Equal(@"Jordan\ O'Neil\-Smith\ \#1\&\~", PyRegex.Escape("Jordan O'Neil-Smith #1&~"));

    [Fact]
    public void a_slow_pattern_times_out_and_never_deletes()
    {
        using var dir = new TempDir();
        var settings = dir.Settings(TriageBase.SettingsText.Replace(@"sender = '@news\.example\.com$'", "sender = '^(a+)+$'"));
        var graph = new FakeGraph([TriageBase.Message(1, new string('a', 40) + "!")]);
        var report = Triage.Run(settings, new Mailbox(graph, WriteDoor.Live(graph).Mail), null, false, Make.Quiet);
        Assert.Equal((0, 1), (report.Deleted, report.New));
        Assert.Contains("ran out of time", Triage.Queue(settings)[0].Req("check"));
        Assert.DoesNotContain(graph.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void isoformat_in_every_form()
    {
        var t = new DateTime(2026, 11, 10, 15, 6, 29).AddTicks(1010200);
        Assert.Equal("2026-11-10T15:06", PyTime.Iso(t, "minutes"));
        Assert.Equal("2026-11-10T15:06:29", PyTime.Iso(t, "seconds"));
        Assert.Equal("2026-11-10T15:06:29.101020", PyTime.Iso(t, "microseconds"));
        Assert.Equal("2026-11-10T15:06:29.101020", PyTime.Iso(t));
        Assert.Equal("2026-11-10T15:06:29", PyTime.Iso(new DateTime(2026, 11, 10, 15, 6, 29)));
        Assert.Equal("2026-11-10T15:06:29.000000", PyTime.Iso(new DateTime(2026, 11, 10, 15, 6, 29), "microseconds"));
        Assert.Equal(new DateTime(2026, 11, 10, 15, 6, 29).AddTicks(1010200), PyTime.Micro(t.AddTicks(7)));
    }

    [Fact]
    public void strftime_in_english_and_only_the_supported_directives()
    {
        var t = new DateTime(2026, 11, 13, 9, 5, 0);
        Assert.Equal("Fri 13 Nov 09:05", PyTime.Strftime(t, "%a %d %b %H:%M"));
        Assert.Equal("13/11", PyTime.Strftime(t, "%d/%m"));
        Assert.Equal("Friday 13 November 2026 09AM 100%", PyTime.Strftime(t, "%A %d %B %Y %I%p 100%%"));
        Assert.True(PyTime.Supported("%d/%m"));
        Assert.False(PyTime.Supported("%-d/%m"));
        Assert.False(PyTime.Supported("%j"));
        using var dir = new TempDir();
        Assert.Throws<SettingsException>(() => dir.Settings(Make.InviteSettings.Replace("days_ahead = 30", "days_ahead = 30\ndate_format = '%e/%m'")));
    }

    [Fact]
    public void code_points_not_utf16_units()
    {
        Assert.Equal("ab😀", PyText.Head("ab😀cd", 3));
        Assert.Equal(1, PyText.Head("😀", 1).Length / 2);
        Assert.True(PyText.CompareCodePoints("😀", "\uff5c") > 0);       // U+1F600 sorts after U+FF5C, as in Python
        Assert.True(string.CompareOrdinal("😀", "\uff5c") < 0);          // ordinal gets it the other way round
        Assert.Equal("x", PyText.Strip("\u001c x \u2028"));
        Assert.Equal(["a", "b"], PyText.Split(" a\u001fb "));
        Assert.True(PyText.IsDigit("²"));
        Assert.Throws<FormatException>(() => PyText.Int("²"));
        Assert.Equal(3, PyText.Int("٣"));
    }

    [Fact]
    public void the_flow_digest_fails_on_a_superscript_count_as_python_does() =>
        Assert.Throws<FormatException>(() => Checks.FlowFailures("<tr><td>A flow</td><td>²</td></tr>"));

    [Fact]
    public void state_files_are_written_as_python_wrote_them()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state.json");
        var data = new JsonObject { ["review"] = new JsonArray((JsonNode)new JsonObject { ["subject"] = "Café 😀 \"x\"\n", ["n"] = 1 }), ["empty"] = new JsonArray(), ["obj"] = new JsonObject() };
        StateFiles.Save(path, data);
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);   // no byte order mark
        Assert.Equal("{\r\n  \"review\": [\r\n    {\r\n      \"subject\": \"Café 😀 \\\"x\\\"\\n\",\r\n      \"n\": 1\r\n    }\r\n  ],\r\n  \"empty\": [],\r\n  \"obj\": {}\r\n}",
                     File.ReadAllText(path));
        Assert.Equal(StateFiles.Text(data), StateFiles.Text(StateFiles.Load(path, () => null)));
    }

    [Fact]
    public void setting_an_existing_key_keeps_its_place_as_a_python_dict_does()
    {
        var o = new JsonObject { ["a"] = 1, ["n"] = 2, ["b"] = 3 };
        o["n"] = 9;
        Assert.Equal(["a", "n", "b"], o.Select(p => p.Key));
    }

    [Fact]
    public async Task a_lock_released_after_an_await_can_be_taken_by_another_thread()
    {
        var name = $"mail-triage-test-{Guid.NewGuid():N}";
        var held = Exclusive.Acquire(name, 1, "test");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await Task.Run(() => { }, TestContext.Current.CancellationToken);   // likely another thread now
        held.Dispose();
        var other = await Task.Run(() => ThreadHeldLock.TryAcquire(name, 1), TestContext.Current.CancellationToken);
        Assert.NotNull(other);
        other!.Dispose();
    }

    [Fact]
    public void a_held_lock_excludes_another_holder_and_says_so_in_pythons_words()
    {
        var name = $"mail-triage-test-{Guid.NewGuid():N}";
        using var held = Exclusive.Acquire(name, 1, "triage run");
        var error = Assert.Throws<TimeoutException>(() => Exclusive.Acquire(name, 0.06, "triage run"));
        Assert.Equal("Another triage run didn't finish within 0 minutes.", error.Message);
        Assert.Null(Exclusive.SingleInstance(name));
    }

    [Fact]
    public void london_time_as_zoneinfo_reads_it_under_invariant_globalisation()
    {
        var zone = PyTime.Zone("GMT Standard Time");
        Assert.Equal(TimeSpan.Zero, PyTime.Aware(new DateTime(2026, 11, 12, 12, 0, 0), zone).Offset);
        Assert.Equal(TimeSpan.FromHours(1), PyTime.Aware(new DateTime(2026, 7, 1, 12, 0, 0), zone).Offset);
        // 01:30 on 25 Oct 2026 happens twice: Python's fold=0 takes the first (summer time).
        Assert.Equal(TimeSpan.FromHours(1), PyTime.Aware(new DateTime(2026, 10, 25, 1, 30, 0), zone).Offset);
        // 01:30 on 29 Mar 2026 doesn't happen: Python takes the offset from before the change.
        Assert.Equal(TimeSpan.Zero, PyTime.Aware(new DateTime(2026, 3, 29, 1, 30, 0), zone).Offset);
        Assert.Equal(new DateTime(2026, 7, 1, 13, 0, 0), PyTime.Local(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), zone));
    }

    [Fact]
    public void the_m365_adapter_passes_calls_and_headers_through()
    {
        var seen = new List<string>();
        var adapter = new M365GraphAdapter(
            (url, method, body, scopes, headers) =>
            {
                seen.Add($"{method.Method} {url} {body?.ToJsonString()} {string.Join(",", scopes ?? [])} {string.Join(",", headers?.Values ?? [])}");
                return Task.FromResult(new JsonObject { ["ok"] = true });
            },
            (url, scopes, headers) => Pages(url, headers));
        static async IAsyncEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers)
        {
            await Task.Yield();
            yield return new JsonObject { ["id"] = "1", ["prefer"] = headers?["Prefer"] };
            yield return JsonValue.Create(5);   // not an object: skipped
            yield return new JsonObject { ["id"] = "2" };
        }
        var calendar = new Calendar(adapter, WriteDoor.Refusing().Calendar);
        var events = calendar.Events(new DateOnly(2026, 11, 12), 7);
        Assert.Equal(["1", "2"], events.Select(e => e.Req("id")));
        Assert.Equal("outlook.timezone=\"GMT Standard Time\"", events[0].Req("prefer"));
        Assert.True(adapter.Request("/me/x", "POST", new JsonObject(), ["Mail.ReadWrite"]).Truthy("ok"));
        Assert.Equal(["POST /me/x {} Mail.ReadWrite "], seen);
    }

    [Fact]
    public void the_m365_client_takes_the_settings_sign_in()
    {
        using var dir = new TempDir();
        var settings = dir.Settings("login_hint = \"Me@Example.com\"\ntenant = \"t1\"\nmsal_cache = \"cache.json\"\n[[folder]]\nname = \"x\"\n");
        var client = M365GraphAdapter.Create(settings, false);
        Assert.Equal((Path.Combine(dir.Path, "cache.json"), "t1", "Me@Example.com", (bool?)false), (client.Options.CachePath, client.Options.Tenant, client.Options.LoginHint, client.Options.Interactive));
        Assert.Equal(["Mail.ReadWrite"], client.Options.Scopes);
        Assert.Equal("me@example.com", settings.Email);
        _ = typeof(SignInNeededException);
    }

    [Fact]
    public void urlencode_is_pythons() =>
        Assert.Equal("%24filter=receivedDateTime+ge+2026-09-17T00%3A00%3A00Z&%24select=id%2Csubject",
                     Url.Encode([("$filter", "receivedDateTime ge 2026-09-17T00:00:00Z"), ("$select", "id,subject")]));
}

/// <summary>Settings read as tomllib and config.py read them.</summary>
public sealed class SettingsEdgeTests
{
    [Fact]
    public void an_unquoted_date_is_read_as_its_text()
    {
        using var dir = new TempDir();
        var settings = dir.Settings("[[folder]]\nname = \"x\"\nsince = 2026-09-17\n");
        Assert.Equal("2026-09-17", settings.Folders[0].Since);
    }

    [Fact]
    public void paths_expand_variables_and_home_and_resolve_against_the_settings_folder()
    {
        using var dir = new TempDir();
        var settings = dir.Settings("queue_file = \"%LOCALAPPDATA%/x/q.json\"\nstate_file = \"sub/../r.json\"\nlog_file = \"~/l.log\"\n[[folder]]\nname = \"x\"\n");
        Assert.Equal(PyPath.Pure(Environment.GetEnvironmentVariable("LOCALAPPDATA") + @"\x\q.json"), settings.QueueFile);
        Assert.Equal(Path.Combine(dir.Path, "r.json"), settings.StateFile);
        Assert.Equal(PyPath.Pure(Environment.GetEnvironmentVariable("USERPROFILE") + @"\l.log"), settings.LogFile);
    }

    [Fact]
    public void subject_format_takes_first_and_name_and_doubled_braces()
    {
        Assert.Equal("1:1 {Sam} - Sam Jones", PyFormat.Named("1:1 {{{first}}} - {name}", new Dictionary<string, string> { ["first"] = "Sam", ["name"] = "Sam Jones" }));
        Assert.Throws<SettingsException>(() => PyFormat.Named("{0}", new Dictionary<string, string>()));
        Assert.Throws<SettingsException>(() => PyFormat.Named("{first:>5}", new Dictionary<string, string> { ["first"] = "x" }));
    }

    [Fact]
    public void bad_patterns_anywhere_are_refused_at_load()
    {
        using var dir = new TempDir();
        Assert.Throws<SettingsException>(() => dir.Settings(Make.InviteSettings.Replace("protected = ['^Weekly 1-2-1 - Alex$']", "protected = ['(?P<x>a)']")));
        Assert.Throws<SettingsException>(() => dir.Settings("[[folder]]\nname = \"x\"\n[[rule]]\nsender = '\\p{L}'\naction = 'delete'\n"));
    }

    [Fact]
    public void flow_check_wildcards_only_in_the_file_name()
    {
        using var dir = new TempDir();
        Assert.Throws<SettingsException>(() => dir.Settings("[[folder]]\nname = \"x\"\n[[flow_check]]\nflow = 'f'\noutput = 'out*/a.html'\n"));
    }

    [Fact]
    public void the_template_has_python_line_ends_and_no_rules() =>
        Assert.Contains("# sender = '@news\\.example\\.com$'", Settings.Template);
}
