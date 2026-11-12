using MailTriage.Interim;

namespace MailTriage;

/// <summary>"Now", as Python's datetime.now(): naive local time, to the microsecond. Tests and replays set their
/// own TimeProvider for the code that runs under them (per async flow, so tests can run in parallel).</summary>
public static class Clock
{
    private static readonly AsyncLocal<TimeProvider?> Current = new();

    public static TimeProvider Provider => Current.Value ?? TimeProvider.System;

    public static DateTime Now => PyTime.Micro(Provider.GetLocalNow().DateTime);

    /// <summary>Use this provider until the result is disposed.</summary>
    public static IDisposable Use(TimeProvider provider)
    {
        var previous = Current.Value;
        Current.Value = provider;
        return new Restore(() => Current.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>A fixed local time, in a fixed zone (for tests and replays).</summary>
public sealed class FixedTime(DateTime local, TimeZoneInfo? zone = null) : TimeProvider
{
    private readonly TimeZoneInfo zone = zone ?? TimeZoneInfo.Local;

    public DateTime Local { get; set; } = local;

    public override TimeZoneInfo LocalTimeZone => zone;

    public override DateTimeOffset GetUtcNow() => PyTime.Aware(Local, zone).ToUniversalTime();
}
