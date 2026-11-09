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

    public bool TryBecomePrimary()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
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
            CaptureEventName);
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        return true;
    }

    public void StartListening(Action captureRequested)
    {
        if (!_ownsMutex || _captureEvent is null || _shutdownEvent is null)
        {
            throw new InvalidOperationException("Only the primary instance can listen for requests.");
        }

        _listener = Task.Factory.StartNew(
            () =>
            {
                var handles = new WaitHandle[] { _captureEvent, _shutdownEvent };
                while (WaitHandle.WaitAny(handles) == 0)
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
        _shutdownEvent?.Set();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _listener?.Dispose();
        _shutdownEvent?.Dispose();
        _captureEvent?.Dispose();
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
}
