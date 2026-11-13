using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace MailTriage;

/// <summary>A settings problem: the message says which setting and what's wrong (Python's ValueError).</summary>
public sealed class SettingsException(string message) : Exception(message);

public sealed class Folder(string name, string? since = null, string unmatched = "review")
{
    public string Name { get; } = name;
    public string? Since { get; } = since;
    /// <summary>"leave": only the rules act here; mail no rule names is left alone (an inbox).</summary>
    public string Unmatched { get; set; } = unmatched;
}

public sealed class Rule
{
    public required string Name { get; init; }
    public required string Sender { get; init; }
    public required string Action { get; init; }
    public string? Match { get; init; }
    public string? Check { get; init; }
    public string Why { get; init; } = "";
    public string? To { get; init; }
    public List<string>? Folders { get; init; }

    public bool Matches(string sender, string text) =>
        PyRegex.IsMatch(Sender, sender, true) && (Match is null || PyRegex.IsMatch(Match, text, true));
}

public sealed record FlowCheck(string Flow, string Output, string? Pending = null);

public sealed class InviteRule
{
    public required string Name { get; init; }
    public string Action { get; init; } = "skip";
    public string? Subject { get; init; }
    public string? Organiser { get; init; }
    public bool? AllDay { get; init; }
    public List<string>? ShowAs { get; init; }
    public bool? ViaGroup { get; init; }
    public bool? Optional { get; init; }
    public long? MinPeople { get; init; }
    public string Why { get; init; } = "";
}

public sealed record MovableBlock(string Subject, string? Within = null, string? Before = null);

public sealed class Invites
{
    public const string DefaultLeavePattern = @"^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b";
    public long DaysAhead { get; set; } = 42;
    public List<string> FixedBlocks { get; set; } = [];
    public List<string> CommuteBlocks { get; set; } = [];
    public List<MovableBlock> Movable { get; set; } = [];
    public List<string> Protected { get; set; } = [];
    public string LeaveNote { get; set; } = "On annual leave";
    public string DateFormat { get; set; } = "%d/%m";
    public (string Start, string End) WorkingHours { get; set; } = ("08:00", "18:00");
    public (string Start, string End) TravelHours { get; set; } = ("06:00", "21:00");
    public List<string> RequestsFolders { get; set; } = [];
    public bool AcceptShortened { get; set; }
    public List<string> ProposalsFolders { get; set; } = [];
    public bool AcceptProposals { get; set; }
    public string LeavePattern { get; set; } = DefaultLeavePattern;
    public List<InviteRule> Rules { get; set; } = [];
}

public sealed class OneToOne(string name, string email, string subject)
{
    public string Name { get; } = name;
    public string Email { get; set; } = email;
    public string Subject { get; } = subject;
}

public sealed class OneToOnes
{
    public const string DefaultLeavePattern = @"^{name}\s+(annual leave|on leave)\b";
    public List<OneToOne> Reports { get; set; } = [];
    public bool Act { get; set; }
    public long MaxActions { get; set; } = 20;
    public double CancelDays { get; set; } = 3;
    public string Midday { get; set; } = "13:00";
    public long? DaysAhead { get; set; }
    public List<string> BankHolidayCategories { get; set; } = ["Holiday"];
    public string LeavePattern { get; set; } = DefaultLeavePattern;
    public string ReportNote { get; set; } = "Cancelling as you're on leave - enjoy the time off";
    public string MyNote { get; set; } = "Cancelling as I'm on leave";
    public string BankHolidayNote { get; set; } = "Cancelling for the bank holiday";
}

/// <summary>The settings file: which folders to triage, and the rules (config.py). Regular expressions are Python's,
/// and only the part of Python's dialect that .NET reads the same way is accepted (PyRegex).</summary>
public sealed class Settings
{
    public static readonly string[] Actions = ["delete", "review", "check", "file"];
    private static readonly Dictionary<string, string> Aliases = new() { ["resolve"] = "delete" };   // the first name for delete
    private const string HoursPattern = "([01]\\d|2[0-3]):[0-5]\\d-([01]\\d|2[0-4]):[0-5]\\d";

    public const string Template = """
        # mail-triage settings. See the README.
        # login_hint = "you@example.com"

        # The folders to triage. Point Outlook rules at these, and leave your inbox alone.
        # [[folder]]
        # name = "Newsletters"

        # Rules, first match wins. Anything no rule matches is queued for you, never deleted.
        # [[rule]]
        # name = "Vendor newsletters"
        # sender = '@news\.example\.com$'
        # action = "delete"
        # why = "News, not action."

        """;

    public static Func<string, string?> Env { get; set; } = Environment.GetEnvironmentVariable;

    public static string AppData => PyPath.Pure(Env("APPDATA") is { Length: > 0 } a ? a : PyPath.Home(Env));
    public static string Local => PyPath.Pure(System.IO.Path.Combine(Env("LOCALAPPDATA") is { Length: > 0 } l ? l : PyPath.Home(Env), "mail-triage"));
    public static string DefaultPath => System.IO.Path.Combine(AppData, "mail-triage", "config.toml");

    public required string Path { get; init; }
    public List<Folder> Folders { get; } = [];
    public List<Rule> Rules { get; } = [];
    public List<FlowCheck> FlowChecks { get; } = [];
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public string QueueFile { get; init; } = System.IO.Path.Combine(Local, "queue.json");
    public string StateFile { get; init; } = System.IO.Path.Combine(Local, "raised.json");
    public string DeletedFile { get; init; } = System.IO.Path.Combine(Local, "deleted.json");
    public string AcceptedFile { get; init; } = System.IO.Path.Combine(Local, "accepted.json");
    public string LogFile { get; init; } = System.IO.Path.Combine(Local, "mail-triage.log");
    public string Notify { get; init; } = "new";
    public long EveryMinutes { get; init; } = 15;
    public (string Start, string End) Hours { get; init; } = ("07:00", "19:00");
    public bool Weekdays { get; set; } = true;
    public string? Email { get; set; }
    public Invites? Invites { get; set; }
    public OneToOnes? OneToOnes { get; set; }

    /// <summary>The first rule that matches and applies in this folder. A rule naming folders applies only in them;
    /// a folder with unmatched = "leave" (an inbox) gets only the rules that name it.</summary>
    public Rule? RuleFor(string sender, string text, Folder? folder = null)
    {
        bool Applies(Rule rule)
        {
            if (folder is null)
                return true;
            var named = (rule.Folders ?? []).Select(f => f.ToLowerInvariant()).ToList();
            return named.Count > 0 || folder.Unmatched == "leave" ? named.Contains(folder.Name.ToLowerInvariant()) : true;
        }
        return Rules.FirstOrDefault(rule => Applies(rule) && rule.Matches(sender, text));
    }

    private static string ExpandPath(string value) => PyPath.ExpandVars(PyPath.ExpandUser(value, Env), Env);

    /// <summary>config._path: ~ and %VARIABLES% expanded; relative to the settings file's folder, resolved.</summary>
    internal static string ToPath(string value, string baseDir)
    {
        var path = ExpandPath(value);
        return PyPath.IsAbsolute(path) ? PyPath.Pure(path) : PyPath.ResolveAgainst(baseDir, path);
    }

    /// <summary>config._pattern: a glob, expanded like a path but not resolved.</summary>
    internal static string ToPattern(string value, string baseDir)
    {
        var path = ExpandPath(value);
        if (PyPath.IsAbsolute(path))
            return PyPath.Pure(path);
        var p = path.Replace('/', '\\');
        if (p.StartsWith('\\'))
            return PyPath.Pure(System.IO.Path.GetPathRoot(baseDir)!.TrimEnd('\\') + p);
        return PyPath.Pure(baseDir + "\\" + path);
    }

    /// <summary>Reads and checks the settings (config.load). Throws FileNotFoundException or SettingsException.</summary>
    public static Settings Load(string? path = null)
    {
        var full = PyPath.Resolve(path is { Length: > 0 } ? path : Env("MAIL_TRIAGE_CONFIG") is { Length: > 0 } e ? e : DefaultPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `mail-triage config` to create one.");
        TomlTable raw;
        try
        {
            raw = TomlSerializer.Deserialize(File.ReadAllText(full, new UTF8Encoding(false, true)), TomlContext.Default.TomlTable) ?? [];
        }
        catch (Exception error) when (error is not IOException and not UnauthorizedAccessException)
        {
            throw new SettingsException($"{full} isn't valid TOML: {error.Message}");
        }
        return FromToml(raw, full);
    }

    internal static Settings FromToml(TomlTable raw, string full)
    {
        var baseDir = System.IO.Path.GetDirectoryName(full)!;
        var hours = Toml.Str(raw.Get("hours") ?? "07:00-19:00");
        if (!PyRegex.Get(HoursPattern).FullMatch(hours))
            throw new SettingsException($"hours in {full} should look like \"07:00-19:00\".");
        var every = Toml.Int(raw.Get("every_minutes") ?? 15L);
        if (every < 1)
            throw new SettingsException($"every_minutes in {full} should be at least 1.");
        var notify = raw.Get("notify") ?? "new";
        if (notify is not ("new" or "errors"))
            throw new SettingsException($"notify in {full} should be \"new\" or \"errors\".");
        string PathOr(string key, string fallback) => Toml.Truthy(raw.Get(key)) ? ToPath(Toml.Str(raw.Get(key)), baseDir) : fallback;
        var hourParts = hours.Split('-');
        var settings = new Settings
        {
            Path = full,
            LoginHint = raw.Get("login_hint") is { } hint ? Toml.Str(hint) : null,
            Tenant = raw.Get("tenant") is { } tenant ? Toml.Str(tenant) : "organizations",
            MsalCache = Toml.Truthy(raw.Get("msal_cache")) ? ToPath(Toml.Str(raw.Get("msal_cache")), baseDir) : null,
            QueueFile = PathOr("queue_file", System.IO.Path.Combine(Local, "queue.json")),
            StateFile = PathOr("state_file", System.IO.Path.Combine(Local, "raised.json")),
            DeletedFile = PathOr("deleted_file", System.IO.Path.Combine(Local, "deleted.json")),
            AcceptedFile = PathOr("accepted_file", System.IO.Path.Combine(Local, "accepted.json")),
            LogFile = PathOr("log_file", System.IO.Path.Combine(Local, "mail-triage.log")),
            Notify = (string)notify,
            EveryMinutes = every,
            Hours = (hourParts[0], hourParts[1]),
            Weekdays = Toml.Truthy(raw.Get("weekdays") ?? true),
        };

        foreach (var item in Toml.Tables(raw.Get("folder")))
        {
            if (!Toml.Truthy(item.Get("name")))
                throw new SettingsException($"A [[folder]] in {full} has no name.");
            var name = Toml.Str(item.Get("name"));
            var since = item.Get("since");
            if (since is not null && !PyRegex.Get(@"\d{4}-\d{2}-\d{2}").FullMatch(Toml.Str(since)))
                throw new SettingsException($"since for folder {name} in {full} should be a date such as \"2026-09-17\".");
            var unmatched = item.Get("unmatched") ?? "review";
            if (unmatched is not ("review" or "leave"))
                throw new SettingsException($"unmatched for folder {name} in {full} should be \"review\" or \"leave\".");
            settings.Folders.Add(new Folder(name, Toml.Truthy(since) ? Toml.Str(since!) : null, (string)unmatched));
        }

        var i = 0;
        foreach (var item in Toml.Tables(raw.Get("rule")))
        {
            i++;
            var name = item.Get("name") is { } n ? Toml.Str(n) : $"rule {i}";
            if (!Toml.Truthy(item.Get("sender")))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} has no sender.");
            var given = item.Get("action") is { } a ? Toml.Str(a) : "";
            var action = Aliases.GetValueOrDefault(given, given);
            if (!Actions.Contains(action))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} has action {Toml.Repr(item.Get("action"))}: use delete, review, check or file.");
            if (action == "file" && !Toml.Truthy(item.Get("to")))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} has action file but no `to` folder.");
            if (action == "check" && !(item.Get("check") is string c && Checks.All.ContainsKey(c)))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} needs check = one of {string.Join(", ", Checks.All.Keys.Order(PyStr.Order))}.");
            foreach (var key in new[] { "sender", "match" })
                if (Toml.Truthy(item.Get(key)) && PyRegex.Problem(Toml.Str(item.Get(key))) is { } problem)
                    throw new SettingsException($"Rule {Py.ReprString(name)} in {full}: {key} isn't a valid regular expression ({problem}).");
            settings.Rules.Add(new Rule
            {
                Name = name,
                Sender = Toml.Str(item.Get("sender")),
                Action = action,
                Match = item.Get("match") is { } m ? Toml.Str(m) : null,
                Check = item.Get("check") is { } ch ? Toml.Str(ch) : null,
                Why = item.Get("why") is { } w ? Toml.Str(w) : "",
                To = item.Get("to") is { } to ? Toml.Str(to) : null,
                Folders = Toml.Truthy(item.Get("folders")) ? Toml.Strings(item.Get("folders")) : null,
            });
        }

        foreach (var item in Toml.Tables(raw.Get("flow_check")))
        {
            if (!Toml.Truthy(item.Get("flow")) || !Toml.Truthy(item.Get("output")))
                throw new SettingsException($"A [[flow_check]] in {full} needs flow and output.");
            var output = ToPattern(Toml.Str(item.Get("output")), baseDir);
            var pending = Toml.Truthy(item.Get("pending")) ? ToPattern(Toml.Str(item.Get("pending")), baseDir) : null;
            foreach (var pattern in new[] { output, pending })
                if (pattern is not null && System.IO.Path.GetDirectoryName(pattern) is { } dir && (dir.Contains('*') || dir.Contains('?') || dir.Contains('[')))
                    throw new SettingsException($"A [[flow_check]] in {full} has wildcards in a folder name ({pattern}): only the file name may have them.");
            settings.FlowChecks.Add(new FlowCheck(Toml.Str(item.Get("flow")), output, pending));
        }

        var email = Toml.Truthy(raw.Get("email")) ? Toml.Str(raw.Get("email")) : Toml.Truthy(raw.Get("login_hint")) ? Toml.Str(raw.Get("login_hint")) : "";
        settings.Email = email.Length > 0 ? email.ToLowerInvariant() : null;
        if (raw.Get("invites") is { } invitesValue)
        {
            var section = invitesValue as TomlTable ?? throw new SettingsException($"[invites] in {full} should be a table.");
            if (settings.Email is null)
                throw new SettingsException($"[invites] in {full} needs email (or login_hint) set, to tell you from other attendees.");
            var rules = new List<InviteRule>();
            var r = 0;
            foreach (var item in Toml.Tables(raw.Get("invite_rule")))
            {
                r++;
                var name = item.Get("name") is { } n ? Toml.Str(n) : $"invite rule {r}";
                var action = item.Get("action") ?? "skip";
                if (action is not ("skip" or "review"))
                    throw new SettingsException($"Invite rule {Py.ReprString(name)} in {full}: action is skip or review (nothing acts on its own yet).");
                foreach (var key in new[] { "subject", "organiser" })
                    if (Toml.Truthy(item.Get(key)) && PyRegex.Problem(Toml.Str(item.Get(key))) is { } problem)
                        throw new SettingsException($"Invite rule {Py.ReprString(name)} in {full}: {key} isn't a valid regular expression ({problem}).");
                rules.Add(new InviteRule
                {
                    Name = name,
                    Action = (string)action,
                    Subject = item.Get("subject") is { } s ? Toml.Str(s) : null,
                    Organiser = item.Get("organiser") is { } o ? Toml.Str(o) : null,
                    AllDay = item.Get("all_day") is { } ad ? Toml.Truthy(ad) : null,
                    ShowAs = item.Get("show_as") is { } sa ? Toml.Strings(sa) : null,
                    ViaGroup = item.Get("via_group") is { } vg ? Toml.Truthy(vg) : null,
                    Optional = item.Get("optional") is { } op ? Toml.Truthy(op) : null,
                    MinPeople = item.Get("min_people") is { } mp ? Toml.Int(mp) : null,
                    Why = item.Get("why") is { } w ? Toml.Str(w) : "",
                });
            }
            var movable = new List<MovableBlock>();
            foreach (var item in Toml.Tables(raw.Get("movable_block")))
            {
                var within = item.Get("within");
                if (!Toml.Truthy(item.Get("subject")) || within is not (null or "day"))
                    throw new SettingsException($"A [[movable_block]] in {full} needs subject, and within (if given) is \"day\".");
                movable.Add(new MovableBlock(Toml.Str(item.Get("subject")), (string?)within, item.Get("before") is { } b ? Toml.Str(b) : null));
            }
            (string, string) Span(string name, (string, string) fallback)
            {
                var value = Toml.Str(section.Get(name) ?? $"{fallback.Item1}-{fallback.Item2}");
                if (!PyRegex.Get(HoursPattern).FullMatch(value))
                    throw new SettingsException($"{name} in [invites] in {full} should look like \"08:00-18:00\".");
                var parts = value.Split('-');
                return (parts[0], parts[1]);
            }
            var invites = new Invites
            {
                DaysAhead = Toml.Int(section.Get("days_ahead") ?? 42L),
                FixedBlocks = Toml.Strings(section.Get("fixed_blocks")),
                CommuteBlocks = Toml.Strings(section.Get("commute_blocks")),
                Movable = movable,
                Protected = Toml.Strings(section.Get("protected")),
                LeavePattern = section.Get("leave_pattern") is { } lp ? Toml.Str(lp) : Invites.DefaultLeavePattern,
                LeaveNote = section.Get("leave_note") is { } ln ? Toml.Str(ln) : "On annual leave",
                DateFormat = section.Get("date_format") is { } df ? Toml.Str(df) : "%d/%m",
                WorkingHours = Span("working_hours", ("08:00", "18:00")),
                TravelHours = Span("travel_hours", ("06:00", "21:00")),
                RequestsFolders = Toml.Strings(section.Get("requests_folders")),
                AcceptShortened = Toml.Truthy(section.Get("accept_shortened") ?? false),
                ProposalsFolders = Toml.Strings(section.Get("proposals_folders")),
                AcceptProposals = Toml.Truthy(section.Get("accept_proposals") ?? false),
                Rules = rules,
            };
            // Checked here so a pattern .NET would read differently is refused at load, not mid-run (the guard).
            if (!PyTime.Supported(invites.DateFormat))
                throw new SettingsException($"date_format in [invites] in {full} uses a directive this version doesn't support (only %{string.Join(" %", PyTime.Directives.ToCharArray())}).");
            foreach (var (what, pattern) in invites.FixedBlocks.Select(p => ("fixed_blocks", p)).Concat(invites.CommuteBlocks.Select(p => ("commute_blocks", p)))
                         .Concat(invites.Protected.Select(p => ("protected", p))).Concat([("leave_pattern", invites.LeavePattern)])
                         .Concat(movable.Select(m => ("movable_block subject", m.Subject)))
                         .Concat(movable.Where(m => m.Before is not null).Select(m => ("movable_block before", m.Before!))))
                if (PyRegex.Problem(pattern) is { } problem)
                    throw new SettingsException($"{what} in {full} isn't a valid regular expression ({problem}).");
            settings.Invites = invites;
        }
        if (raw.Get("one_to_ones") is { } oneValue)
        {
            var section = oneValue as TomlTable ?? throw new SettingsException($"[one_to_ones] in {full} should be a table.");
            if (settings.Invites is null)
                throw new SettingsException($"[one_to_ones] in {full} needs an [invites] section (your email, working_hours, leave_pattern).");
            var midday = Toml.Str(section.Get("midday") ?? "13:00");
            if (!PyRegex.Get("([01]\\d|2[0-3]):[0-5]\\d").FullMatch(midday))
                throw new SettingsException($"midday in [one_to_ones] in {full} should look like \"13:00\".");
            var form = section.Get("subject_format") is { } sf ? Toml.Str(sf) : "Weekly 1-2-1 - {first}";
            var reports = new List<OneToOne>();
            foreach (var item in Toml.Tables(raw.Get("one_to_one")))
            {
                if (!Toml.Truthy(item.Get("name")) || !Toml.Truthy(item.Get("email")))
                    throw new SettingsException($"A [[one_to_one]] in {full} needs name and email.");
                var name = Toml.Str(item.Get("name"));
                var subject = Toml.Truthy(item.Get("subject")) ? Toml.Str(item.Get("subject"))
                    : PyFormat.Named(form, new Dictionary<string, string> { ["first"] = PyStr.Split(name)[0], ["name"] = name });
                reports.Add(new OneToOne(name, Toml.Str(item.Get("email")).ToLowerInvariant(), subject));
            }
            var pattern = section.Get("leave_pattern") is { } lp ? Toml.Str(lp) : OneToOnes.DefaultLeavePattern;
            if (!pattern.Contains("{name}"))
                throw new SettingsException($"leave_pattern in [one_to_ones] in {full} needs {{name}}, where the person's name goes.");
            foreach (var report in reports)
                if (PyRegex.Problem(pattern.Replace("{name}", PyRegex.Escape(report.Name))) is { } problem)
                    throw new SettingsException($"leave_pattern in [one_to_ones] in {full} isn't a valid regular expression ({problem}).");
            settings.OneToOnes = new OneToOnes
            {
                Reports = reports,
                Act = Toml.Truthy(section.Get("act") ?? false),
                MaxActions = Toml.Int(section.Get("max_actions") ?? 20L),
                CancelDays = Toml.Float(section.Get("cancel_days") ?? 3L),
                Midday = midday,
                DaysAhead = Toml.Truthy(section.Get("days_ahead")) ? Toml.Int(section.Get("days_ahead")!) : null,
                BankHolidayCategories = section.Get("bank_holiday_categories") is { } bh ? Toml.Strings(bh) : ["Holiday"],
                LeavePattern = pattern,
                ReportNote = section.Get("report_note") is { } rn ? Toml.Str(rn) : "Cancelling as you're on leave - enjoy the time off",
                MyNote = section.Get("my_note") is { } mn ? Toml.Str(mn) : "Cancelling as I'm on leave",
                BankHolidayNote = section.Get("bank_holiday_note") is { } bn ? Toml.Str(bn) : "Cancelling for the bank holiday",
            };
        }
        if (settings.Folders.Count == 0 && settings.Invites is null)
            throw new SettingsException($"{full} names no [[folder]] to triage, and no [invites].");
        return settings;
    }

    /// <summary>config.create_default.</summary>
    public static string CreateDefault()
    {
        var path = DefaultPath;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // CRLF, as Python's text mode wrote it, whatever line ends the source file has.
            File.WriteAllText(path, Template.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        }
        return path;
    }

    /// <summary>The loaded settings as JSON, in the same shape as the Python harness's dump of its Config (parity).</summary>
    public JsonObject Dump()
    {
        JsonArray Strs(IEnumerable<string>? items) => J.Array(items ?? []);
        JsonNode? Opt(string? s) => s is null ? null : JsonValue.Create(s);
        var o = new JsonObject
        {
            ["path"] = Path,
            ["folders"] = new JsonArray(Folders.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["since"] = Opt(f.Since), ["unmatched"] = f.Unmatched }).ToArray()),
            ["rules"] = new JsonArray(Rules.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name, ["sender"] = r.Sender, ["action"] = r.Action, ["match"] = Opt(r.Match), ["check"] = Opt(r.Check),
                ["why"] = r.Why, ["to"] = Opt(r.To), ["folders"] = r.Folders is null ? null : Strs(r.Folders),
            }).ToArray()),
            ["flow_checks"] = new JsonArray(FlowChecks.Select(f => (JsonNode)new JsonObject { ["flow"] = f.Flow, ["output"] = f.Output, ["pending"] = Opt(f.Pending) }).ToArray()),
            ["login_hint"] = Opt(LoginHint), ["tenant"] = Tenant, ["msal_cache"] = Opt(MsalCache), ["queue_file"] = QueueFile,
            ["state_file"] = StateFile, ["deleted_file"] = DeletedFile, ["accepted_file"] = AcceptedFile, ["log_file"] = LogFile,
            ["notify"] = Notify, ["every_minutes"] = EveryMinutes, ["hours"] = Strs([Hours.Start, Hours.End]), ["weekdays"] = Weekdays,
            ["email"] = Opt(Email),
        };
        if (Invites is { } i)
        {
            o["invites"] = new JsonObject
            {
                ["days_ahead"] = i.DaysAhead, ["fixed_blocks"] = Strs(i.FixedBlocks), ["commute_blocks"] = Strs(i.CommuteBlocks),
                ["movable"] = new JsonArray(i.Movable.Select(m => (JsonNode)new JsonObject { ["subject"] = m.Subject, ["within"] = Opt(m.Within), ["before"] = Opt(m.Before) }).ToArray()),
                ["protected"] = Strs(i.Protected), ["leave_note"] = i.LeaveNote, ["date_format"] = i.DateFormat,
                ["working_hours"] = Strs([i.WorkingHours.Start, i.WorkingHours.End]), ["travel_hours"] = Strs([i.TravelHours.Start, i.TravelHours.End]),
                ["requests_folders"] = Strs(i.RequestsFolders), ["accept_shortened"] = i.AcceptShortened,
                ["proposals_folders"] = Strs(i.ProposalsFolders), ["accept_proposals"] = i.AcceptProposals, ["leave_pattern"] = i.LeavePattern,
                ["rules"] = new JsonArray(i.Rules.Select(r => (JsonNode)new JsonObject
                {
                    ["name"] = r.Name, ["action"] = r.Action, ["subject"] = Opt(r.Subject), ["organiser"] = Opt(r.Organiser),
                    ["all_day"] = r.AllDay, ["show_as"] = r.ShowAs is null ? null : Strs(r.ShowAs), ["via_group"] = r.ViaGroup,
                    ["optional"] = r.Optional, ["min_people"] = r.MinPeople, ["why"] = r.Why,
                }).ToArray()),
            };
        }
        else
            o["invites"] = null;
        if (OneToOnes is { } t)
        {
            o["one_to_ones"] = new JsonObject
            {
                ["reports"] = new JsonArray(t.Reports.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["email"] = r.Email, ["subject"] = r.Subject }).ToArray()),
                ["act"] = t.Act, ["max_actions"] = t.MaxActions, ["cancel_days"] = PyJson.Float(t.CancelDays), ["midday"] = t.Midday,
                ["days_ahead"] = t.DaysAhead, ["bank_holiday_categories"] = Strs(t.BankHolidayCategories), ["leave_pattern"] = t.LeavePattern,
                ["report_note"] = t.ReportNote, ["my_note"] = t.MyNote, ["bank_holiday_note"] = t.BankHolidayNote,
            };
        }
        else
            o["one_to_ones"] = null;
        return o;
    }
}

/// <summary>str.format with named fields only: "{first}", and "{{" / "}}" for braces. Anything else in braces
/// (an index, a format spec, an unknown name) is refused, as Python would refuse it or read it differently.</summary>
public static class PyFormat
{
    public static string Named(string format, IReadOnlyDictionary<string, string> values)
    {
        var b = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '{')
            {
                if (i + 1 < format.Length && format[i + 1] == '{')
                {
                    b.Append('{');
                    i++;
                    continue;
                }
                var close = format.IndexOf('}', i);
                if (close < 0)
                    throw new SettingsException($"Single '{{' encountered in format string {Py.ReprString(format)}");
                var name = format[(i + 1)..close];
                if (!values.TryGetValue(name, out var value))
                    throw new SettingsException($"subject_format {Py.ReprString(format)} uses {{{name}}}: only {{first}} and {{name}} are known.");
                b.Append(value);
                i = close;
            }
            else if (c == '}')
            {
                if (i + 1 < format.Length && format[i + 1] == '}')
                {
                    b.Append('}');
                    i++;
                    continue;
                }
                throw new SettingsException($"Single '}}' encountered in format string {Py.ReprString(format)}");
            }
            else
                b.Append(c);
        }
        return b.ToString();
    }
}

/// <summary>Python's view of TOML values (tomllib): str(), bool(), int() and float() as Python applies them.</summary>
internal static class Toml
{
    public static object? Get(this TomlTable table, string key) => table.TryGetValue(key, out var value) ? value : null;

    public static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Py.Repr(d),
        TomlDateTime t when t.Kind == TomlDateTimeKind.LocalDate => t.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TomlDateTime t => t.ToString(),
        _ => value.ToString() ?? "",
    };

    public static string Repr(object? value) => value is null ? "None" : value is string s ? Py.ReprString(s) : Str(value);

    public static bool Truthy(object? value) => value switch
    {
        null => false,
        string s => s.Length > 0,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        TomlArray a => a.Count > 0,
        TomlTableArray a => a.Count > 0,
        TomlTable t => t.Count > 0,
        _ => true,
    };

    public static long Int(object value) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) => (long)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s => PyStr.Int(s),
        _ => throw new SettingsException($"{Repr(value)} isn't a whole number."),
    };

    public static double Float(object value) => value switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(PyStr.Strip(s), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new SettingsException($"{Repr(value)} isn't a number."),
    };

    public static List<string> Strings(object? value) => value switch
    {
        null => [],
        TomlArray a => a.Select(x => x is null ? "None" : Str(x)).ToList(),
        string s => s.Select(c => c.ToString()).ToList(),   // list("abc"), as Python would make it
        _ => throw new SettingsException($"{Repr(value)} should be a list."),
    };

    public static IEnumerable<TomlTable> Tables(object? value) => value switch
    {
        null => [],
        TomlTableArray a => a,
        TomlArray a => a.OfType<TomlTable>(),
        _ => throw new SettingsException($"{Repr(value)} should be a list of tables."),
    };
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
