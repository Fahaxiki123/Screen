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

/// <summary>
/// Keeps acquisition independent of the increasingly expensive full preview.
/// Only the preceding accepted viewport is needed to validate a new seam.
/// </summary>
internal sealed class ScrollCaptureSequence
{
    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private const long MaximumOutputBytes = 512L * 1024 * 1024;
    private const long MaximumWorkingBytes = 768L * 1024 * 1024;
    private readonly Func<CancellationToken, Task<PixelBuffer>> _capture;
    private readonly IScrollStitcher _stitcher;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<PixelBuffer> _frames = [];
    private Task<ScrollFrameCapture>? _pendingCapture;
    private PixelBuffer? _lastObserved;
    private int _stableObservations;
    private long _inputBytes;
    private int _outputHeight;

    public ScrollCaptureSequence(
        Func<CancellationToken, Task<PixelBuffer>> capture,
        IScrollStitcher stitcher,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _capture = capture;
        _stitcher = stitcher;
        _delay = delay ?? Task.Delay;
    }

    public int FrameCount => _frames.Count;
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
        if (_frames.Count > 0 && AreNearlyEqual(_frames[^1], crop))
        {
            return new ScrollFrameCapture(ScrollFrameOutcome.Unchanged);
        }

        var nextHeight = crop.Height;
        if (_frames.Count > 0)
        {
            PixelBuffer[] pair = [_frames[^1], crop];
            var match = await Task.Run(() => _stitcher.Stitch(pair, token), token);
            token.ThrowIfCancellationRequested();
            if (!match.IsComplete || match.Seams.Count != 1)
            {
                return new ScrollFrameCapture(ScrollFrameOutcome.Rejected, match.FailureReason);
            }
            nextHeight = checked(_outputHeight + match.Seams[0].OutputY);
        }

        var nextInputBytes = checked(_inputBytes + (long)crop.Stride * crop.Height);
        var outputBytes = checked((long)crop.Width * nextHeight * 4);
        if (nextHeight > 100_000 || nextInputBytes > MaximumInputBytes
            || outputBytes > MaximumOutputBytes || nextInputBytes + outputBytes > MaximumWorkingBytes)
        {
            return new ScrollFrameCapture(ScrollFrameOutcome.Rejected,
                "The long screenshot memory limit was reached.");
        }

        _frames.Add(crop);
        _inputBytes = nextInputBytes;
        _outputHeight = nextHeight;
        return new ScrollFrameCapture(ScrollFrameOutcome.Added);
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

    public PixelBuffer[] Snapshot() => _frames.ToArray();

    public Task<StitchResult> StitchAsync(PixelBuffer[] snapshot) =>
        Task.Run(() => _stitcher.Stitch(snapshot, _cancellation.Token), _cancellation.Token);

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
