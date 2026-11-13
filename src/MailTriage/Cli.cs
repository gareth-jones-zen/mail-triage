using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;
using WinBackground;

namespace MailTriage;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Whether log lines are echoed to Out (the console exe, unless --scheduled).</summary>
    public bool Echo { get; init; } = true;

    /// <summary>The Graph for a run: settings and interactive (null: decide by console) to an IGraph.</summary>
    public Func<Settings, bool?, IGraph> Graph { get; init; } =
        (settings, interactive) => new M365GraphAdapter(M365GraphAdapter.Create(settings, interactive));

    /// <summary>How sign-in asks for a token interactively.</summary>
    public Action<Settings> SignIn { get; init; } =
        settings => M365GraphAdapter.Create(settings, true).TokenAsync(Mailbox.Scopes, true).GetAwaiter().GetResult();

    public Func<string, ITask> Tasks { get; init; } = Autostart.Create;
    public Action<string, IReadOnlyList<string>> Notify { get; init; } = Toast.Notify;
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
}

/// <summary>A usage mistake on the command line (argparse's exit 2).</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>mail-triage: standing rules for your mail (cli.py). The same commands and exit codes as the Python
/// version; argparse's wording is approximated. Two .NET-only options choose how a run may write:
/// --writes live (the default) | journal | refuse, and --journal FILE (where journal mode records each write).</summary>
public static class Cli
{
    public const string Version = "1.0.0-dev";

    public const string Usage = """
        mail-triage: standing rules for your mail. Delete the routine; queue the rest for you, each email once.

            mail-triage config              create the settings file (if missing) and say where it is
            mail-triage sign-in             sign in to Microsoft (once; opens a browser)
            mail-triage run                 apply the rules: delete what they allow, queue the rest
            mail-triage run --dry-run       say what it would do, change nothing (needs only Mail.Read)
            mail-triage list                the queue, numbered (--json for the file as it is)
            mail-triage delete 2 5          delete queued emails, by number (or by Graph id)
            mail-triage keep 1 3            take them off the queue and leave them in the folder
            mail-triage clear               empty the queue (each email stays in its folder, never raised again)
            mail-triage deleted             what the rules deleted since the last clear, by rule (--all: the last 60 days)
            mail-triage which <sender> [subject]   which rule an email would meet

            With [invites] in the settings, unanswered meeting invites are queued too, each with a recommendation:
            mail-triage file 4 --to Inbox/Projects   move queued emails to a folder, left unread
            mail-triage invite accept 3     answer a queued invite: accept, tentative or decline
                --note "..."                    a note to the organiser; --silent answers without telling them
                --move                          with accept: move each clash you can move to its suggested slot
                --series                        with decline: the whole series (otherwise only the instances)
            mail-triage move <event id> 2026-11-11T16:00 [16:50]   move one of your own meetings or blocks
            mail-triage leave [--decline]   meetings in your leave you haven't declined (the next 60 days), and decline them
            mail-triage one-to-ones         with [one_to_ones]: what each run would do with your 1:1s and leave (changes nothing)

            mail-triage watch              run now, then every every_minutes in working hours; what autostart runs
            mail-triage autostart on        the watcher at every logon, and now (off, status)

        Options before the command: --config FILE, --version, and (this version only) --writes live|journal|refuse,
        --journal FILE.
        """;

    private static readonly string[] Commands =
        ["config", "sign-in", "run", "list", "delete", "resolve", "keep", "clear", "watch", "deleted", "raised", "which", "invite", "file",
         "move", "leave", "one-to-ones", "autostart"];

    /// <summary>The parsed command line.</summary>
    internal sealed class Args
    {
        public string? Config;
        public WriteMode Writes = WriteMode.Live;
        public string? Journal;
        public string Command = "";
        public List<string> Positional = [];
        public HashSet<string> Flags = [];
        public Dictionary<string, string> Values = [];

        public bool Flag(string name) => Flags.Contains(name);
        public string? Value(string name) => Values.GetValueOrDefault(name);
    }

    private static readonly Dictionary<string, (string[] Flags, string[] Values)> Options = new()
    {
        ["run"] = (["--dry-run", "--scheduled"], []),
        ["list"] = (["--json"], []),
        ["deleted"] = (["--all"], []),
        ["invite"] = (["--silent", "--series", "--move"], ["--note"]),
        ["file"] = ([], ["--to"]),
        ["leave"] = (["--decline"], ["--days", "--note"]),
        ["one-to-ones"] = (["--dry-run"], ["--days"]),
    };

    internal static Args Parse(string[] argv)
    {
        var a = new Args();
        var i = 0;
        for (; i < argv.Length; i++)
        {
            var arg = argv[i];
            string? Next(string name) => i + 1 < argv.Length ? argv[++i] : throw new UsageException($"argument {name}: expected one argument");
            if (arg == "--config")
                a.Config = Next("--config");
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                a.Config = arg["--config=".Length..];
            else if (arg == "--writes")
                a.Writes = Next("--writes") switch
                {
                    "live" => WriteMode.Live,
                    "journal" => WriteMode.Journal,
                    "refuse" => WriteMode.Refuse,
                    var other => throw new UsageException($"argument --writes: invalid choice: {Py.ReprString(other!)} (choose from 'live', 'journal', 'refuse')"),
                };
            else if (arg == "--journal")
                a.Journal = Next("--journal");
            else if (arg is "--version" or "-h" or "--help")
            {
                a.Command = arg;
                return a;
            }
            else if (arg.StartsWith('-'))
                throw new UsageException($"unrecognized arguments: {arg}");
            else
                break;
        }
        if (i >= argv.Length)
            throw new UsageException("the following arguments are required: command");
        a.Command = argv[i++];
        if (!Commands.Contains(a.Command))
            throw new UsageException($"argument command: invalid choice: {Py.ReprString(a.Command)}");
        var (flags, values) = Options.GetValueOrDefault(a.Command, ([], []));
        for (; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "--")
            {
                a.Positional.AddRange(argv[(i + 1)..]);
                break;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                var name = eq > 0 ? arg[..eq] : arg;
                if (flags.Contains(name) && eq < 0)
                    a.Flags.Add(name);
                else if (values.Contains(name))
                    a.Values[name] = eq > 0 ? arg[(eq + 1)..] : i + 1 < argv.Length ? argv[++i] : throw new UsageException($"argument {name}: expected one argument");
                else
                    throw new UsageException($"unrecognized arguments: {arg}");
            }
            else
                a.Positional.Add(arg);
        }
        void Count(int min, int max, string what)
        {
            if (a.Positional.Count < min)
                throw new UsageException($"the following arguments are required: {what}");
            if (a.Positional.Count > max)
                throw new UsageException($"unrecognized arguments: {string.Join(' ', a.Positional.Skip(max))}");
        }
        switch (a.Command)
        {
            case "delete" or "resolve" or "keep":
                Count(1, int.MaxValue, "refs");
                break;
            case "which":
                Count(1, 2, "sender");
                break;
            case "invite":
                Count(2, int.MaxValue, "action, refs");
                if (a.Positional[0] is not ("accept" or "tentative" or "decline"))
                    throw new UsageException($"argument action: invalid choice: {Py.ReprString(a.Positional[0])} (choose from 'accept', 'tentative', 'decline')");
                break;
            case "file":
                Count(1, int.MaxValue, "refs");
                if (a.Value("--to") is null)
                    throw new UsageException("the following arguments are required: --to");
                break;
            case "move":
                Count(2, 3, "event, start");
                break;
            case "autostart":
                Count(1, 1, "action");
                if (a.Positional[0] is not ("on" or "off" or "status"))
                    throw new UsageException($"argument action: invalid choice: {Py.ReprString(a.Positional[0])} (choose from 'on', 'off', 'status')");
                break;
            default:
                Count(0, 0, "");
                break;
        }
        foreach (var name in new[] { "--days" })
            if (a.Value(name) is { } days && !long.TryParse(days, out _))
                throw new UsageException($"argument {name}: invalid int value: {Py.ReprString(days)}");
        return a;
    }

    private static void Print(TextWriter w, string text) => w.WriteLine(Py.Lines(text));

    public static int Run(string[] argv, CliHost host)
    {
        Args args;
        try
        {
            args = Parse(argv);
        }
        catch (UsageException error)
        {
            Print(host.Err, "usage: mail-triage [-h] [--version] [--config CONFIG] {" + string.Join(",", Commands) + "} ...");
            Print(host.Err, $"mail-triage: error: {error.Message}");
            return 2;
        }
        if (args.Command == "--version")
        {
            Print(host.Out, Version);
            return 0;
        }
        if (args.Command is "-h" or "--help")
        {
            Print(host.Out, Usage);
            return 0;
        }
        if (args.Command == "config")
        {
            Print(host.Out, args.Config ?? Settings.CreateDefault());
            return 0;
        }
        Settings settings;
        try
        {
            settings = Settings.Load(args.Config);
        }
        catch (Exception error) when (error is FileNotFoundException or SettingsException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        var scheduled = args.Flag("--scheduled");
        var log = Logger.Create(settings.LogFile, host.Echo && !scheduled ? host.Out : null, stampEcho: false);

        if (args.Command == "autostart")
            return AutostartCommand(args.Positional[0], settings, host);
        if (args.Command == "watch")
            return Watcher.Watch(settings, (s, watchLog) => RunOnce(s, host.Graph(s, false), args, watchLog, false), host.Notify);
        if (args.Command == "which")
        {
            var rule = settings.RuleFor(args.Positional[0], args.Positional.Count > 1 ? args.Positional[1] : "");
            Print(host.Out, rule is not null
                ? $"{rule.Name}: {rule.Action}" + (rule.Check is not null ? $" ({rule.Check})" : "") + (rule.Why.Length > 0 ? $"\n  {rule.Why}" : "")
                : "No rule: it would be queued for review.");
            return 0;
        }
        if (args.Command == "list")
            return List(settings, args.Flag("--json"), host);
        if (args.Command == "deleted")
            return DeletedCommand(Triage.Deleted(settings, args.Flag("--all")), host);
        if (args.Command is "clear" or "raised" or "keep")
        {
            // Locked like a run, so a run in progress can't write back a queue that's just been cleared.
            try
            {
                using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))
                {
                    if (args.Command == "keep")
                        Triage.Keep(settings, args.Positional, log);
                    else
                        Triage.Clear(settings, log);
                }
            }
            catch (Exception error) when (error is LookupException or TimeoutException)
            {
                Print(host.Err, $"{(error is LookupException ? "LookupError" : "TimeoutError")}: {error.Message}");
                return 1;
            }
            return 0;
        }

        try
        {
            if (args.Command == "sign-in")
            {
                host.SignIn(settings);
                Print(host.Out, "Signed in.");
                return 0;
            }
            var graph = host.Graph(settings, scheduled ? false : null);
            if (args.Command == "one-to-ones")
                return OneToOnesCommand(args, settings, graph, host);
            if (args.Command is "invite" or "move" or "leave")
            {
                if (settings.Invites is null)
                {
                    Print(host.Err, $"{settings.Path} has no [invites] section.");
                    return 2;
                }
                return CalendarCommand(args, settings, graph, Door(args, graph, args.Writes, settings), log, host);
            }
            RunReport report;
            using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))
            {
                var door = Door(args, graph, args.Flag("--dry-run") ? WriteMode.Refuse : args.Writes, settings);
                if (args.Command is "delete" or "resolve")
                {
                    Triage.Delete(settings, new Mailbox(graph, door.Mail), args.Positional, log);
                    return 0;
                }
                if (args.Command == "file")
                {
                    Triage.File(settings, new Mailbox(graph, door.Mail), args.Positional, args.Value("--to")!, log);
                    return 0;
                }
                report = RunWith(settings, graph, door, args.Flag("--dry-run"), log);
            }
            if (scheduled && report.New > 0 && settings.Notify == "new")
                host.Notify($"{report.New} email{(report.New > 1 ? "s" : "")} to review", ["mail-triage list"]);
            return 0;
        }
        catch (SignInNeededException error)
        {
            log($"sign-in needed: {error.Message}");
            if (scheduled)
                host.Notify("mail-triage needs a sign-in", [PyStr.Head(error.Message, 300)]);
            return 1;
        }
        catch (LookupException error)
        {
            log(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            log($"failed: {error.GetType().Name}: {error.Message}");
            if (scheduled)
                host.Notify("mail-triage failed", [PyStr.Head($"{error.GetType().Name}: {error.Message}", 300), $"Log: {settings.LogFile}"]);
            return 1;
        }
    }

    /// <summary>The write door for this command line: journal mode appends each intended write to --journal (or the log).</summary>
    private static WriteDoor Door(Args args, IGraph graph, WriteMode mode, Settings settings)
    {
        if (mode != WriteMode.Journal)
            return WriteDoor.For(mode, graph);
        var file = args.Journal;
        var log = Logger.Create(file ?? settings.LogFile);
        return WriteDoor.Journaling(call => log($"journal (not sent): {call.Text}"));
    }

    private static RunReport RunWith(Settings settings, IGraph graph, WriteDoor door, bool dryRun, Action<string> log) =>
        Triage.Run(settings, new Mailbox(graph, door.Mail, dryRun), settings.Invites is null ? null : new Calendar(graph, door.Calendar), dryRun, log);

    private static RunReport RunOnce(Settings settings, IGraph graph, Args args, Action<string> log, bool dryRun) =>
        RunWith(settings, graph, Door(args, graph, args.Writes, settings), dryRun, log);

    private static int List(Settings settings, bool json, CliHost host)
    {
        var items = Triage.Queue(settings);
        if (json)
            Print(host.Out, PyJson.Dump(new JsonArray(items.Select(i => (JsonNode)i).ToArray()), 2, false));
        else
        {
            foreach (var item in items)
                Print(host.Out, $"{(item.Has("n") ? Py.Str(item.Get("n")) : "?"),3}. {PyStr.Head(item.Fmt("received", "None"), 10)}  {item.Fmt("from", "None")}\n"
                                + $"     {item.Fmt("subject", "None")}\n     [{item.Fmt("rule", "None")}]" + (item.Truthy("check") ? $" {Py.Str(item.Get("check"))}" : ""));
            if (items.Count == 0)
                Print(host.Out, "Nothing waiting.");
            var gone = Triage.Deleted(settings);
            if (gone.Count > 0)
                Print(host.Out, $"\n{gone.Count} deleted by the rules since the queue was last cleared: `mail-triage deleted`.");
        }
        return 0;
    }

    private static int DeletedCommand(List<JsonObject> items, CliHost host)
    {
        if (items.Count == 0)
        {
            Print(host.Out, "Nothing deleted by the rules since the queue was last cleared.");
            return 0;
        }
        static string When(JsonObject i) => PyStr.Head(i.Req("when"), 16).Replace("T", " ");
        Print(host.Out, $"{items.Count} deleted, {When(items[0])} to {When(items[^1])}:");
        var byRule = new List<(string Rule, List<JsonObject> Group)>();
        foreach (var item in items)
        {
            var rule = item.Str("rule") is { Length: > 0 } r ? r : "(no rule)";
            var index = byRule.FindIndex(g => g.Rule == rule);
            if (index < 0)
                byRule.Add((rule, [item]));
            else
                byRule[index].Group.Add(item);
        }
        foreach (var (rule, group) in byRule.OrderBy(g => -g.Group.Count))
        {
            Print(host.Out, $"\n  {rule} ({group.Count})");
            foreach (var item in group)
                Print(host.Out, $"    {PyStr.Head(item.Fmt("received", "None"), 10)}  {item.Fmt("from", "None")}: {PyStr.Head(item.Fmt("subject", "None"), 90)}"
                                + (item.Truthy("to") ? $"  -> filed to {Py.Str(item.Get("to"))}" : "")
                                + (item.Truthy("accepted") ? $"  -> accepted ({Py.Str(item.Get("accepted"))})" : "")
                                + (item.Truthy("did") ? $"  -> {Py.Str(item.Get("did"))}" : ""));
        }
        return 0;
    }

    private static int CalendarCommand(Args args, Settings settings, IGraph graph, WriteDoor door, Action<string> log, CliHost host)
    {
        var calendar = new Calendar(graph, door.Calendar);
        if (args.Command == "invite")
        {
            using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))
                Triage.Answer(settings, calendar, args.Positional[1..], args.Positional[0], args.Value("--note") ?? "", !args.Flag("--silent"), log,
                    series: args.Flag("--series"), move: args.Flag("--move"));
            return 0;
        }
        if (args.Command == "move")
        {
            var eventId = args.Positional[0];
            var e = calendar.Event(eventId);
            if (!e.Truthy("isOrganizer"))
            {
                Print(host.Err, "That's someone else's meeting: it can't be moved, only declined with a proposed time.");
                return 2;
            }
            var (oldStart, oldEnd) = InviteTriage.Times(e);
            var start = PyTime.FromIso(args.Positional[1]);
            var end = args.Positional.Count > 2 ? start.Date + PyTime.TimeFromIso(args.Positional[2]) : start + (oldEnd - oldStart);
            calendar.Move(eventId, start, end);
            log($"moved  {Py.Str(e.Get("subject"))}: {InviteTriage.Day(oldStart)} -> {InviteTriage.Day(start)}–{InviteTriage.Hm(end)}"
                + (InviteTriage.People(e).Count <= 1 ? "" : " (attendees get an update)"));
            return 0;
        }
        var days = long.Parse(args.Value("--days") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
        var found = InviteTriage.LeaveSweep(settings, calendar, DateOnly.FromDateTime(Clock.Now), days);
        if (found.Count == 0)
        {
            Print(host.Out, $"Nothing in your leave over the next {days} days that you haven't declined.");
            return 0;
        }
        var decline = args.Flag("--decline");
        foreach (var e in found)
        {
            var (start, end) = InviteTriage.Times(e);
            var send = !e.Has("responseRequested") || Py.Truthy(e.Get("responseRequested"));
            var note = args.Value("--note") is { Length: > 0 } given ? given : e.Req("note");
            Print(host.Out, $"  {InviteTriage.Day(start)}–{InviteTriage.Hm(end)}  {Py.Str(e.Get("subject"))}  ({InviteTriage.Organiser(e).Name}; "
                            + $"{Py.Str(e.Get("responseStatus").Get("response"))})" + (send ? $"  note: \"{note}\"" : "  (no response requested)"));
            if (decline)
                calendar.Respond(e.Req("id"), "decline", note, send);   // the occurrence: the rest of the series is untouched
        }
        Print(host.Out, $"{found.Count} meeting(s) in your leave" + (decline ? ": declined, each occurrence on its own."
            : ". `--decline` declines them (each occurrence, not the series)."));
        if (decline)
            log($"leave sweep: declined {found.Count} meeting(s)");
        return 0;
    }

    /// <summary>A preview: it only reads (your calendar, and your reports' free/busy). Writes are refused outright.</summary>
    private static int OneToOnesCommand(Args args, Settings settings, IGraph graph, CliHost host)
    {
        if (settings.OneToOnes is null)
        {
            Print(host.Err, $"{settings.Path} has no [one_to_ones] section.");
            return 2;
        }
        long? daysArg = args.Value("--days") is { } d ? long.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : null;
        var found = OneToOnePlanner.Plan(settings, new Calendar(graph, WriteDoor.Refusing().Calendar), daysAhead: daysArg);
        var days = daysArg is { } x and not 0 ? x : settings.OneToOnes.DaysAhead is { } y and not 0 ? y : settings.Invites!.DaysAhead;
        if (found.Count == 0)
        {
            Print(host.Out, $"Nobody's off for any of your 1:1s in the next {days} days.");
            return 0;
        }
        foreach (var decision in found)
            Print(host.Out, "  " + OneToOnePlanner.Describe(decision));
        var mode = settings.OneToOnes.Act ? "acts on these" : "only records these (act = false)";
        Print(host.Out, $"{found.Count} 1:1(s) in the next {days} days; a run {mode}"
                        + (settings.OneToOnes.Act ? $", up to {settings.OneToOnes.MaxActions} changes at a time." : "."));
        return 0;
    }

    private static int AutostartCommand(string action, Settings settings, CliHost host)
    {
        var task = host.Tasks(Autostart.TaskName);
        if (action == "on")
        {
            host.Tasks(Autostart.OldTaskName).Disable();
            var (exe, args) = Autostart.Command(host.ProgramDir, settings.Path);
            var command = task.Enable(exe, args, Path.GetDirectoryName(settings.Path));
            Print(host.Out, $"Autostart is on: '{Autostart.TaskName}' runs at every logon, and has started now. It triages at once, then "
                            + $"every {settings.EveryMinutes} minutes, {settings.Hours.Start}-{settings.Hours.End}{(settings.Weekdays ? " Mon-Fri" : "")}.\n  {command}");
        }
        else if (action == "off")
        {
            var was = task.Disable() | host.Tasks(Autostart.OldTaskName).Disable();
            Print(host.Out, was ? "Autostart is off." : "Autostart was already off.");
        }
        else
        {
            var info = task.Status();
            if (info is null)
                Print(host.Out, "Autostart is off. `mail-triage autostart on` turns it on.");
            else
            {
                Print(host.Out, $"Autostart is on ('{Autostart.TaskName}').");
                foreach (var name in new[] { "state", "last_run", "last_result", "next_run", "command" })
                    Print(host.Out, $"  {name.Replace('_', ' '),-12} {info.GetValueOrDefault(name, "")}");
            }
        }
        return 0;
    }
}
