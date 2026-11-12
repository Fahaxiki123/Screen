using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Orchestrates one recording session: capture loop ownership, audio tracks,
/// control bar, region border, Esc-to-stop hook, optional click ripples, and
/// the stop ordering rule — the capture loop must be fully joined before the
/// encoder finalizes the container, and audio is muxed in only after both
/// streams are complete.
/// </summary>
internal sealed class RecordingController : IDisposable
{
    private readonly IRecordingCapture _recorder;
    private readonly RecordingControlWindow _control;
    private RecordingBorderWindow? _border;
    private LowLevelKeyboardHook? _hook;
    private LowLevelMouseHook? _mouseHook;
    private readonly RecordingAudioService? _audio;
    private readonly DispatcherTimer _timer;
    private readonly PixelRect _region;
    private readonly string _filePath;
    private readonly string _tempDir;
    private readonly string _videoTempPath;
    private readonly RecordingStopSignal _stopSignal;
    private readonly bool _showClickEffects;
    private readonly TaskCompletionSource<RecordingSaveResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<RecordingSaveResult>? _finishTask;
    private bool _disposed;

    internal RecordingController(
        IRecordingCapture recorder,
        PixelRect region,
        string filePath,
        string tempDir,
        RecordingAudioService? audio,
        RecordingBorderWindow? border,
        RecordingStopSignal stopSignal,
        bool showClickEffects = false)
    {
        _recorder = recorder;
        _region = region;
        _filePath = filePath;
        _tempDir = tempDir;
        _videoTempPath = Path.Combine(tempDir, "video.mp4");
        _audio = audio;
        _border = border;
        _stopSignal = stopSignal;
        _showClickEffects = showClickEffects;

        _control = new RecordingControlWindow(
            hasAudio: audio is not null,
            levelProvider: audio is null ? null : () => audio.CurrentPeakLevel);
        _control.PauseToggled += (_, _) => TogglePause();
        _control.StopRequested += (_, _) => _ = StopAsync();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            // Check for mid-recording faults (GPU device loss, encoder crash, etc.)
            if (_recorder.Fault is { } fault)
            {
                SimpleLogger.LogError("Recording fault detected, stopping", fault);
                _ = StopAsync();
                return;
            }
            _control.UpdateTimer(_recorder.ElapsedSeconds);
        };
    }

    public string FilePath => _filePath;
    public Task<RecordingSaveResult> Completion => _completion.Task;

    public static RecordingController Create(
        JsonSettingsStore settings,
        PixelRect region,
        RecordingSelection? selection,
        RecordingBorderWindow? persistentBorder)
    {
        var appSettings = settings.Load();
        var profile = appSettings.Recording;
        if (selection is not null)
        {
            profile = profile with
            {
                ShowCursor = selection.ShowCursor,
                ShowClickEffects = selection.ShowClickEffects,
                RecordSystemAudio = selection.RecordSystemAudio,
                RecordMicrophone = selection.RecordMicrophone
            };
        }
        // Encoders need even dimensions; the crop keeps the top-left.
        var target = region with { Width = region.Width & ~1, Height = region.Height & ~1 };
        if (target.Width < 64 || target.Height < 64)
        {
            throw new InvalidOperationException("录制区域太小。");
        }
        FfmpegLocator.Resolve();
        Directory.CreateDirectory(appSettings.SaveDirectory);
        var filePath = Path.Combine(appSettings.SaveDirectory, $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var tempDir = Path.Combine(Path.GetTempPath(), "Screen.Recording", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var stopSignal = new RecordingStopSignal();
        var timeline = new RecordingTimeline();
        ScreenRecorder? recorder = null;
        RecordingAudioService? audio = null;
        try
        {
            recorder = new ScreenRecorder(Path.Combine(tempDir, "video.mp4"), target, profile, stopSignal, timeline);
            if (profile.RecordSystemAudio || profile.RecordMicrophone)
            {
                audio = RecordingAudioService.Start(
                    tempDir,
                    profile.RecordSystemAudio,
                    profile.RecordMicrophone,
                    stopSignal,
                    timeline);
                if (audio.Tracks.Count == 0)
                {
                    audio.Dispose();
                    audio = null;
                }
            }
            return new RecordingController(recorder, target, filePath, tempDir, audio, persistentBorder, stopSignal,
                profile.ShowClickEffects);
        }
        catch
        {
            audio?.Dispose();
            recorder?.Dispose();
            if (recorder?.IsRunning != true)
            {
                DeleteQuietly(tempDir);
            }
            throw;
        }
    }

    public void ShowControls()
    {
        // The host owns this controller before any stop input can arrive.
        // Keep the selection border visible through the countdown and handoff.
        _hook = new LowLevelKeyboardHook(0, requireForegroundWindow: false);
        _hook.EscapePressed += (_, _) => _control.Dispatcher.BeginInvoke(() => _ = StopAsync());
        if (_showClickEffects)
        {
            try
            {
                _mouseHook = new LowLevelMouseHook();
                _mouseHook.LeftButtonDown += (_, point) =>
                    _control.Dispatcher.BeginInvoke(() => new ClickRippleWindow(point.X, point.Y).Show());
            }
            catch
            {
                // Click effects are optional.
            }
        }
        _border ??= new RecordingBorderWindow(_region);
        _border.Show();
        _control.Show();
        _control.Reposition(_region);
        _timer.Start();
    }

    private void TogglePause()
    {
        if (_finishTask is not null)
        {
            return;
        }
        if (_recorder.IsPaused)
        {
            _recorder.Resume();
        }
        else
        {
            _recorder.Pause();
        }
        // Audio tracks drop samples while paused, mirroring the video loop so
        // both timelines stay aligned through pause/resume cycles.
        if (_audio is { } audio)
        {
            audio.Paused = _recorder.IsPaused;
        }
        _control.SetPaused(_recorder.IsPaused);
    }

    internal async Task StopAsync()
    {
        if (_finishTask is not null)
        {
            return;
        }
        try
        {
            await BeginFinish();
        }
        finally
        {
            Dispose();
        }
    }

    private Task<RecordingSaveResult> BeginFinish()
    {
        if (_finishTask is not null)
        {
            return _finishTask;
        }
        _timer.Stop();
        // Both capture paths observe the same stop boundary immediately. In
        // particular, audio must not keep recording while ffmpeg finalizes.
        _stopSignal.Request();
        _recorder.RequestStop();
        _finishTask = Task.Run(() =>
        {
            try
            {
                var audioError = RecordingStopCoordinator.FinalizeCapture(
                    () => _audio?.Stop(), _recorder.StopAndFinalize);
                var interruptionWarning = _recorder.Fault is { } fault
                    ? $"录屏因异常提前中断（{fault.Message}），已保存中断前内容"
                    : null;
                if (audioError is not null)
                {
                    SimpleLogger.LogError($"Recording audio finalization failed; recovery directory: {_tempDir}", audioError);
                    var warning = CombineWarnings(
                        interruptionWarning,
                        $"声音处理失败，已保存无声视频（{audioError.Message}）");
                    return RecordingFileStore.Save(_videoTempPath, _filePath, _tempDir,
                        warning, preserveRecovery: true);
                }
                return MuxAudioAndFinish(interruptionWarning);
            }
            catch (Exception exception)
            {
                SimpleLogger.LogError("Recording could not be saved; recovery materials retained", exception);
                return new RecordingSaveResult(false, null, exception.Message, _tempDir);
            }
        });
        return _finishTask;
    }

    private static string? CombineWarnings(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first)
            ? second
            : string.IsNullOrWhiteSpace(second)
                ? first
                : $"{first}；{second}";

    /// <summary>
    /// Muxes the video-only scratch file with the raw audio tracks into the
    /// final MP4. Scratch files are consumed only after a successful file commit.
    /// </summary>
    private RecordingSaveResult MuxAudioAndFinish(string? interruptionWarning = null)
    {
        var tracks = _audio?.Tracks.Where(track => track.BytesWritten > 0).ToList() ?? [];
        if (tracks.Count == 0)
        {
            return RecordingFileStore.Save(_videoTempPath, _filePath, _tempDir, warning: interruptionWarning);
        }
        var muxPath = Path.Combine(_tempDir, "muxed.mp4");
        string? muxError = null;
        try
        {
            var startInfo = BuildMuxStartInfo(
                FfmpegLocator.Resolve(),
                _videoTempPath,
                tracks,
                _recorder.EncodedDurationSeconds,
                muxPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("无法启动 ffmpeg。");
            }
            // stderr must be drained asynchronously — a synchronous
            // ReadToEnd() blocks until the process exits, so the timeout
            // below could never fire on a hung mux.
            var stderrLog = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                {
                    lock (stderrLog)
                    {
                        stderrLog.AppendLine(line);
                    }
                }
            };
            process.BeginErrorReadLine();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                process.WaitForExit(TimeSpan.FromSeconds(5));
                throw new TimeoutException("ffmpeg 混流超时（5 分钟）。");
            }
            // The process has exited; the parameterless overload returns at
            // once and only waits for the async stderr drain to finish.
            process.WaitForExit();
            string tail;
            lock (stderrLog)
            {
                tail = stderrLog.ToString();
            }
            if (process.ExitCode != 0)
            {
                tail = tail.Length > 300 ? tail[^300..] : tail;
                throw new InvalidOperationException($"ffmpeg 退出码 {process.ExitCode}：{tail.Trim()}");
            }
        }
        catch (Exception exception)
        {
            muxError = exception.Message;
            SimpleLogger.LogError($"Recording audio mux failed; recovery directory: {_tempDir}", exception);
        }
        // A failed mux may fall back to the intact silent video. A failed file
        // commit may not claim success or remove either recoverable source.
        var warning = CombineWarnings(
            interruptionWarning,
            muxError is null ? null : $"声音处理失败，已保存无声视频（{muxError}）");
        return RecordingFileStore.Save(muxError is null ? muxPath : _videoTempPath,
            _filePath, _tempDir, warning, preserveRecovery: muxError is not null);
    }

    internal static ProcessStartInfo BuildMuxStartInfo(
        string ffmpegPath,
        string videoTempPath,
        IReadOnlyList<RecordingAudioTrack> tracks,
        double encodedDurationSeconds,
        string muxPath)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        void Add(params string[] arguments)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        Add("-hide_banner", "-loglevel", "error", "-y");
        Add("-i", videoTempPath);
        foreach (var track in tracks)
        {
            Add(
                "-f",
                track.FfmpegFormat,
                "-ar",
                track.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-ac",
                track.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-i",
                track.RawPath);
        }
        Add("-map", "0:v");
        if (tracks.Count == 1)
        {
            Add("-map", "1:a", "-af", "apad");
        }
        else
        {
            Add("-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:normalize=0,apad[mixed]", "-map", "[mixed]");
        }
        Add("-c:v", "copy", "-ar", "48000", "-c:a", "aac", "-b:a", "192k");
        // Pad a short audio stream, or trim a final callback, to the exact
        // number of accepted video frames. Never shorten the video to audio.
        Add("-t", encodedDurationSeconds.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture));
        Add("-movflags", "+faststart");
        Add(muxPath);
        return startInfo;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Scratch cleanup must never break the stop path.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        RecordingSaveResult result;
        try
        {
            var finishTask = BeginFinish();
            if (finishTask.IsCompleted || finishTask.Wait(TimeSpan.FromSeconds(5)))
            {
                result = finishTask.GetAwaiter().GetResult();
            }
            else
            {
                SimpleLogger.LogWarning($"Recording finalization exceeded synchronous teardown budget; recovery directory: {_tempDir}");
                result = new RecordingSaveResult(false, null, "程序退出时录屏仍在后台收尾，原始素材已保留。", _tempDir);
            }
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError($"Recording recovery directory: {_tempDir}", exception);
            result = new RecordingSaveResult(false, null, exception.Message, _tempDir);
        }
        try
        {
            _timer.Stop();
            _mouseHook?.Dispose();
            _hook?.Dispose();
            _audio?.Dispose();
            _recorder.Dispose();
            _control.Close();
            _border?.Close();
        }
        finally
        {
            _completion.TrySetResult(result);
        }
    }
}
