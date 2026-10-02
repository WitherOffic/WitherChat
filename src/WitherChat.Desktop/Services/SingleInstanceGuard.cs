namespace WitherChat.Desktop.Services;

internal sealed class SingleInstanceGuard : IDisposable
{
    internal const string DefaultMutexName = "Local\\WitherChat-9E69A68D-87D9-47F1-99AE-F35AA2DCC3EA";
    private const string PortableMutexName = "WitherChat-9E69A68D-87D9-47F1-99AE-F35AA2DCC3EA";

    private Mutex? _mutex;
    private readonly bool _ownsMutex;

    private SingleInstanceGuard(Mutex? mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool IsFirstInstance => _ownsMutex;

    public static SingleInstanceGuard Acquire(string? mutexName = null)
    {
        mutexName ??= OperatingSystem.IsWindows() ? DefaultMutexName : PortableMutexName;
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        try
        {
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            return new SingleInstanceGuard(mutex, createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            return new SingleInstanceGuard(null, ownsMutex: false);
        }
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        if (_ownsMutex)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        mutex.Dispose();
    }
}
