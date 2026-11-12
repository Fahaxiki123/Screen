using System.Diagnostics;

namespace Screen.Core;

/// <summary>
/// Monotonic recording timeline that excludes paused intervals.  Frame and
/// audio timestamps captured on the raw clock are mapped onto this timeline
/// so a paused segment leaves no gap (and no frozen footage) in the output
/// media.  The clock accepts an injectable timestamp source for unit tests.
/// </summary>
public sealed class PauseClock
{
    private readonly object _gate = new();
    private readonly Func<long> _timestamp;
    private readonly long _zeroTicks;
    private long _pauseOffsetTicks;
    private long _pauseStartTicks;
    private long _lastEmittedTicks;
    private bool _paused;

    public PauseClock()
        : this(Stopwatch.GetTimestamp)
    {
    }

    public PauseClock(Func<long> timestamp)
    {
        _timestamp = timestamp;
        _zeroTicks = timestamp();
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>Recorded duration so far, excluding paused time.</summary>
    public double ElapsedSeconds => ToRecordingSeconds(_timestamp());

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }
            _paused = true;
            _pauseStartTicks = _timestamp();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused)
            {
                return;
            }
            _pauseOffsetTicks += _timestamp() - _pauseStartTicks;
            _paused = false;
        }
    }

    /// <summary>
    /// Maps a raw clock timestamp onto the recording timeline.  Timestamps
    /// captured while paused collapse onto the pause instant, and out-of-order
    /// arrivals are clamped so the emitted sequence stays monotonic — media
    /// containers reject backwards timestamps.
    /// </summary>
    public double ToRecordingSeconds(long rawTicks)
    {
        lock (_gate)
        {
            var effective = _paused ? Math.Min(rawTicks, _pauseStartTicks) : rawTicks;
            var ticks = effective - _zeroTicks - _pauseOffsetTicks;
            if (ticks < 0)
            {
                ticks = 0;
            }
            if (ticks < _lastEmittedTicks)
            {
                ticks = _lastEmittedTicks;
            }
            _lastEmittedTicks = ticks;
            return ticks / (double)Stopwatch.Frequency;
        }
    }
}
