// Interim: belongs in background-tools-lib's WinBackground, beside NamedLock (win_background.exclusive in Python).
using WinBackground;

namespace MailTriage.Interim;

/// <summary>win_background's exclusive() and single_instance(), over WinBackground's NamedLock: the same Local\ mutex
/// names as the Python version, so a Python process and a .NET process exclude each other. NamedLock owns its mutex on
/// a thread of its own (background-tools-lib a1e4d97), so a release after an await still releases it.</summary>
public static class Exclusive
{
    /// <summary>exclusive(name, timeout): waits up to timeout seconds for the lock, then throws TimeoutException with
    /// Python's wording.</summary>
    public static IDisposable Acquire(string name, double timeoutSeconds = 600, string what = "run")
    {
        var held = new NamedLock(name);
        if (held.Acquire(timeoutSeconds))
            return held;
        held.Dispose();
        throw new TimeoutException($"Another {what} didn't finish within {Py.Fixed(timeoutSeconds / 60, 0)} minutes.");
    }

    /// <summary>single_instance(name): held until disposed or the process ends; null if it's already held.</summary>
    public static IDisposable? SingleInstance(string name) => Locks.SingleInstance(name);
}
