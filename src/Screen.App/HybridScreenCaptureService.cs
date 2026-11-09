using Screen.Core;

namespace Screen.App;

internal sealed class HybridScreenCaptureService : IScreenCaptureService
{
    private readonly DxgiScreenCaptureService _dxgi = new();
    private readonly GdiScreenCaptureService _gdi = new();

    public async Task<CaptureFrame> CaptureAtAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default)
    {
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

                _dxgi.Reset();
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
    }

    public void ReleaseSessions() => _dxgi.Reset();

    public void Dispose()
    {
        _dxgi.Dispose();
        _gdi.Dispose();
    }
}
