using System.Windows.Media.Imaging;
using Screen.Core;

namespace Screen.App;

internal enum ScrollFrameOutcome
{
    Added,
    Unchanged,
    Rejected,
    Unstable
}

internal sealed record ScrollFrameCapture(ScrollFrameOutcome Outcome, string? FailureReason = null);

internal sealed record ScrollPreviewFrame(BitmapSource Image, int Width, int Height, int FrameCount);

/// <summary>
/// Keeps acquisition independent of the increasingly expensive full preview.
/// Only the preceding accepted viewport is needed to validate a new seam.
/// </summary>
internal sealed class ScrollCaptureSequence
{
    private readonly Func<CancellationToken, Task<PixelBuffer>> _capture;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly VerticalScrollStitcher.Session _stitch = new();
    private readonly ScrollPreviewBuilder _preview = new();
    private Task<ScrollFrameCapture>? _pendingCapture;
    private PixelBuffer? _lastObserved;
    private int _stableObservations;

    public ScrollCaptureSequence(
        Func<CancellationToken, Task<PixelBuffer>> capture,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _capture = capture;
        _delay = delay ?? Task.Delay;
    }

    public int FrameCount => _stitch.Count;
    public CancellationToken CancellationToken => _cancellation.Token;

    public Task<ScrollFrameCapture> CaptureAsync() =>
        _pendingCapture is { IsCompleted: false }
            ? _pendingCapture
            : _pendingCapture = CaptureCoreAsync();

    private async Task<ScrollFrameCapture> CaptureCoreAsync()
    {
        var token = _cancellation.Token;
        token.ThrowIfCancellationRequested();
        var crop = await _capture(token);
        token.ThrowIfCancellationRequested();
        // Stillness belongs to consecutive screen observations, not to the
        // last viewport admitted by the stitcher. A settled hover/repaint can
        // remain different from that viewport indefinitely.
        _stableObservations = _lastObserved is not null && AreNearlyEqual(_lastObserved, crop)
            ? Math.Min(2, _stableObservations + 1)
            : 0;
        _lastObserved = crop;
        if (_stitch.LastFrame is { } previous && AreNearlyEqual(previous, crop))
        {
            return new ScrollFrameCapture(ScrollFrameOutcome.Unchanged);
        }

        var failure = await Task.Run(() => _stitch.TryAppend(crop, token), token);
        return failure is null
            ? new ScrollFrameCapture(ScrollFrameOutcome.Added)
            : new ScrollFrameCapture(ScrollFrameOutcome.Rejected, failure);
    }

    /// <summary>
    /// First drains the in-flight capture, including the initial viewport.
    /// Each settling sample is admitted too, so smooth scrolling cannot skip
    /// an entire viewport while completion is waiting for the last frame.
    /// </summary>
    public async Task<ScrollFrameCapture> FlushAsync()
    {
        if (_pendingCapture is not null)
        {
            await _pendingCapture;
        }
        var outcome = new ScrollFrameCapture(ScrollFrameOutcome.Unchanged);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await _delay(TimeSpan.FromMilliseconds(80), _cancellation.Token);
            outcome = await CaptureAsync();
            if (_stableObservations >= 2)
            {
                return outcome;
            }
        }
        return outcome.Outcome == ScrollFrameOutcome.Rejected
            ? outcome
            : new ScrollFrameCapture(ScrollFrameOutcome.Unstable);
    }

    // The controller serializes preview updates. Finish uses its own immutable
    // layout and can run even if a preview worker is still returning.
    public Task<ScrollPreviewFrame> UpdatePreviewAsync()
    {
        var layout = _stitch.Snapshot();
        var token = _cancellation.Token;
        return Task.Run(() => new ScrollPreviewFrame(
            _preview.Update(layout, token).ToBitmapSource(), layout.Width, layout.Height, layout.Frames.Length), token);
    }

    public Task<StitchResult> ComposeAsync()
    {
        var layout = _stitch.Snapshot();
        var token = _cancellation.Token;
        return Task.Run(() => VerticalScrollStitcher.Compose(layout, token), token);
    }

    public void Cancel() => _cancellation.Cancel();

    internal static bool AreNearlyEqual(PixelBuffer first, PixelBuffer second)
    {
        if (first.Width != second.Width || first.Height != second.Height)
        {
            return false;
        }
        // An average over the whole (often white) page hides newly exposed
        // text. Count materially changed pixels instead, allowing only tiny
        // cursor/antialiasing differences rather than a whole clipped line.
        var allowedChangedPixels = Math.Max(4, (long)first.Width * first.Height / 2000);
        var changed = 0;
        for (var y = 0; y < first.Height; y++)
        {
            var firstRow = y * first.Stride;
            var secondRow = y * second.Stride;
            for (var x = 0; x < first.Width; x++)
            {
                var a = firstRow + x * 4;
                var b = secondRow + x * 4;
                if ((Math.Abs(first.Pixels[a] - second.Pixels[b]) > 2
                    || Math.Abs(first.Pixels[a + 1] - second.Pixels[b + 1]) > 2
                    || Math.Abs(first.Pixels[a + 2] - second.Pixels[b + 2]) > 2)
                    && ++changed > allowedChangedPixels)
                {
                    return false;
                }
            }
        }
        return true;
    }
}

internal sealed class ScrollCaptureBottomDetector
{
    private int _unchanged;

    public bool Observe(ScrollFrameOutcome outcome)
    {
        _unchanged = outcome == ScrollFrameOutcome.Unchanged ? _unchanged + 1 : 0;
        return _unchanged >= 3;
    }

    public void Reset() => _unchanged = 0;
}
