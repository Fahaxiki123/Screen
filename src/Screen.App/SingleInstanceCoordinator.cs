namespace Screen.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\Screen.NativeScreenshot.SingleInstance.v1";
    private const string CaptureEventName = @"Local\Screen.NativeScreenshot.CaptureRequested.v1";
    private Mutex? _mutex;
    private EventWaitHandle? _captureEvent;
    private EventWaitHandle? _shutdownEvent;
    private Task? _listener;
    private bool _ownsMutex;
    private bool _disposed;
    private readonly string _mutexName;
    private readonly string _captureEventName;

    public SingleInstanceCoordinator() : this(MutexName, CaptureEventName)
    {
    }

    internal SingleInstanceCoordinator(string mutexName, string captureEventName)
    {
        _mutexName = mutexName;
        _captureEventName = captureEventName;
    }

    public bool TryBecomePrimary()
    {
        _mutex = new Mutex(true, _mutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _captureEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            _captureEventName);
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        return true;
    }

    public void StartListening(Action captureRequested)
    {
        if (!_ownsMutex || _captureEvent is not { } capture || _shutdownEvent is not { } shutdown)
        {
            throw new InvalidOperationException("Only the primary instance can listen for requests.");
        }

        _listener = Task.Factory.StartNew(
            () =>
            {
                var handles = new WaitHandle[] { shutdown, capture };
                while (WaitHandle.WaitAny(handles) == 1 && !shutdown.WaitOne(0))
                {
                    captureRequested();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public static void NotifyPrimary(bool requestCapture)
    {
        if (!requestCapture)
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var captureEvent = EventWaitHandle.OpenExisting(CaptureEventName);
                captureEvent.Set();
                return;
            }
            catch (Exception exception) when (
                exception is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                if (attempt == 9)
                {
                    return;
                }
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _shutdownEvent?.Set();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The completion path below observes and logs the listener failure.
        }

        if (_listener is { IsCompleted: false } pending)
        {
            _ = pending.ContinueWith(CloseListenerEvents, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            CloseListenerEvents(_listener);
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }
        _mutex?.Dispose();
        _ownsMutex = false;
    }

    private void CloseListenerEvents(Task? listener)
    {
        if (listener?.Exception is { } exception)
        {
            SimpleLogger.LogError("Single-instance request listener failed", exception);
        }
        _shutdownEvent?.Dispose();
        _captureEvent?.Dispose();
    }
}
