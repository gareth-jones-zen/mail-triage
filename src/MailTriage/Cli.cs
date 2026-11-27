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
    /// <summary>Waits between read-backs (a hold's room answering, 1.23.0); tests pass a no-op.</summary>
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;
}

/// <summary>A usage mistake on the command line (argparse's exit 2).</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>mail-triage: standing rules for your mail (cli.py). The same commands and exit codes as the Python
/// version; argparse's wording is approximated. Two .NET-only options choose how a run may write:
/// --writes live (the default) | journal | refuse, and --journal FILE (where journal mode records each write).</summary>
public static class Cli
{
    /// <summary>From the build (Directory.Build.props, or the release's -p:Version from its tag).</summary>
    public static readonly string Version = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Cli).Assembly)?.InformationalVersion ?? "unknown";

    public const string Usage = """
        mail-triage: standing rules for your mail. Delete the routine; queue the rest for you, each email once.

            mail-triage config              create the settings file (if missing) and say where it is
            mail-triage sign-in             sign in to Microsoft (once; opens a browser)
            mail-triage run                 apply the rules: delete what they allow, queue the rest
            mail-triage run --dry-run       say what it would do, change nothing (needs only Mail.Read)
            mail-triage list                the queue, numbered (--json for the file as it is)
            mail-triage delete 2 5          delete queued emails, by number (or by Graph id)
            mail-triage keep 1 3            take them off the queue and leave them in the folder
            mail-triage kept                what keep took off the queue (the last 60 days), numbered (--json for the UI)
            mail-triage requeue 4           put a kept item back on the queue as it was (by its number in `kept`, or its id),
                                            at the end; local only, nothing in the mailbox changes
            mail-triage clear               empty the queue (each email stays in its folder, never raised again)
            mail-triage deleted             what the rules deleted since the last clear, by rule (--all: the last 60 days),
                                            with the note in each meeting email answered without asking, numbered
                                            (--json: each record, with its id and whether it can be restored)
            mail-triage restore 7 [--yes]   an email mail-triage deleted (by its number or id in `deleted`), from Deleted
                                            Items back to the Inbox, read state as it is; without --yes, a preview;
                                            no rule deletes it again, and it isn't queued as new
            mail-triage summary           one line for a session briefing: what's queued and what the rules deleted
                                            since the last clear, by rule (--json for the counts)
            mail-triage which <sender> [subject]   which rule an email would meet
            mail-triage handle 3 [--handler NAME]  give a queued email (or any, by Graph id) to its rule's handler, then
                                            do what the handler's `then` says (rules with action = "handle" do this in a run)
            mail-triage forward 2 3 [--rule NAME] [--yes]  apply the forward rule they match (or the one named) to queued
                                            emails: forward, mark read, file, and off the queue (never forwards one twice);
                                            without --yes, shows each email, its rule and who it would go to
                                            (recipients outside your domain flagged)

            With [invites] in the settings, unanswered meeting invites are queued too, each with a recommendation:
            mail-triage file 4 --to Inbox/Projects   move queued emails to a folder, left unread
            mail-triage invite accept 3 [--yes]   answer a queued invite: accept, tentative or decline; without --yes,
                                            shows what it would send, to whom, for which occurrences
                --note "..."                    a note to the organiser; --silent answers without telling them
                --move                          with accept: move each clash you can move to its suggested slot
                --series                        with decline: the whole series (otherwise only the instances)
            mail-triage move <event id> 2026-11-11T16:00 [16:50] [--yes]
                                            move one of your own meetings or blocks; without --yes, shows the move,
                                            who gets an update and anything it would clash with
            mail-triage hold 2026-11-25T16:15 17:00 "Hold: ITS in FibreHub" [--body TEXT | --body-file FILE]
                [--location TEXT | --room ROOM]
                [--online] [--yes]           a hold in your own calendar, never inviting people (you add them and send);
                                            --room books a room from [hold] rooms (its address or name);
                                            without --yes, shows what it would create; --online makes it a Teams
                                            meeting, whose join details can't be removed afterwards
            mail-triage send-invite 2026-12-01T10:00 10:30 "Subject" --to ADDR[,ADDR...] [--optional ADDR,...]
                [--body TEXT | --body-file FILE] [--room ROOM | --location TEXT] [--no-online]
                [--replace-hold EVENT_ID] [--yes]
                                            send a meeting invitation from your calendar, once you've approved the
                                            wording: without --yes, shows exactly what would go; a Teams meeting
                                            unless --no-online; --room from [hold] rooms; --replace-hold deletes that
                                            hold once the invite is created; never sent twice
            mail-triage respond <event id> accept|tentative|decline [--note TEXT] [--series] [--yes]
                                            answer a meeting you're invited to, whatever you said before (accepted,
                                            tentative or not yet); one occurrence unless --series; without --yes, shows
                                            what it would send
            mail-triage cancel <event id> [--note TEXT] [--series] [--yes]
                                            one of your own events: a block with no attendees is deleted (no one is
                                            told); a meeting with attendees is cancelled, and each gets a cancellation;
                                            one occurrence unless --series; without --yes, shows what it would do
            mail-triage leave [--decline [--yes]] [--note TEXT] [--days N]
                                            meetings in your leave you haven't declined (the next 60 days); --decline
                                            shows each decline it would send (each occurrence, never the series: the
                                            note, and whether the organiser is told); --decline --yes declines them
            mail-triage one-to-ones         with [one_to_ones] and/or [catch_ups]: what each run would do with your 1:1s and
                                            catch-ups when someone's on leave (changes nothing); [--days N]
            mail-triage sent [--yes]        with [sent]: what it would delete or file in Sent Items (meeting messages you
                                            sent, rule forwards' copies), and with --yes, do it; the first --yes reads all of
                                            Sent Items, then each run reads only what's new
            mail-triage days-off [--days N]   each weekday ahead: leave, a half day (which half), a bank holiday or
                                            working, from [leave]
            mail-triage double-bookings     each upcoming double booking, and what the [[double_booking_rule]]s would do
                                            with it (changes nothing)

            mail-triage drafts [--all]      your Drafts folder, newest change first, numbered (--json for the UI)
            mail-triage send-draft 1 [--full]   exactly what that draft would send, and its fingerprint; sends nothing
            mail-triage send-draft 1 --yes --seen FINGERPRINT
                                            send it, once, only if it's still what that preview showed; never sent
                                            twice, and never resent after a lost answer (check Sent Items); no undo

            A write by hand whose answer was lost (move, leave --decline, invite, respond, cancel, hold, restore) is
            recorded: the next preview or --yes for it reads the event again, says if it went, and otherwise skips it
            (exit 1); --again sends it, once you've checked Outlook.

            mail-triage watch              run now, then every every_minutes in working hours; what autostart runs
            mail-triage autostart on        the watcher at every logon, and now (off, restart, status)

        Options before the command: --config FILE, --version, and (this version only) --writes live|journal|refuse,
        --journal FILE.
        """;

    private static readonly string[] Commands =
        ["config", "sign-in", "run", "list", "delete", "resolve", "keep", "clear", "watch", "deleted", "summary", "raised", "which", "invite", "file",
         "move", "hold", "send-invite", "respond", "cancel", "sent", "leave", "one-to-ones", "double-bookings", "days-off", "autostart", "handle", "forward", "restore",
         "kept", "requeue", "drafts", "send-draft"];

    /// <summary>The parsed command line.</summary>
    internal sealed class Args
    {
        public string? Config;
        public WriteMode Writes = WriteMode.Live;
        public string? Journal;
        public string Command = "";
        /// <summary>--help or -h after the command (1.35.0): that command's help.</summary>
        public bool Help;
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
        ["kept"] = (["--json"], []),
        ["deleted"] = (["--all", "--json"], []),
        ["restore"] = (["--yes", "--again"], []),
        ["summary"] = (["--json"], []),
        ["invite"] = (["--silent", "--series", "--move", "--yes", "--again"], ["--note"]),
        ["move"] = (["--yes", "--again"], []),
        ["file"] = ([], ["--to"]),
        ["handle"] = ([], ["--handler"]),
        ["forward"] = (["--yes"], ["--rule"]),
        ["leave"] = (["--decline", "--yes", "--again"], ["--days", "--note"]),
        ["one-to-ones"] = (["--dry-run"], ["--days"]),
        ["days-off"] = ([], ["--days"]),
        ["hold"] = (["--yes", "--online", "--again"], ["--body", "--body-file", "--location", "--room"]),
        ["send-invite"] = (["--yes", "--no-online"], ["--to", "--optional", "--body", "--body-file", "--location", "--room", "--replace-hold"]),
        ["respond"] = (["--yes", "--series", "--again"], ["--note"]),
        ["cancel"] = (["--yes", "--series", "--again"], ["--note"]),
        ["sent"] = (["--yes"], []),
        ["drafts"] = (["--all", "--json"], []),
        ["send-draft"] = (["--yes", "--full"], ["--seen"]),
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
            if (arg is "-h" or "--help")
            {
                a.Help = true;
                return a;
            }
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
            case "delete" or "resolve" or "keep" or "requeue":
                Count(1, int.MaxValue, "refs");
                break;
            case "which":
                Count(1, 2, "sender");
                break;
            case "handle" or "restore" or "send-draft":
                Count(1, 1, "ref");
                break;
            case "invite":
                Count(2, int.MaxValue, "action, refs");
                if (a.Positional[0] is not ("accept" or "tentative" or "decline"))
                    throw new UsageException($"argument action: invalid choice: {Py.ReprString(a.Positional[0])} (choose from 'accept', 'tentative', 'decline')");
                break;
            case "forward":
                Count(1, int.MaxValue, "refs");
                break;
            case "file":
                Count(1, int.MaxValue, "refs");
                if (a.Value("--to") is null)
                    throw new UsageException("the following arguments are required: --to");
                break;
            case "move":
                Count(2, 3, "event, start");
                break;
            case "respond":
                Count(2, 2, "event, action");
                if (a.Positional[1] is not ("accept" or "tentative" or "decline"))
                    throw new UsageException($"argument action: invalid choice: {Py.ReprString(a.Positional[1])} (choose from 'accept', 'tentative', 'decline')");
                break;
            case "cancel":
                Count(1, 1, "event");
                break;
            case "hold":
                Count(3, 3, "start, end, subject");
                if (a.Value("--body") is not null && a.Value("--body-file") is not null)
                    throw new UsageException("argument --body-file: not allowed with argument --body");
                if (a.Value("--location") is not null && a.Value("--room") is not null)
                    throw new UsageException("argument --room: not allowed with argument --location (the room is the location)");
                break;
            case "send-invite":
                Count(3, 3, "start, end, subject");
                if (a.Value("--to") is not { } to || to.Trim().Length == 0)
                    throw new UsageException("the following arguments are required: --to");
                if (a.Value("--body") is not null && a.Value("--body-file") is not null)
                    throw new UsageException("argument --body-file: not allowed with argument --body");
                if (a.Value("--location") is not null && a.Value("--room") is not null)
                    throw new UsageException("argument --room: not allowed with argument --location (the room is the location)");
                break;
            case "autostart":
                Count(1, 1, "action");
                if (a.Positional[0] is not ("on" or "off" or "restart" or "status"))
                    throw new UsageException($"argument action: invalid choice: {Py.ReprString(a.Positional[0])} (choose from 'on', 'off', 'restart', 'status')");
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
        if (args.Help)
        {
            Print(host.Out, args.Command switch
            {
                "drafts" => SendDrafts.ListHelp,
                "send-draft" => SendDrafts.SendHelp,
                _ => Usage,
            });
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
                ? $"{rule.Name}: {rule.Action}" + (rule.Check is not null ? $" ({rule.Check})" : "")
                  + (rule.Handler is not null ? $" ({rule.Handler}, then {settings.Handlers[rule.Handler].Then})" : "") + (rule.Why.Length > 0 ? $"\n  {rule.Why}" : "")
                : "No rule: it would be queued for review.");
            return 0;
        }
        if (args.Command == "list")
            return List(settings, args.Flag("--json"), host);
        if (args.Command == "deleted")
        {
            var listed = Restores.Listed(settings, args.Flag("--all"));
            if (!args.Flag("--json"))
                return DeletedCommand(listed, host);
            Print(host.Out, PyJson.Dump(new JsonArray(listed.Select(x => (JsonNode)Restores.Json(x)).ToArray()), 2, false));   // 1.32.0
            return 0;
        }
        if (args.Command == "summary")
            return Summary(settings, args.Flag("--json"), host);
        if (args.Command == "kept")
            return KeptCommand(settings, args.Flag("--json"), host);
        if (args.Command is "clear" or "raised" or "keep" or "requeue")
        {
            // Locked like a run, so a run in progress can't write back a queue that's just been cleared.
            try
            {
                using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))
                {
                    if (args.Command == "keep")
                        Triage.Keep(settings, args.Positional, log);
                    else if (args.Command == "requeue")
                        Kept.Requeue(settings, args.Positional, log);   // 1.33.0
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
            if (args.Command == "double-bookings")
                return DoubleBookingsCommand(settings, graph, host);
            if (args.Command == "days-off")
                return DaysOffCommand(args, settings, graph, host);
            if (args.Command == "restore")
                return Restores.Run(settings, graph, args.Flag("--yes") ? Door(args, graph, args.Writes, settings) : WriteDoor.Refusing(), args.Positional[0],
                    args.Flag("--yes"), args.Flag("--again"), log, line => Print(host.Out, line), host.Sleep);
            if (args.Command == "drafts")   // 1.35.0: reads only, without the run lock
                return SendDrafts.List(settings, graph, args.Flag("--all"), args.Flag("--json"), host);
            if (args.Command == "send-draft")   // 1.35.0: --yes gets the live door; a preview, one that refuses every write
                return SendDrafts.Run(args, settings, graph, args.Flag("--yes") ? Door(args, graph, args.Writes, settings) : WriteDoor.Refusing(), log, host);
            if (args.Command == "sent")
                return SentCommand(args, settings, graph, args.Flag("--yes") ? Door(args, graph, args.Writes, settings) : WriteDoor.Refusing(), log, host);
            if (args.Command == "hold")
                return HoldCommand(args, settings, graph, args.Flag("--yes") ? Door(args, graph, args.Writes, settings) : WriteDoor.Refusing(), log, host);
            if (args.Command == "send-invite")
                return SendInvites.Run(args, settings, graph, args.Flag("--yes") ? Door(args, graph, args.Writes, settings) : WriteDoor.Refusing(), log, host);
            if (args.Command is "respond" or "cancel")
                return CalendarWrites.Run(args.Command, args.Positional, args.Value("--note") ?? "", args.Flag("--series"), args.Flag("--yes"),
                    args.Flag("--again"), settings, graph, args.Flag("--yes") ? Door(args, graph, args.Writes, settings) : WriteDoor.Refusing(), log, host.Out, host.Err);
            if (args.Command is "invite" or "move" or "leave")
            {
                if (settings.Invites is null)
                {
                    Print(host.Err, $"{settings.Path} has no [invites] section.");
                    return 2;
                }
                if (args.Command == "leave" && args.Flag("--yes") && !args.Flag("--decline"))
                {
                    Print(host.Err, "--yes goes with --decline: `mail-triage leave --decline` shows what it would send, "
                                    + "`mail-triage leave --decline --yes` declines them. Nothing sent.");
                    return 2;
                }
                // 1.28.0: invite and move write only with --yes, whatever --writes says; without it they preview through a
                // door that refuses every write. 1.29.0: so does leave, which writes only with --decline --yes.
                var preview = args.Command is "invite" or "move" ? !args.Flag("--yes") : args.Command == "leave" && !(args.Flag("--decline") && args.Flag("--yes"));
                return CalendarCommand(args, settings, graph, preview ? WriteDoor.Refusing() : Door(args, graph, args.Writes, settings), log, host);
            }
            if (args.Command == "forward" && !args.Flag("--yes"))
                return Triage.PreviewForward(settings, new Mailbox(graph, WriteDoor.Refusing().Mail), args.Positional, args.Value("--rule"),
                    line => Print(host.Out, line));   // 1.28.0: reads only, without the run lock
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
                if (args.Command == "forward")
                    return Triage.Forward(settings, new Mailbox(graph, door.Mail), args.Positional, args.Value("--rule"), log);
                if (args.Command == "handle")
                {
                    if (door.Mode != WriteMode.Live)
                    {
                        Print(host.Err, "A handler writes outside mail-triage, so `handle` runs only with --writes live.");
                        return 2;
                    }
                    return Triage.HandleByHand(settings, new Mailbox(graph, door.Mail), new Handlers(settings), args.Positional[0], args.Value("--handler"), log);
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

    /// <summary>Handlers run only when the run may write: a handler writes outside the write door.</summary>
    private static RunReport RunWith(Settings settings, IGraph graph, WriteDoor door, bool dryRun, Action<string> log)
    {
        var mailbox = new Mailbox(graph, door.Mail, dryRun);
        var report = Triage.Run(settings, mailbox, settings.Invites is null ? null : new Calendar(graph, door.Calendar), dryRun, log,
            door.Mode == WriteMode.Live && !dryRun ? new Handlers(settings) : null);
        if (settings.Sent is not null)
        {
            try
            {
                SentItems.RunScheduled(settings, mailbox, dryRun, log);   // 1.19.0: nothing until the first `sent --yes`
            }
            catch (Exception error) when (error is not SignInNeededException)
            {
                log($"sent: failed: {error.GetType().Name}: {error.Message}");   // Sent Items never stops the triage run
            }
        }
        return report;
    }

    private static RunReport RunOnce(Settings settings, IGraph graph, Args args, Action<string> log, bool dryRun) =>
        RunWith(settings, graph, Door(args, graph, args.Writes, settings), dryRun, log);

    /// <summary>`kept` (1.33.0): what `keep` took off the queue, newest last, each with the number `requeue` takes.</summary>
    private static int KeptCommand(Settings settings, bool json, CliHost host)
    {
        var all = Kept.All(settings);
        if (json)
        {
            Print(host.Out, PyJson.Dump(new JsonArray(all.Select(r => (JsonNode)Kept.Json(r)).ToArray()), 2, false));
            return 0;
        }
        foreach (var r in all)
            Print(host.Out, $"{r.N,3}. {PyStr.Head(r.Item.Fmt("received", "None"), 10)}  {r.Item.Fmt("from", "None")}\n"
                            + $"     {r.Item.Fmt("subject", "None")}\n     [{r.Item.Fmt("rule", "None")}] kept {r.Kept.Replace('T', ' ')}");
        Print(host.Out, all.Count == 0 ? "Nothing kept in the last 60 days." : "`mail-triage requeue <n>` puts one back on the queue.");
        return 0;
    }

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

    /// <summary>For a session briefing (1.7.0, 18 Nov 2026): "28 emails and 1 invite to review; 24 deleted by the rules
    /// since the last clear (15 Cancelled meetings, ...)", then where to look. --json: the counts. A briefing used to
    /// read the queue and deletion files itself, and lost them when their paths moved to a base file.</summary>
    private static int Summary(Settings settings, bool json, CliHost host)
    {
        var queue = Triage.Queue(settings);
        var invites = queue.Count(i => i.Str("type") == "invite");
        var emails = queue.Count - invites;
        var byRule = new List<(string Rule, int Count)>();
        foreach (var item in Triage.Deleted(settings))
        {
            var rule = item.Str("rule") is { Length: > 0 } r ? r : "(no rule)";
            var at = byRule.FindIndex(g => g.Rule == rule);
            if (at < 0)
                byRule.Add((rule, 1));
            else
                byRule[at] = (rule, byRule[at].Count + 1);
        }
        byRule = byRule.OrderBy(g => -g.Count).ToList();
        var deleted = byRule.Sum(g => g.Count);
        if (json)
        {
            var rules = new JsonObject();
            foreach (var (rule, count) in byRule)
                rules[rule] = count;
            Print(host.Out, PyJson.Dump(new JsonObject { ["emails"] = emails, ["invites"] = invites, ["deleted"] = deleted, ["deleted_by_rule"] = rules }, 2, false));
            return 0;
        }
        static string Plural(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
        Print(host.Out, $"{Plural(emails, "email")} and {Plural(invites, "invite")} to review; {deleted} deleted by the rules since the last clear"
                        + (byRule.Count > 0 ? $" ({string.Join(", ", byRule.Select(g => $"{g.Count} {g.Rule}"))})" : ""));
        Print(host.Out, "   `mail-triage list` / `deleted`; invites: `invite accept|decline <n>`");
        return 0;
    }

    private static int DeletedCommand(List<Restores.Numbered> listed, CliHost host)
    {
        if (listed.Count == 0)
        {
            Print(host.Out, "Nothing deleted by the rules since the queue was last cleared.");
            return 0;
        }
        static string When(JsonObject i) => PyStr.Head(i.Req("when"), 16).Replace("T", " ");
        Print(host.Out, $"{listed.Count} deleted, {When(listed[0].Entry)} to {When(listed[^1].Entry)}:");
        var byRule = new List<(string Rule, List<Restores.Numbered> Group)>();
        foreach (var x in listed)
        {
            var rule = x.Entry.Str("rule") is { Length: > 0 } r ? r : "(no rule)";
            var index = byRule.FindIndex(g => g.Rule == rule);
            if (index < 0)
                byRule.Add((rule, [x]));
            else
                byRule[index].Group.Add(x);
        }
        foreach (var (rule, group) in byRule.OrderBy(g => -g.Group.Count))
        {
            Print(host.Out, $"\n  {rule} ({group.Count})");
            foreach (var (n, _, item) in group)
                // 1.32.0: each with its number, for `restore`, and whether it's been restored
                Print(host.Out, $"    {n}. {PyStr.Head(item.Fmt("received", "None"), 10)}  {item.Fmt("from", "None")}: {PyStr.Head(item.Fmt("subject", "None"), 90)}"
                                + (item.Truthy("to") ? $"  -> filed to {Py.Str(item.Get("to"))}" : "")
                                + (item.Truthy("accepted") ? $"  -> accepted ({Py.Str(item.Get("accepted"))})" : "")
                                + (item.Truthy("did") ? $"  -> {Py.Str(item.Get("did"))}" : "")
                                + Restores.State(item) switch
                                {
                                    "restored" => $"  [restored to the Inbox {PyStr.Head(item.Get("restored").StrOr("at"), 16).Replace("T", " ")}]",
                                    "unconfirmed" => "  [restore may have gone: check the Inbox]",
                                    _ => "",
                                }
                                // 1.31.0: what its sender wrote, or what changed in the meeting's description
                                + (item.Str("note") is { Length: > 0 } note ? $"\n      {item.Str("note_by") ?? "note"}: \"{note}\"" : ""));
        }
        if (listed.Any(x => Restores.WhyNot(x.Entry) is null && Restores.State(x.Entry) is null))
            Print(host.Out, "\nBack to the Inbox: `mail-triage restore <n>` (a preview; --yes to move it).");
        return 0;
    }

    private static int CalendarCommand(Args args, Settings settings, IGraph graph, WriteDoor door, Action<string> log, CliHost host)
    {
        var calendar = new Calendar(graph, door.Calendar);
        var again = args.Flag("--again");
        if (args.Command == "invite")
        {
            // 1.28.0: without --yes, a preview: it reads the queue (and the events it names) and takes no lock, so it
            // neither waits for a run nor holds one up.
            if (!args.Flag("--yes"))
                return Triage.PreviewAnswer(settings, calendar, args.Positional[1..], args.Positional[0], args.Value("--note") ?? "", !args.Flag("--silent"),
                    args.Flag("--series"), args.Flag("--move"), line => Print(host.Out, line), args.Flag("--again"), log);
            (int Answered, int NotDone) answered;
            using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))
                answered = Triage.Answer(settings, calendar, args.Positional[1..], args.Positional[0], args.Value("--note") ?? "", !args.Flag("--silent"), log,
                    series: args.Flag("--series"), move: args.Flag("--move"), again: args.Flag("--again"), say: line => Print(host.Err, line),
                    journal: door.Mode == WriteMode.Live);
            return answered.NotDone > 0 ? 1 : 0;   // 1.30.0: a lost answer, or one skipped for an earlier one, exits 1
        }
        if (args.Command == "move")
        {
            var eventId = args.Positional[0];
            JsonObject e;
            try
            {
                e = calendar.Event(eventId);
            }
            catch (M365Graph.GraphRequestException error) when ((int)error.Status is 404 or 400)
            {
                Print(host.Err, $"No event with that id in your calendar ({(int)error.Status}).");
                return 2;
            }
            if (!e.Truthy("isOrganizer"))
            {
                Print(host.Err, "That's someone else's meeting: it can't be moved, only declined with a proposed time.");
                return 2;
            }
            var (oldStart, oldEnd) = InviteTriage.Times(e);
            DateTime start, end;
            try
            {
                start = PyTime.FromIso(args.Positional[1]);
                end = args.Positional.Count > 2 ? start.Date + PyTime.TimeFromIso(args.Positional[2]) : start + (oldEnd - oldStart);
            }
            catch (FormatException)
            {
                Print(host.Err, "Start is YYYY-MM-DDTHH:MM and end is HH:MM, in local time.");
                return 2;
            }
            var subject = e.StrOr("subject");
            var owner = (settings.Email ?? "").ToLowerInvariant();
            var others = InviteTriage.People(e).Where(a => InviteTriage.Address(a) != owner).ToList();
            var moving = $"\"{subject}\" ({CalendarWrites.Scope(e)}): {InviteTriage.Day(oldStart)}–{InviteTriage.Hm(oldEnd)} -> "
                         + $"{InviteTriage.Day(start)}–{InviteTriage.Hm(end)}";
            // 1.30.0: an earlier move of this event whose answer was lost is checked against the event as it is now (just read).
            var moveKey = UnconfirmedWrites.Key("move", eventId);
            var earlierMove = UnconfirmedWrites.Check(settings, moveKey, () => e, UnconfirmedWrites.Moved, log);
            if (earlierMove.Seen == UnconfirmedWrites.Seen.Went)
            {
                Print(host.Out, earlierMove.Text);
                if (UnconfirmedWrites.Moved(e, UnconfirmedWrites.MoveEntry("move", eventId, subject, oldEnd, start, end)))
                {
                    Print(host.Out, "Nothing to do: it's already at that time.");
                    return 0;
                }
            }
            var pendingMove = earlierMove.Seen == UnconfirmedWrites.Seen.Pending;
            if (pendingMove && args.Flag("--yes") && !again)
            {
                log($"unconfirmed: skipped {UnconfirmedWrites.Describe(earlierMove.Entry!)}: an earlier one may not have landed");
                Print(host.Err, earlierMove.Text + " Not sent again: once you've checked, `--again --yes` moves it.");
                return 1;
            }
            if (!args.Flag("--yes"))
            {
                if (pendingMove)
                    Print(host.Out, earlierMove.Text);
                Print(host.Out, $"Would move your {(others.Count > 0 ? "meeting" : "block")} {moving}.");
                if (others.Count > 0)
                {
                    Print(host.Out, others.Count == 1 ? "  This person gets an update:" : $"  Each of these {others.Count} people gets an update:");
                    foreach (var a in others)
                        Print(host.Out, $"    {CalendarWrites.Who(a)}");
                }
                else
                    Print(host.Out, "  No one else is invited, so no one is told.");
                try
                {
                    var zone = calendar.Zone();
                    var clashes = Proposals.Clashes(calendar.Between(PyTime.At(zone, start), PyTime.At(zone, end)), eventId, start, end, allDay: true);
                    if (clashes.Count == 0)
                        Print(host.Out, "  Nothing else in your calendar then.");
                    else
                    {
                        Print(host.Out, $"  Warning: it would clash with {clashes.Count} thing{(clashes.Count == 1 ? "" : "s")} in your calendar:");
                        foreach (var c in clashes)
                        {
                            var (cs, ce) = InviteTriage.Times(c);
                            var at = c.Truthy("isAllDay") ? PyTime.Strftime(cs, "%a %d %b") + ", all day" : $"{InviteTriage.Day(cs)}–{InviteTriage.Hm(ce)}";
                            Print(host.Out, $"    \"{c.StrOr("subject")}\", {at} ({c.Str("showAs")}, {(c.Truthy("isOrganizer") ? "yours" : CalendarWrites.Current(c))})");
                        }
                    }
                }
                catch (Exception error) when (error is not SignInNeededException)
                {
                    Print(host.Out, $"  (Couldn't read your calendar at the new time, so clashes aren't checked: {PyStr.Head(error.Message, 120)})");
                }
                Print(host.Out, (others.Count > 0 ? "Nothing sent." : "Nothing changed.")
                                + (!pendingMove ? " Run again with --yes to move it."
                                    : again ? " Run again with --again --yes to move it."
                                    : " --yes would skip it, as the earlier move may have landed; once you've checked Outlook, --again --yes moves it."));
                return 0;
            }
            if (pendingMove)
                log($"unconfirmed: sending {UnconfirmedWrites.Describe(earlierMove.Entry!)} again (--again)");
            if (Unconfirmed.Try(() => calendar.Move(eventId, start, end)) is { } lost)
            {
                UnconfirmedWrites.Record(settings, moveKey, UnconfirmedWrites.MoveEntry("move", eventId, subject, oldEnd, start, end), lost, log);
                Print(host.Err, $"Microsoft didn't confirm the move of \"{Py.Str(e.Get("subject"))}\", so it may have gone: check Outlook "
                                + "before moving it again. It wasn't sent twice. It's recorded: the next `move` of it checks whether it landed.");
                return 1;
            }
            if (door.Mode != WriteMode.Live)
            {
                Print(host.Out, $"Journalled, not sent: move {moving}.");
                return 0;
            }
            if (pendingMove)
                UnconfirmedWrites.Clear(settings, moveKey, "sent again with --again, and confirmed", log);
            log($"moved  {Py.Str(e.Get("subject"))}: {InviteTriage.Day(oldStart)} -> {InviteTriage.Day(start)}–{InviteTriage.Hm(end)}"
                + (InviteTriage.People(e).Count <= 1 ? "" : " (attendees get an update)"));
            Activity.Write(settings, log, "move", ActivityJournal.By.Hand, e.Str("subject"), "by hand",
                $"{InviteTriage.Day(oldStart)}–{InviteTriage.Hm(oldEnd)} -> {InviteTriage.Day(start)}–{InviteTriage.Hm(end)}"
                + (others.Count > 0 ? $"; {others.Count} told" : "; no one else invited"), eventId, Activity.MoveBack(eventId, oldStart, oldEnd));   // 1.33.0
            return 0;
        }
        var days = long.Parse(args.Value("--days") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
        var found = InviteTriage.PlanLeaveDeclines(settings, calendar, DateOnly.FromDateTime(Clock.Now), days, args.Value("--note"));
        var decline = args.Flag("--decline");
        // 1.30.0: the declines an earlier `leave --decline --yes` sent whose answer was lost. One that has dropped out of the
        // plan (declined, or gone from the calendar) is read again and cleared if it went; one still in it isn't declined,
        // so it may not have landed: skipped unless --again.
        var inPlan = found.Select(d => d.Event.Req("id")).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, entry) in UnconfirmedWrites.Matching(settings, o => o.Str("command") == "leave" && !inPlan.Contains(o.StrOr("event"))))
        {
            var look = UnconfirmedWrites.Check(settings, key, () => UnconfirmedWrites.ReadOrGone(calendar, entry.Req("event")), UnconfirmedWrites.Answered, log);
            if (look.Seen == UnconfirmedWrites.Seen.Went)
                Print(host.Out, look.Text);
        }
        var earlier = new Dictionary<string, UnconfirmedWrites.Look>(StringComparer.Ordinal);
        foreach (var d in found)
        {
            var look = UnconfirmedWrites.Check(settings, UnconfirmedWrites.Key("respond", d.Event.Req("id")), () => d.Event, UnconfirmedWrites.Answered, log);
            if (look.Seen == UnconfirmedWrites.Seen.Went)
                Print(host.Out, look.Text);   // an earlier answer of another kind (a `respond`) that went: this decline is new
            else if (look.Seen == UnconfirmedWrites.Seen.Pending)
                earlier[d.Event.Req("id")] = look;
        }
        if (found.Count == 0)
        {
            Print(host.Out, $"Nothing in your leave over the next {days} days that you haven't declined.");
            return 0;
        }
        if (decline && !args.Flag("--yes"))
            return PreviewLeaveDeclines(found, days, earlier, again, host);   // 1.29.0: what --decline --yes would send, and nothing sent
        var unconfirmed = 0;
        var skipped = 0;
        foreach (var d in found)
        {
            var e = d.Event;
            var id = e.Req("id");
            Print(host.Out, $"  {InviteTriage.Day(d.Start)}–{InviteTriage.Hm(d.End)}  {Py.Str(e.Get("subject"))}  ({InviteTriage.Organiser(e).Name}; "
                            + $"{Py.Str(e.Get("responseStatus").Get("response"))})" + (d.Respond ? $"  note: \"{d.Note}\"" : "  (no response requested)"));
            var pending = earlier.GetValueOrDefault(id);
            if (!decline)
            {
                if (pending is not null)
                    Print(host.Out, $"    {pending.Text}");
                continue;
            }
            if (pending is not null && !again)
            {
                skipped++;
                log($"unconfirmed: skipped {UnconfirmedWrites.Describe(pending.Entry!)}: an earlier one may not have landed");
                Print(host.Err, $"    {pending.Text} Skipped, never resent: once you've checked, `leave --decline --again --yes` declines it.");
                continue;
            }
            if (pending is not null)
                log($"unconfirmed: sending {UnconfirmedWrites.Describe(pending.Entry!)} again (--again)");
            // The occurrence: the rest of the series is untouched.
            if (Unconfirmed.Try(() => calendar.Respond(id, "decline", d.Note, d.Respond)) is { } lost)
            {
                unconfirmed++;
                UnconfirmedWrites.Record(settings, UnconfirmedWrites.Key("respond", id), UnconfirmedWrites.AnswerEntry("leave", id, null, e.StrOr("subject"),
                    "decline", $"{InviteTriage.Day(d.Start)}, {CalendarWrites.Scope(e)}", d.End), lost, log);
                Print(host.Err, "    Microsoft didn't confirm this decline, so it may have gone: check Outlook before declining it again.");
            }
            else
            {
                if (pending is not null && door.Mode == WriteMode.Live)
                    UnconfirmedWrites.Clear(settings, UnconfirmedWrites.Key("respond", id), "sent again with --again, and confirmed", log);
                if (door.Mode == WriteMode.Live)   // 1.33.0
                    Activity.Write(settings, log, "decline", ActivityJournal.By.Hand, $"{e.StrOr("subject")}: {InviteTriage.Day(d.Start)}–{InviteTriage.Hm(d.End)}",
                        "leave --decline", d.Respond ? $"on leave; note: \"{d.Note}\"" : "on leave; no response requested", id, Activity.Answer(id, "decline"));
            }
        }
        if (unconfirmed > 0)
            Print(host.Err, $"{unconfirmed} decline(s) may or may not have gone (above): never sent twice. Each is recorded, so the next "
                            + "`leave --decline` checks whether each landed.");
        if (skipped > 0)
            Print(host.Err, $"{skipped} decline(s) skipped: an earlier one may not have landed (above).");
        var declined = found.Count - unconfirmed - skipped;
        Print(host.Out, $"{found.Count} meeting(s) in your leave" + (!decline
            ? ". `--decline` shows what declining them would send (each occurrence, not the series); `--decline --yes` declines them."
            : declined == found.Count ? ": declined, each occurrence on its own."
            : $": {declined} declined, each occurrence on its own."));
        if (decline)
            log($"leave sweep: declined {declined} meeting(s)");
        return decline && unconfirmed + skipped > 0 ? 1 : 0;   // 1.30.0: a lost answer exits 1, as `move` does
    }

    /// <summary>`leave --decline` without --yes (1.29.0): for each meeting, when it is, the organiser, that only this
    /// occurrence is declined (never the series), the note, and whether the organiser gets a response. It only reads (the
    /// calendar, already read for the plan); nothing is sent, whatever --writes says.</summary>
    private static int PreviewLeaveDeclines(List<InviteTriage.LeaveDecline> found, long days, Dictionary<string, UnconfirmedWrites.Look> earlier,
        bool again, CliHost host)
    {
        Print(host.Out, $"Would decline {found.Count} meeting{(found.Count == 1 ? "" : "s")} in your leave over the next {days} days, "
                        + "each occurrence on its own, never the series:");
        var n = 0;
        foreach (var d in found)
        {
            var e = d.Event;
            var (name, address) = InviteTriage.Organiser(e);
            var organiser = name.Length > 0 ? name : address;
            Print(host.Out, $"  {++n}. \"{e.StrOr("subject")}\", organised by {(name.Length > 0 && address.Length > 0 ? $"{name} <{address}>" : organiser)}");
            Print(host.Out, $"     {InviteTriage.Day(d.Start)}–{InviteTriage.Hm(d.End)}; your answer so far: {CalendarWrites.Current(e)}");
            if (earlier.GetValueOrDefault(e.Req("id")) is { } pending)
                Print(host.Out, $"     {pending.Text}" + (again ? " --again: it would be declined again."
                    : " --yes would skip it; once you've checked, --again --yes declines it."));
            Print(host.Out, CalendarWrites.Scope(e) == "this occurrence only"
                ? "     Would decline this occurrence only, not the series."
                : "     Would decline it.");
            Print(host.Out, d.Respond
                ? $"     {organiser} gets a response" + (d.Note.Length > 0 ? $", with the note \"{d.Note}\"." : ", with no note.")
                : $"     No response is sent (the organiser didn't ask for one): {organiser} isn't told" + (d.Note.Length > 0 ? ", and the note isn't sent." : "."));
        }
        Print(host.Out, $"Nothing sent. Run again with --decline {(again ? "--again " : "")}--yes to decline {(found.Count == 1 ? "it" : "them")}.");
        return 0;
    }

    /// <summary>A preview: it only reads (your calendar, and the other people's free/busy). Writes are refused outright.
    /// 1:1s ([one_to_ones]) first, then catch-ups ([catch_ups], 1.26.0), each labelled.</summary>
    private static int OneToOnesCommand(Args args, Settings settings, IGraph graph, CliHost host)
    {
        if (settings.OneToOnes is null && settings.CatchUps is null)
        {
            Print(host.Err, $"{settings.Path} has no [one_to_ones] or [catch_ups] section.");
            return 2;
        }
        long? daysArg = args.Value("--days") is { } d ? long.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : null;
        var calendar = new Calendar(graph, WriteDoor.Refusing().Calendar);
        string Mode(bool act, long max) => act ? $"acts on these, up to {max} changes at a time." : "only records these (act = false).";
        if (settings.OneToOnes is { } one)
        {
            var found = OneToOnePlanner.Plan(settings, calendar, daysAhead: daysArg);
            var days = daysArg is { } x and not 0 ? x : one.DaysAhead is { } y and not 0 ? y : settings.Invites!.Days(Clock.Now);
            if (found.Count == 0)
                Print(host.Out, $"Nobody's off for any of your 1:1s in the next {days} days.");
            else
            {
                foreach (var decision in found)
                    Print(host.Out, "  " + OneToOnePlanner.Describe(decision));
                Print(host.Out, $"{found.Count} 1:1(s) in the next {days} days; a run {Mode(one.Act, one.MaxActions)}");
            }
        }
        if (settings.CatchUps is { } catchUps)
        {
            var (found, unreadable, watched) = OneToOnePlanner.PlanCatchUps(settings, calendar, daysAhead: daysArg);
            var days = daysArg is { } x and not 0 ? x : catchUps.DaysAhead is { } y and not 0 ? y
                : settings.OneToOnes?.DaysAhead is { } z and not 0 ? z : settings.Invites!.Days(Clock.Now);
            Print(host.Out, watched.Count == 0 ? $"No catch-ups in the next {days} days."
                : $"Catch-ups in the next {days} days ({watched.Count}): {string.Join("; ", watched)}");
            foreach (var name in unreadable)
                Print(host.Err, $"Couldn't read {name}'s calendar (free/busy): their leave isn't known, so only yours counts for those catch-ups.");
            if (found.Count == 0)
                Print(host.Out, $"Nobody's off for any of your catch-ups in the next {days} days.");
            else
            {
                foreach (var decision in found)
                    Print(host.Out, "  " + OneToOnePlanner.Describe(decision));
                Print(host.Out, $"{found.Count} catch-up(s) in the next {days} days; a run {Mode(catchUps.Act, catchUps.MaxActions)}");
            }
        }
        return 0;
    }

    /// <summary>A hold in your own calendar (1.18.0), for the find-a-slot skill: never inviting people (there's no way to
    /// add one), so it notifies nobody; you open it, add the people and send. --room (1.23.0) books one room from [hold]
    /// rooms as a resource attendee, and reports the room's answer. Without --yes it only shows what it would
    /// create. A create that may have reached Microsoft is never retried: it says to check the calendar.</summary>
    private static int HoldCommand(Args args, Settings settings, IGraph graph, WriteDoor door, Action<string> log, CliHost host)
    {
        DateTime start, end;
        try
        {
            start = PyTime.FromIso(args.Positional[0]);
            end = args.Positional[1].Contains('T') ? PyTime.FromIso(args.Positional[1]) : start.Date + PyTime.TimeFromIso(args.Positional[1]);
        }
        catch (FormatException)
        {
            Print(host.Err, "Start is YYYY-MM-DDTHH:MM and end is HH:MM (or YYYY-MM-DDTHH:MM), in local time.");
            return 2;
        }
        var subject = args.Positional[2].Trim();
        if (subject.Length == 0)
        {
            Print(host.Err, "The hold needs a subject.");
            return 2;
        }
        if (end <= start)
        {
            Print(host.Err, $"The end ({PyTime.Strftime(end, "%d/%m %H:%M")}) must be after the start ({PyTime.Strftime(start, "%d/%m %H:%M")}).");
            return 2;
        }
        if (start <= Clock.Now)
        {
            Print(host.Err, $"The start ({PyTime.Strftime(start, "%a %d/%m %H:%M")}) is in the past.");
            return 2;
        }
        var body = args.Value("--body") ?? (args.Value("--body-file") is { } file ? PyStr.ReadText(file) : "");
        var location = args.Value("--location");
        var online = args.Flag("--online");
        HoldRoom? room = null;
        if (args.Value("--room") is { } wanted)
        {
            room = Holds.FindRoom(settings.HoldRooms, wanted);
            if (room is null)
            {
                Print(host.Err, Holds.NotARoom(settings.HoldRooms, wanted, "a hold"));
                return 2;
            }
        }
        var what = $"\"{subject}\", {PyTime.Strftime(start, "%a %d %b %H:%M")}-{PyTime.Strftime(end, "%H:%M")}, busy, "
                   + (room is null ? "no attendees" : $"books {room.Name}; no people invited")
                   + (room is null && location is { Length: > 0 } ? $", at {location}" : "") + (online ? ", a Teams meeting" : "");
        var calendar = new Calendar(graph, door.Calendar);
        // 1.30.0: an earlier hold of this time and subject whose answer was lost: is it in the calendar now?
        var again = args.Flag("--again");
        var holdKey = UnconfirmedWrites.HoldKey(subject, start, end);
        var earlier = UnconfirmedWrites.Check(settings, holdKey, () => Holds.Find(calendar, subject, start, end), UnconfirmedWrites.Held, log);
        if (earlier.Seen == UnconfirmedWrites.Seen.Went)
        {
            Print(host.Out, earlier.Text);
            Print(host.Out, "Nothing to do: the hold is in your calendar.");
            return 0;
        }
        var pending = earlier.Seen == UnconfirmedWrites.Seen.Pending;
        if (pending && args.Flag("--yes") && !again)
        {
            log($"unconfirmed: skipped {UnconfirmedWrites.Describe(earlier.Entry!)}: an earlier one may not have landed");
            Print(host.Err, earlier.Text + " Not created again: once you've checked, `--again --yes` creates it.");
            return 1;
        }
        if (!args.Flag("--yes"))
        {
            if (pending)
                Print(host.Out, earlier.Text);
            Print(host.Out, $"Would create in your calendar: {what}.");
            if (body.Length > 0)
                Print(host.Out, "  Body: " + PyStr.Head(body.Replace("\n", " / "), 300));
            Print(host.Out, "Nothing written." + (!pending ? " Run again with --yes to create it."
                : again ? " Run again with --again --yes to create it."
                : " --yes would skip it, as the earlier hold may have been made; once you've checked Outlook, --again --yes creates it."));
            return 0;
        }
        if (pending)
            log($"unconfirmed: sending {UnconfirmedWrites.Describe(earlier.Entry!)} again (--again)");
        JsonObject? created;
        try
        {
            created = calendar.Hold(subject, start, end, body, location, online, room, settings.HoldRooms.Select(r => r.Address).ToList());
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is >= 400 and < 500)
        {
            Print(host.Err, $"Microsoft refused the hold ({(int)error.Status}): nothing was created. {PyStr.Head(error.Message, 200)}");
            return 1;
        }
        catch (Exception error) when (error is not WriteRefusedException and not InvalidOperationException)
        {
            log($"hold failed, may have reached Microsoft: {subject} {PyTime.Strftime(start, "%d/%m %H:%M")}: {error.GetType().Name}: {error.Message}");
            UnconfirmedWrites.Record(settings, holdKey, UnconfirmedWrites.HoldEntry(subject, start, end), $"{error.GetType().Name}: {error.Message}", log);
            Print(host.Err, "The hold may or may not have been created (the call failed after it was sent). Check your calendar before "
                            + "trying again: it isn't retried, so it can't be made twice. It's recorded: the next `hold` of it checks whether it's there.");
            return 1;
        }
        if (door.Mode != WriteMode.Live)
        {
            Print(host.Out, $"Journalled, not sent: {what}.");
            return 0;
        }
        if (pending)
            UnconfirmedWrites.Clear(settings, holdKey, "made again with --again, and confirmed", log);
        var id = created?.Str("id");
        string? answer = null;
        if (id is not null)
        {
            Print(host.Out, $"Held: {what}.");
            Print(host.Out, $"  id   {id}");
            try
            {
                // The room's mailbox answers on its own, usually within seconds: read back a few times for its answer.
                // Only reads: the create is never repeated, whatever the room says.
                var back = Holds.ReadUntil(() => calendar.Event(id), e => room is null || Holds.RoomAnswer(e, room.Address) is not null, host.Sleep);
                answer = room is null ? null : Holds.RoomAnswer(back, room.Address);
                if (back.Str("webLink") is { } link)
                    Print(host.Out, $"  open {link}");
            }
            catch (Exception error) when (error is not WriteRefusedException)
            {
                Print(host.Out, $"  (couldn't read it back: {PyStr.Head(error.Message, 120)}; it was created)");
            }
            if (room is not null)
                Print(host.Out, answer switch
                {
                    "accepted" => $"  room {room.Name} accepted: it's booked.",
                    "tentativelyAccepted" => $"  room {room.Name} tentatively accepted: check the hold in Outlook.",
                    "declined" => $"  room {room.Name} DECLINED (probably already booked at that time): the hold stands, without the room. "
                                  + "Not retried: pick another room in Outlook, or hold again with another --room after deleting this one.",
                    _ => $"  room {room.Name} hasn't answered yet: check the hold in Outlook before relying on the room.",
                });
        }
        else
            Print(host.Out, $"Held: {what} (Microsoft returned no id: check your calendar).");
        Triage.RecordHeld(settings, subject, start, end, id, room?.Name, answer);
        log($"held  {subject}: {PyTime.Strftime(start, "%a %d/%m %H:%M")}-{PyTime.Strftime(end, "%H:%M")}, "
            + (room is null ? "no attendees" : $"room {room.Name} ({answer ?? "no answer yet"}), no people invited"));
        return 0;
    }

    /// <summary>`mail-triage sent` (1.19.0): what [sent] would do in Sent Items; with --yes, do it. Under the run lock.</summary>
    private static int SentCommand(Args args, Settings settings, IGraph graph, WriteDoor door, Action<string> log, CliHost host)
    {
        if (settings.Sent is null)
        {
            Print(host.Err, $"{settings.Path} has no [sent] section, so nothing in Sent Items is touched.");
            return 2;
        }
        using (Locks.Exclusive(Watcher.RunLock(settings), 600, "triage run"))
        {
            var mailbox = new Mailbox(graph, door.Mail, !args.Flag("--yes"));
            var state = StateFiles.LoadObject(settings.SentFile, () => []);
            var (findings, first, read) = SentItems.Plan(settings, mailbox, state, Clock.Now);
            if (!args.Flag("--yes"))
            {
                foreach (var line in SentItems.Describe(findings, first, read, false, settings.Sent))
                    Print(host.Out, line);
                Print(host.Out, "Nothing changed. `mail-triage sent --yes` does it" + (first ? ", and from then on each run tidies what's new." : "."));
                return 0;
            }
            SentItems.Apply(settings, mailbox, state, findings, first, log, ActivityJournal.By.Hand);
            foreach (var line in SentItems.Describe(findings, first, read, true, settings.Sent))
                Print(host.Out, line);
            if (door.Mode != WriteMode.Live)
                Print(host.Out, "(Journalled, not sent: Sent Items is unchanged, and the next run starts from the same place.)");
            return 0;
        }
    }

    /// <summary>A preview: each weekday from today, what [leave]'s shared rules make of it. It only reads.</summary>
    private static int DaysOffCommand(Args args, Settings settings, IGraph graph, CliHost host)
    {
        if (settings.Invites is null)
        {
            Print(host.Err, $"{settings.Path} has no [invites] section.");
            return 2;
        }
        var today = DateOnly.FromDateTime(Clock.Now);
        var days = args.Value("--days") is { } d ? long.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : settings.Invites.Days(Clock.Now);
        var events = new Calendar(graph, WriteDoor.Refusing().Calendar).Events(today, days);
        var lines = DaysOffLines(settings, events, today, days);
        foreach (var l in lines)
            Print(host.Out, l.Text);
        Print(host.Out, $"{lines.Count} weekday(s) from {PyTime.Strftime(today, "%d/%m")}: {lines.Count(l => l.New != "working")} off in some way.");
        return 0;
    }

    /// <summary>One line per weekday: (text, verdict).</summary>
    internal static List<(string Text, string New)> DaysOffLines(Settings settings, List<JsonObject> events, DateOnly today, long days)
    {
        var view = Leave.View(settings, events);
        var output = new List<(string, string)>();
        for (var day = today; day < today.AddDays((int)days); day = day.AddDays(1))
        {
            if (PyTime.Weekday(day) >= 5)
                continue;
            var d = view.Days.GetValueOrDefault(day);
            var now = Leave.Verdict(d);
            var entry = d?.Entry?.Subject;
            output.Add(($"  {PyTime.Strftime(day, "%a %d %b")}  {now,-13}{(entry is null ? "" : $"  {entry}")}", now));
        }
        return output;
    }

    /// <summary>A preview (1.13.0): it only reads (your calendar, and attendees' free/busy). Writes are refused outright.</summary>
    private static int DoubleBookingsCommand(Settings settings, IGraph graph, CliHost host)
    {
        if (settings.Invites is not { DoubleBookings: true })
        {
            Print(host.Err, $"{settings.Path} doesn't have double_bookings = true in [invites].");
            return 2;
        }
        var found = DoubleBookingPlanner.Plan(settings, new Calendar(graph, WriteDoor.Refusing().Calendar));
        var days = settings.Invites.Days(Clock.Now);
        if (found.Count == 0)
        {
            Print(host.Out, $"No double bookings in the next {days} days.");
            return 0;
        }
        foreach (var r in found)
            Print(host.Out, "  " + DoubleBookingPlanner.Describe(r));
        var config = settings.DoubleBookings;
        var acting = found.Count(r => r.Action is "move" or "decline");
        Print(host.Out, $"{found.Count} double booking(s) in the next {days} days, {acting} settled by a rule; "
                        + (config is null ? "there are no [[double_booking_rule]]s, so each is queued."
                            : config.Act ? $"a run acts on these, up to {config.MaxActions} changes at a time."
                            : "a run only records these (act = false), and queues each as before."));
        return 0;
    }

    private static int AutostartCommand(string action, Settings settings, CliHost host)
    {
        var task = host.Tasks(Autostart.TaskName);
        if (action == "on")
        {
            host.Tasks(Autostart.OldTaskName).Disable();
            var (exe, args) = Autostart.Command(host.ProgramDir, settings.Path);
            var command = task.Enable(exe, args, Path.GetDirectoryName(settings.Path), settings.Schedule.ToTaskSchedule(TaskKind.Watcher));
            Print(host.Out, $"Autostart is on: '{Autostart.TaskName}' runs at every logon, and has started now. It triages at once, then "
                            + $"{settings.ScheduleText()}.\n  {command}");
        }
        else if (action == "off")
        {
            var was = task.Disable() | host.Tasks(Autostart.OldTaskName).Disable();
            Print(host.Out, was ? "Autostart is off." : "Autostart was already off.");
        }
        else if (action == "restart")
        {
            try
            {
                Print(host.Out, task.Restart()
                    ? $"Restarted '{Autostart.TaskName}': the watcher is running the installed version."
                    : "Autostart is off, so there's nothing to restart. `mail-triage autostart on` turns it on.");
            }
            catch (InvalidOperationException error)
            {
                Print(host.Err, error.Message);
                return 1;
            }
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
                if (ScheduleDrift.Advice(ScheduleDrift.Check(info, settings.Schedule.Fingerprint(TaskKind.Watcher)), "mail-triage") is { } advice)
                    Print(host.Out, "  " + advice);
            }
        }
        return 0;
    }
}
