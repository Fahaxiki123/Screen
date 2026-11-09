namespace Screen.App;

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
