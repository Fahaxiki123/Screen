using System.Diagnostics;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Shared monotonic unpaused clock for video and audio capture. Remains at
/// zero until the video capture loop actually begins accepting frames, so
/// device/encoder warm-up never skews A/V start alignment.
/// </summary>
internal sealed class RecordingTimeline(Func<long>? timestamp = null)
{
    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    private readonly object _gate = new();
    private PauseClock? _clock;
    private bool _pausedBeforeStart;

    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _clock is not null;
            }
        }
    }

    public double ElapsedSeconds
    {
        get
        {
            lock (_gate)
            {
                return _clock?.ElapsedSeconds ?? 0;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_clock is not null)
            {
                return;
            }
            _clock = new PauseClock(_timestamp);
            if (_pausedBeforeStart)
            {
                _clock.Pause();
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _pausedBeforeStart = true;
            _clock?.Pause();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _pausedBeforeStart = false;
            _clock?.Resume();
        }
    }
}

internal sealed class RecordingStopSignal
{
    private int _requested;
    public bool IsRequested => Volatile.Read(ref _requested) != 0;
    public void Request() => Interlocked.Exchange(ref _requested, 1);
}

internal static class RecordingStopCoordinator
{
    public static Exception? FinalizeCapture(Action stopAudio, Action finalizeVideo)
    {
        Exception? audioError = null;
        try
        {
            stopAudio();
        }
        catch (Exception exception)
        {
            audioError = exception;
        }
        // Audio is optional. Its disk/device failures must never prevent the
        // video writer from emitting its MP4 index and closing the container.
        // A video failure still propagates, so no caller can claim it was saved.
        finalizeVideo();
        return audioError;
    }

    public static void JoinAndFinalize(Task captureLoop, Action abortEncoder, Action finalizeEncoder, TimeSpan joinTimeout, TimeSpan abortTimeout)
    {
        if (!WaitForCompletion(captureLoop, joinTimeout))
        {
            abortEncoder();
            var joined = WaitForCompletion(captureLoop, abortTimeout);
            throw new TimeoutException(joined
                ? "录屏写入超时，已终止编码器；录制素材已保留。"
                : "录屏写入线程未能退出，未执行文件收尾；录制素材已保留。");
        }
        finalizeEncoder();
    }

    internal static bool WaitForCompletion(Task task, TimeSpan timeout)
    {
        try
        {
            return task.Wait(timeout);
        }
        catch (AggregateException)
        {
            // A cancelled/faulted task has also relinquished its stream. The
            // recorder reports capture faults separately from container errors.
            return task.IsCompleted;
        }
    }
}
