using System.Diagnostics;
using Screen.Core;

namespace Screen.App;

internal sealed class HybridScreenCaptureService : IScreenCaptureService
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly IScreenCaptureService _dxgi;
    private readonly IScreenCaptureService _gdi;
    private readonly Action _resetPrimary;
    private readonly TimeSpan _idleTimeout;
    private readonly Timer _idleTimer;
    private long _lastActiveTimestamp;
    private int _activeCaptures;
    private int _disposed;

    public HybridScreenCaptureService()
        : this(DefaultIdleTimeout)
    {
    }

    internal HybridScreenCaptureService(TimeSpan idleTimeout)
    {
        var dxgi = new DxgiScreenCaptureService();
        _dxgi = dxgi;
        _gdi = new GdiScreenCaptureService();
        _resetPrimary = dxgi.Reset;
        _idleTimeout = idleTimeout > TimeSpan.Zero ? idleTimeout : Timeout.InfiniteTimeSpan;
        _idleTimer = new Timer(OnIdleTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal HybridScreenCaptureService(
        IScreenCaptureService primary,
        IScreenCaptureService fallback,
        Action resetPrimary,
        TimeSpan idleTimeout)
    {
        _dxgi = primary;
        _gdi = fallback;
        _resetPrimary = resetPrimary;
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
            var frame = await _dxgi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
            if (frame.ColorSpace != CaptureColorSpace.Srgb)
            {
                // Desktop Duplication's default BGRA8 surface cannot retain HDR values.
                // GDI supplies the compositor's SDR view and avoids the common washed-out result.
                return await _gdi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
            }

            if (CaptureFrameQuality.IsSuspiciouslyBlack(frame))
            {
                // Desktop Duplication can occasionally hand out a valid-sized, zero-filled
                // surface while the output is waking or the duplication session is stale.
                await Task.Delay(45, cancellationToken).ConfigureAwait(false);
                var retry = await _dxgi.CaptureAtAsync(screenPoint, cancellationToken).ConfigureAwait(false);
                if (retry.ColorSpace == CaptureColorSpace.Srgb
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
