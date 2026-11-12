// Interim: belongs in background-tools-lib's WinBackground, beside NamedLock (win_background.exclusive and
// single_instance in Python).
using WinBackground;

namespace MailTriage.Interim;

/// <summary>A named lock held by a thread of its own. NamedLock is a Win32 mutex, and ReleaseMutex only works on
/// the thread that acquired it: released from another thread (after an await, say) it fails silently and the lock
/// stays held until the process exits, which would block a Python run or `clear`. Holding it on a dedicated thread
/// makes acquire and release safe from any thread. The same Local\ names as the Python version, so a Python process
/// and a .NET process exclude each other.</summary>
public sealed class ThreadHeldLock : IDisposable
{
    private readonly ManualResetEventSlim released = new(false);
    private readonly Thread holder;
    private int disposed;

    private ThreadHeldLock(string name, double timeoutSeconds)
    {
        Name = name;
        using var ready = new ManualResetEventSlim(false);
        holder = new Thread(() =>
        {
            var mutex = new NamedLock(name);
            Held = mutex.Acquire(timeoutSeconds);
            ready.Set();
            if (!Held)
                return;
            released.Wait();
            mutex.Release();
        })
        { IsBackground = true, Name = $"lock {name}" };
        holder.Start();
        ready.Wait();
    }

    public string Name { get; }

    public bool Held { get; private set; }

    /// <summary>Waits up to timeoutSeconds (0: try once). Null if another process or thread holds it.</summary>
    public static ThreadHeldLock? TryAcquire(string name, double timeoutSeconds = 0)
    {
        var held = new ThreadHeldLock(name, timeoutSeconds);
        if (held.Held)
            return held;
        held.holder.Join();
        held.released.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        released.Set();
        holder.Join();
        released.Dispose();
    }
}

public static class Exclusive
{
    /// <summary>exclusive(name, timeout): waits up to timeout seconds for the lock, then throws TimeoutException with
    /// Python's wording.</summary>
    public static IDisposable Acquire(string name, double timeoutSeconds = 600, string what = "run") =>
        ThreadHeldLock.TryAcquire(name, timeoutSeconds)
        ?? throw new TimeoutException($"Another {what} didn't finish within {Py.Fixed(timeoutSeconds / 60, 0)} minutes.");

    /// <summary>single_instance(name): held until disposed or the process ends; null if it's already held.</summary>
    public static IDisposable? SingleInstance(string name) => ThreadHeldLock.TryAcquire(name, 0);
}
