namespace Frametide.Services;

/// <summary>Keeps Frametide to one instance per user session; later starts ask the first one to show its window.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Frametide.Instance";
    private const string ShowEventName = @"Local\Frametide.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show) { _mutex = mutex; _show = show; }

    /// <summary>The instance lock, or null when Frametide is already running.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (!created) { mutex.Dispose(); return null; }
        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName));
    }

    public static void SignalFirst()
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) using (show) show.Set();
    }

    /// <summary>Calls <paramref name="onShow"/> (on a thread pool thread) whenever another start asks to show the window.</summary>
    public void Listen(Action onShow) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
