using PyCompat;

namespace MailTriage.Tests;

/// <summary>Settings that name a shared base file (base = "base-config.toml").</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly TempDir dir = new();

    public BaseSettingsTests()
    {
        dir.Write("base-config.toml", $"""
            login_hint = "me@example.com"
            tenant = "contoso"
            msal_cache = "%ROOT%/msal-cache.json"
            time_zone = "GMT Standard Time"

            [paths]
            ROOT = '{dir.Path}\shared'

            [git]
            repo = ".."

            [invites]
            days_ahead = 7
            """);
    }

    public void Dispose() => dir.Dispose();

    private const string Folder = "\n[[folder]]\nname = \"Triage\"\n";

    [Fact]
    public void sign_in_settings_and_path_variables_come_from_the_base()
    {
        var settings = dir.Settings("base = \"base-config.toml\"\nlog_file = \"%ROOT%/mail-triage.log\"\n" + Folder);
        Assert.Equal("me@example.com", settings.LoginHint);
        Assert.Equal("me@example.com", settings.Email);
        Assert.Equal("contoso", settings.Tenant);
        Assert.Equal(PyPath.Pure(System.IO.Path.Combine(dir.Path, "shared", "msal-cache.json")), settings.MsalCache);
        Assert.Equal(PyPath.Pure(System.IO.Path.Combine(dir.Path, "shared", "mail-triage.log")), settings.LogFile);
        Assert.Single(settings.Folders);
    }

    [Fact]
    public void the_tools_own_settings_win()
    {
        var settings = dir.Settings("base = \"base-config.toml\"\nlogin_hint = \"own@example.com\"\ntenant = \"own\"\n" + Folder);
        Assert.Equal(("own@example.com", "own"), (settings.LoginHint, settings.Tenant));
    }

    [Fact]
    public void a_base_table_applies_only_where_this_file_has_the_section()
    {
        Assert.Null(dir.Settings("base = \"base-config.toml\"\n" + Folder).Invites);
        var settings = dir.Settings("base = \"base-config.toml\"\n\n[invites]\nleave_note = \"Away\"\n");
        Assert.Equal(7, settings.Invites!.DaysAhead);
        Assert.Equal("Away", settings.Invites.LeaveNote);
    }

    [Fact]
    public void the_shared_calendar_months_ahead_is_used_when_invites_gives_no_window_of_its_own()
    {
        dir.Write("shared.toml", "login_hint = \"me@example.com\"\ncalendar_months_ahead = 3\n");
        var now = new DateTime(2026, 11, 22, 9, 0, 0);
        var settings = dir.Settings("base = \"shared.toml\"\n\n[invites]\nleave_note = \"Away\"\n", "shared-user.toml");
        Assert.Equal(3, settings.Invites!.Months);
        Assert.Equal(92, settings.Invites.Days(now));   // 22 Nov to 22 Feb
    }

    [Fact]
    public void the_invites_own_window_wins_over_the_shared_one()
    {
        dir.Write("shared.toml", "login_hint = \"me@example.com\"\ncalendar_months_ahead = 3\n");
        var now = new DateTime(2026, 11, 22, 9, 0, 0);
        var months = dir.Settings("base = \"shared.toml\"\n\n[invites]\nmonths_ahead = 1\n", "own-months.toml");
        Assert.Equal(1, months.Invites!.Months);
        Assert.Equal(30, months.Invites.Days(now));   // 22 Nov to 22 Dec
        var days = dir.Settings("base = \"shared.toml\"\n\n[invites]\ndays_ahead = 10\n", "own-days.toml");
        Assert.Null(days.Invites!.Months);
        Assert.Equal(10, days.Invites.Days(now));
    }

    [Fact]
    public void with_no_window_anywhere_invites_look_42_days_ahead()
    {
        var settings = dir.Settings("email = \"me@example.com\"\n\n[invites]\nleave_note = \"Away\"\n", "no-window.toml");
        Assert.Null(settings.Invites!.Months);
        Assert.Equal(42, settings.Invites.Days(new DateTime(2026, 11, 22, 9, 0, 0)));
    }

    [Fact]
    public void the_table_guard_still_refuses_a_split_kind_with_a_base()
    {
        var split = "base = \"base-config.toml\"\n" + Folder + "\n[invites]\n" + Folder;
        var error = Assert.Throws<SettingsException>(() => dir.Settings(split, "split.toml"));
        Assert.Contains("tables, but only", error.Message);
    }

    [Fact]
    public void a_missing_or_broken_base_is_a_settings_error()
    {
        var missing = Assert.Throws<SettingsException>(() => dir.Settings("base = \"nope.toml\"\n" + Folder, "missing.toml"));
        Assert.Contains("doesn't exist", missing.Message);
        dir.Write("broken.toml", "tenant = \"a\"\ntenant = \"b\"\n");
        Assert.Throws<SettingsException>(() => dir.Settings("base = \"broken.toml\"\n" + Folder, "broken-user.toml"));
    }

    [Fact]
    public void a_key_given_twice_in_this_file_is_a_settings_error()
    {
        var error = Assert.Throws<SettingsException>(() => dir.Settings("tenant = \"a\"\ntenant = \"b\"\n" + Folder, "twice.toml"));
        Assert.Contains("isn't valid TOML", error.Message);
    }
}
