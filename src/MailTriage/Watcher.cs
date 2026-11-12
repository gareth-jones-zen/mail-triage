using M365Graph;
using MailTriage.Interim;
using WinBackground;

namespace MailTriage;

/// <summary>Watch mode (watch.py): triage at start, then every every_minutes within working hours.
/// - The first run is at start, whatever the time; after that only within hours (and Monday to Friday).
/// - One watcher per settings file: the same mutex name as the Python version, so a second (of either) exits (3).
/// - The settings file is re-read each run. It never opens a browser.
/// - A failure is notified only after three in a row (a sign-in problem at once), and once until a run succeeds.
/// - It logs `alive` once an hour, in the same format (session-start parses these lines).</summary>
public static class Watcher
{
    public const int AlreadyRunning = 3;

    public static bool Due(Settings settings, DateTime now)
    {
        if (settings.Weekdays && PyTime.Weekday(now) >= 5)
            return false;
        var hm = PyTime.Strftime(now, "%H:%M");
        return string.CompareOrdinal(settings.Hours.Start, hm) <= 0 && string.CompareOrdinal(hm, settings.Hours.End) < 0;
    }

    public static string RunLock(Settings settings) => $"mail-triage-run-{Locks.Key(settings.Path)}";
    public static string WatchLock(Settings settings) => $"mail-triage-watch-{Locks.Key(settings.Path)}";

    /// <summary>Runs until the process ends. runOnce(settings, log) is one triage run (the CLI gives it Graph and the
    /// write door); stop, for tests, ends the loop when it returns true.</summary>
    public static int Watch(Settings settings, Func<Settings, Action<string>, RunReport> runOnce, Action<string, IReadOnlyList<string>> notify,
        Func<bool>? stop = null, Action<TimeSpan>? sleep = null, Func<DateTime>? now = null)
    {
        var clock = now ?? (() => DateTime.Now);
        var pause = sleep ?? Thread.Sleep;
        var log = Logger.Create(settings.LogFile);
        using var held = Exclusive.SingleInstance(WatchLock(settings));   // held until the process ends
        if (held is null)
        {
            log("another watcher is already running for these settings; not starting a second");
            return AlreadyRunning;
        }
        log($"watching {settings.Folders.Count} folder(s) every {settings.EveryMinutes} min, "
            + $"{settings.Hours.Start}-{settings.Hours.End}{(settings.Weekdays ? " Mon-Fri" : "")}, and once now");
        var nextRun = DateTime.MinValue;
        var beat = clock();
        string? failing = null;
        var first = true;
        var streak = 0;   // failures in a row
        while (stop is null || !stop())
        {
            if (clock() >= nextRun)
            {
                nextRun = clock() + TimeSpan.FromMinutes(settings.EveryMinutes);
                if (first || Due(settings, clock()))
                {
                    first = false;
                    try
                    {
                        settings = Settings.Load(settings.Path);
                        RunReport report;
                        using (Exclusive.Acquire(RunLock(settings), 600, "triage run"))
                            report = runOnce(settings, log);
                        if (report.New > 0 && settings.Notify == "new")
                            notify($"{report.New} email{(report.New > 1 ? "s" : "")} to review", ["mail-triage list"]);
                        failing = null;
                        streak = 0;
                    }
                    catch (SignInNeededException error)
                    {
                        log($"sign-in needed: {error.Message}");
                        if (failing != "sign-in")
                            notify("mail-triage needs a sign-in", [PyText.Head(error.Message, 300)]);
                        failing = "sign-in";
                    }
                    catch (Exception error)
                    {
                        log($"run failed: {error.GetType().Name}: {error.Message}");
                        streak++;
                        if (streak == 3)
                            notify("mail-triage couldn't run", [PyText.Head($"{error.GetType().Name}: {error.Message}", 300), $"Log: {settings.LogFile}"]);
                        failing = error.GetType().Name;
                    }
                }
            }
            if (clock() - beat >= TimeSpan.FromHours(1))
            {
                beat = clock();
                log("alive");
            }
            pause(TimeSpan.FromSeconds(30));
        }
        return 0;
    }
}

/// <summary>The watcher at logon, through Task Scheduler (autostart.py). The task runs mail-triage-background.exe
/// --config SETTINGS watch. The same task name as the Python version, so switching either way replaces it.</summary>
public static class Autostart
{
    public const string TaskName = "mail-triage watch";
    public const string OldTaskName = "mail-triage run";   // 0.1.0's daily task
    private static readonly Dictionary<string, string> Results = new() { ["3"] = "another watcher was already running" };

    public static ITask Create(string name) => name == TaskName
        ? new ScheduledTask(name, "Applies standing rules to mail folders as mail arrives (mail-triage watch).", Results)
        : new ScheduledTask(name);

    public static (string Exe, List<string> Args) Command(string programDir, string settingsPath) =>
        (Path.Combine(programDir, "mail-triage-background.exe"), ["--config", PyPath.Resolve(settingsPath), "watch"]);
}
