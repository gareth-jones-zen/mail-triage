using WinBackground;

namespace MailTriage.Tests;

/// <summary>[schedule], background-tools-lib's shared scheduling settings (1.16.0), and the old top-level form.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private const string Folder = "\n[[folder]]\nname = \"Triage\"\n";

    [Fact]
    public void with_no_schedule_the_defaults_are_unchanged()
    {
        var s = dir.Settings(Folder);
        Assert.Equal(15, s.EveryMinutes);
        Assert.Equal(("07:00", "19:00"), s.Hours);
        Assert.True(s.Weekdays);
        Assert.False(s.ScheduleFromOldKeys);
        var monday = new DateTime(2026, 11, 9);
        Assert.True(Watcher.Due(s, monday.AddHours(7)));
        Assert.False(Watcher.Due(s, monday.AddHours(19)));
        Assert.False(Watcher.Due(s, new DateTime(2026, 11, 14, 10, 0, 0)));   // Saturday
    }

    [Fact]
    public void a_schedule_table_is_read()
    {
        var s = dir.Settings("[schedule]\nevery_minutes = 5\nhours = \"00:00-24:00\"\ndays = \"Mon-Sun\"\n" + Folder);
        Assert.Equal(5, s.EveryMinutes);
        Assert.Equal(("00:00", "24:00"), s.Hours);
        Assert.False(s.Weekdays);
        Assert.False(s.ScheduleFromOldKeys);
        Assert.True(Watcher.Due(s, new DateTime(2026, 11, 14, 23, 59, 0)));   // Saturday night
        Assert.Equal("every 5 minutes, 00:00-24:00", s.ScheduleText());
    }

    [Fact]
    public void the_old_top_level_keys_still_work()
    {
        var s = dir.Settings("every_minutes = 5\nhours = \"00:00-24:00\"\nweekdays = false\n" + Folder);
        Assert.True(s.ScheduleFromOldKeys);
        Assert.Equal(5, s.EveryMinutes);
        Assert.Equal(("00:00", "24:00"), s.Hours);
        Assert.False(s.Weekdays);
        var schedule = dir.Settings("[schedule]\nevery_minutes = 5\nhours = \"00:00-24:00\"\ndays = \"Mon-Sun\"\n" + Folder, "new.toml");
        Assert.Equal(schedule.Schedule.Fingerprint(TaskKind.Watcher), s.Schedule.Fingerprint(TaskKind.Watcher));
        foreach (var t in new[] { new DateTime(2026, 11, 14, 3, 0, 0), new DateTime(2026, 11, 9, 12, 0, 0) })
            Assert.Equal(Watcher.Due(schedule, t), Watcher.Due(s, t));
    }

    [Fact]
    public void both_forms_at_once_are_refused()
    {
        var error = Assert.Throws<SettingsException>(() => dir.Settings("every_minutes = 5\n[schedule]\nevery_minutes = 5\n" + Folder));
        Assert.Contains("[schedule]", error.Message);
    }

    [Fact]
    public void a_bad_schedule_is_refused()
    {
        Assert.Throws<SettingsException>(() => dir.Settings("[schedule]\nevery_minute = 5\n" + Folder));
        Assert.Throws<SettingsException>(() => dir.Settings("[schedule]\ndaily_at = \"07:00\"\n" + Folder, "b.toml"));   // a watcher has no daily_at
        Assert.Throws<SettingsException>(() => dir.Settings("[schedule]\nevery_minutes = 0\n" + Folder, "c.toml"));
    }

    [Fact]
    public void autostart_status_says_when_the_task_no_longer_matches()
    {
        dir.Write("config.toml", "[schedule]\nevery_minutes = 5\n" + Folder);
        var task = new FakeTask();
        string Status()
        {
            var output = new StringWriter();
            MailTriage.Cli.Run(["--config", Path.Combine(dir.Path, "config.toml"), "autostart", "status"],
                new CliHost { Out = output, Err = output, Tasks = _ => task, Graph = (_, _) => throw new InvalidOperationException() });
            return output.ToString();
        }
        task.Description = "Applies standing rules";
        Assert.Contains("registered before its schedule was recorded", Status());
        task.Description = ScheduleDrift.WithFingerprint("Applies standing rules", "v1 job every=15 daily=- logon=yes unlock=0");
        Assert.Contains("settings changed since this task was registered: run `mail-triage autostart on`", Status());
        task.Description = ScheduleDrift.WithFingerprint("Applies standing rules", "v1 watcher logon=yes");
        Assert.DoesNotContain("autostart on`", Status());
    }

    private sealed class FakeTask : ITask
    {
        public string Description { get; set; } = "";
        public string Name => "mail-triage watch";
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => "";
        public bool Disable() => true;
        public bool Stop() => true;
        public IReadOnlyDictionary<string, string>? Status() =>
            new Dictionary<string, string> { ["state"] = "Running", ["description"] = Description };
    }
}
