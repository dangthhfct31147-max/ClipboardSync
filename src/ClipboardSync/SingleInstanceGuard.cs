namespace ClipboardSync;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\ClipboardSync.SingleInstance";

    private readonly Mutex? _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, bool hasHandle)
    {
        _mutex = mutex;
        HasHandle = hasHandle;
    }

    public bool HasHandle { get; }

    public static SingleInstanceGuard TryAcquire(string mutexName = DefaultMutexName)
    {
        var mutex = new Mutex(false, mutexName);
        var hasHandle = false;

        try
        {
            hasHandle = mutex.WaitOne(0, false);
            return new SingleInstanceGuard(mutex, hasHandle);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (HasHandle)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }

        _mutex?.Dispose();
    }
}
