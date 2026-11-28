using System.Diagnostics;
using Screen.Core;

namespace Screen.App;

internal sealed class HybridScreenCaptureService : IScreenCaptureService
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly IScreenCaptureService _dxgi;
    private readonly IScreenCaptureService _gdi;
    private readonly Action _resetPrimary;
    private readonly Func<CancellationToken, bool>? _warmUpPrimary;
    private readonly TimeSpan _idleTimeout;
    private readonly Timer _idleTimer;
    private long _lastActiveTimestamp;
    private int _activeCaptures;
    private int _primaryReady;
    private int _warmedUp;
    private int _disposed;

    public HybridScreenCaptureService(bool deferPrimaryUntilWarmed = false)
        : this(DefaultIdleTimeout, deferPrimaryUntilWarmed)
    {
    }

    internal HybridScreenCaptureService(TimeSpan idleTimeout, bool deferPrimaryUntilWarmed = false)
    {
        var dxgi = new DxgiScreenCaptureService();
        _dxgi = dxgi;
        _gdi = new GdiScreenCaptureService();
        _resetPrimary = dxgi.Reset;
        _warmUpPrimary = dxgi.WarmUp;
        _primaryReady = deferPrimaryUntilWarmed ? 0 : 1;
        _idleTimeout = idleTimeout > TimeSpan.Zero ? idleTimeout : Timeout.InfiniteTimeSpan;
        _idleTimer = new Timer(OnIdleTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal HybridScreenCaptureService(
        IScreenCaptureService primary,
        IScreenCaptureService fallback,
        Action resetPrimary,
        TimeSpan idleTimeout,
        Func<CancellationToken, bool>? warmUpPrimary = null,
        bool deferPrimaryUntilWarmed = false)
    {
        _dxgi = primary;
        _gdi = fallback;
        _resetPrimary = resetPrimary;
        _warmUpPrimary = warmUpPrimary;
        _primaryReady = deferPrimaryUntilWarmed ? 0 : 1;
        _idleTimeout = idleTimeout > TimeSpan.Zero ? idleTimeout : Timeout.InfiniteTimeSpan;
        _idleTimer = new Timer(OnIdleTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async Task<CaptureFrame> CaptureAtAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Increment(ref _activeCaptures);
        Volatile.Write(ref _lastActiveTimestamp, Stopwatch.GetTimestamp());
        try
        {
            if (Volatile.Read(ref _primaryReady) == 0)
            {
                // While DXGI/Vortice assemblies are still being pre-warmed out-of-process right
                // after boot, serve the screenshot immediately via signed GDI instead of blocking
                // seconds on cold native/cloud DLL verification.
                return await _gdi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
            }

            var frame = await _dxgi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
            if (frame.MayContainSystemCursor || frame.ColorSpace != CaptureColorSpace.Srgb)
            {
                // Desktop Duplication's default BGRA8 surface cannot retain HDR values.
                // It can also supply a surface that already includes the system cursor.
                // The GDI copy supplies SDR desktop pixels without drawing the pointer.
                return await _gdi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
            }

            if (CaptureFrameQuality.IsSuspiciouslyBlack(frame))
            {
                // Desktop Duplication can occasionally hand out a valid-sized, zero-filled
                // surface while the output is waking or the duplication session is stale.
                await Task.Delay(45, cancellationToken).ConfigureAwait(false);
                var retry = await _dxgi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
                if (retry.ColorSpace == CaptureColorSpace.Srgb
                    && !retry.MayContainSystemCursor
                    && !CaptureFrameQuality.IsSuspiciouslyBlack(retry))
                {
                    return retry;
                }

                _resetPrimary();
                return await _gdi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
            }

            return frame;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await _gdi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _lastActiveTimestamp, Stopwatch.GetTimestamp());
            if (Interlocked.Decrement(ref _activeCaptures) == 0)
            {
                ScheduleIdleRelease();
            }
        }
    }

    public void ReleaseSessions() => _resetPrimary();

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.CompletedTask;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        if (Interlocked.Exchange(ref _warmedUp, 1) != 0)
        {
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sessionReady = _warmUpPrimary?.Invoke(cancellationToken) ?? true;
                _ = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(0, 0));
                using var bitmap = new System.Drawing.Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.Clear(System.Drawing.Color.Black);
                if (sessionReady)
                {
                    Volatile.Write(ref _lastActiveTimestamp, Stopwatch.GetTimestamp());
                    ScheduleIdleRelease();
                }
                else
                {
                    // Allow a later SessionUnlock/ConsoleConnect event to establish the live DXGI session.
                    Interlocked.Exchange(ref _warmedUp, 0);
                }
                Interlocked.Exchange(ref _primaryReady, 1);
                SimpleLogger.LogInfo(
                    $"Capture pipeline warmed up in {timer.Elapsed.TotalMilliseconds:F1} ms (dxgi session: {sessionReady})");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _warmedUp, 0);
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _primaryReady, 1);
                SimpleLogger.LogWarning($"Capture pipeline warm-up skipped: {exception.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _idleTimer.Dispose();
        _dxgi.Dispose();
        _gdi.Dispose();
    }

    private void ScheduleIdleRelease()
    {
        if (Volatile.Read(ref _disposed) != 0 || _idleTimeout == Timeout.InfiniteTimeSpan)
        {
            return;
        }
        try
        {
            _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnIdleTimerTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _activeCaptures) != 0)
        {
            return;
        }
        var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActiveTimestamp));
        if (elapsed < _idleTimeout)
        {
            try
            {
                var remaining = _idleTimeout - elapsed;
                _idleTimer.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
            return;
        }
        try
        {
            _resetPrimary();
        }
        catch (Exception exception)
        {
            SimpleLogger.LogWarning($"Idle DXGI capture session release failed: {exception.Message}");
        }
    }
}
