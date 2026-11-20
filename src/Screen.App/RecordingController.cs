using System.Diagnostics;
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
    private readonly CancellationTokenSource _finishCancellation = new();
    private string? _lastAudioWarning;

    internal RecordingController(
        IRecordingCapture recorder,
        PixelRect region,
        string filePath,
        string tempDir,
        RecordingAudioService? audio,
        RecordingBorderWindow? border,
        RecordingStopSignal stopSignal,
        bool showClickEffects = false,
        RecordingFormat format = RecordingFormat.Mp4)
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
            hasAudio: audio?.Tracks.Count > 0,
            levelProvider: audio is null ? null : () => audio.CurrentPeakLevel,
            format: format);
        _control.PauseToggled += (_, _) => TogglePause();
        _control.StopRequested += (_, _) => StopFromInput();

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
            var audioWarning = _audio?.Warning;
            if (audioWarning is not null && audioWarning != _lastAudioWarning)
            {
                _lastAudioWarning = audioWarning;
                HintToastWindow.Show(audioWarning);
            }
            _control.SetStatus(_recorder.Status ?? audioWarning);
            _control.UpdateTimer(_recorder.ElapsedSeconds);
        };
    }

    public string FilePath => _filePath;
    public RecordingProfile Profile { get; private set; } = new();
    public Task<RecordingSaveResult> Completion => _completion.Task;

    internal static RecordingProfile ResolveSessionProfile(RecordingProfile profile, RecordingSelection? selection)
    {
        if (selection is not null)
        {
            profile = profile with
            {
                Format = selection.Format,
                ShowCursor = selection.ShowCursor,
                ShowClickEffects = selection.ShowClickEffects,
                RecordSystemAudio = selection.RecordSystemAudio,
                RecordMicrophone = selection.RecordMicrophone
            };
        }
        if (profile.Format == RecordingFormat.Gif)
            profile = profile with { RecordSystemAudio = false, RecordMicrophone = false };
        return profile;
    }

    public static RecordingController Create(
        JsonSettingsStore settings,
        PixelRect region,
        RecordingSelection? selection,
        RecordingBorderWindow? persistentBorder)
    {
        var appSettings = settings.Load();
        var profile = ResolveSessionProfile(appSettings.Recording, selection);
        // Encoders need even dimensions; the crop keeps the top-left.
        var target = region with { Width = region.Width & ~1, Height = region.Height & ~1 };
        if (target.Width < 64 || target.Height < 64)
        {
            throw new InvalidOperationException("录制区域太小。");
        }
        FfmpegLocator.Resolve();
        Directory.CreateDirectory(appSettings.SaveDirectory);
        var filePath = Path.Combine(appSettings.SaveDirectory, $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var tempDir = RecordingStorage.CreateSession(profile);

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
            }
            return new RecordingController(recorder, target, filePath, tempDir, audio, persistentBorder, stopSignal,
                profile.ShowClickEffects, profile.Format)
            { Profile = profile };
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
        _hook.EscapePressed += (_, _) => _control.Dispatcher.BeginInvoke(() => StopFromInput());
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

    internal Task<RecordingSaveResult> StopAsync()
    {
        _control.Dispatcher.VerifyAccess();
        if (_finishTask is null)
        {
            _ = CompleteAsync(BeginFinish());
        }
        return Completion;
    }

    private void StopFromInput()
    {
        if (_disposed)
        { return; }
        if (_finishTask is null)
        {
            _ = StopAsync();
            return;
        }
        _control.SetStatus("正在取消保存，保留恢复素材…", finishing: true);
        _finishCancellation.Cancel();
        _recorder.CancelFinalization();
    }

    private async Task CompleteAsync(Task<RecordingSaveResult> finishTask) =>
        Complete(await finishTask);

    private Task<RecordingSaveResult> BeginFinish()
    {
        if (_finishTask is not null)
        {
            return _finishTask;
        }
        _timer.Stop();
        _control.SetStatus("正在保存录屏…", finishing: true);
        // Both capture paths observe the same stop boundary immediately. In
        // particular, audio must not keep recording while ffmpeg finalizes.
        _stopSignal.Request();
        _finishTask = Task.Run(() =>
        {
            try
            {
                _recorder.RequestStop();
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
                    return PublishSilentVideo(warning, preserveRecovery: true);
                }
                return MuxAudioAndFinish(CombineWarnings(interruptionWarning, _audio?.Warning));
            }
            catch (Exception exception)
            {
                SimpleLogger.LogError("Recording could not be saved; recovery materials retained", exception);
                RecordingTempCleaner.PreserveRecovery(_tempDir);
                return new RecordingSaveResult(false, null, exception.Message, _tempDir);
            }
        });
        return _finishTask;
    }

    private static string? CombineWarnings(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first)
            ? second
            : string.IsNullOrWhiteSpace(second) || string.Equals(first, second, StringComparison.Ordinal)
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
            return PublishSilentVideo(interruptionWarning);
        }
        var muxPath = Path.Combine(_tempDir, "muxed.mp4");
        try
        {
            var startInfo = BuildMuxStartInfo(
                FfmpegLocator.Resolve(),
                _videoTempPath,
                tracks,
                _recorder.EncodedDurationSeconds,
                muxPath);

            var inputBytes = new FileInfo(_videoTempPath).Length + tracks.Sum(track => track.BytesWritten);
            RecordingProcessRunner.Run(startInfo, RecordingProcessRunner.FinalizationTimeout(inputBytes), _finishCancellation.Token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            SimpleLogger.LogError($"Recording audio mux failed; recovery directory: {_tempDir}", exception);
            return PublishSilentVideo(CombineWarnings(interruptionWarning,
                $"声音处理失败，已保存无声视频（{exception.Message}）"), preserveRecovery: true);
        }
        // A failed mux may fall back to the intact silent video. A failed file
        // commit may not claim success or remove either recoverable source.
        return RecordingFileStore.Save(muxPath, _filePath, _tempDir,
            CombineWarnings(interruptionWarning, _audio?.Warning),
            preserveRecovery: _audio?.Warning is not null, cancellationToken: _finishCancellation.Token);
    }

    private RecordingSaveResult PublishSilentVideo(string? warning, bool preserveRecovery = false)
    {
        var source = _videoTempPath;
        if (_recorder.NeedsFinalFastStart)
        {
            var finalPath = Path.Combine(_tempDir, "final-video.mp4");
            try
            {
                var startInfo = BuildMuxStartInfo(FfmpegLocator.Resolve(), _videoTempPath, [],
                    _recorder.EncodedDurationSeconds, finalPath);
                RecordingProcessRunner.Run(startInfo,
                    RecordingProcessRunner.FinalizationTimeout(new FileInfo(_videoTempPath).Length), _finishCancellation.Token);
                source = finalPath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                // The encoded MP4 is already complete. Failure to move its
                // index must not discard a playable, recoverable recording.
                warning = CombineWarnings(warning, $"已保存视频，快速播放优化未完成（{exception.Message}）");
                preserveRecovery = true;
            }
        }
        return RecordingFileStore.Save(source, _filePath, _tempDir, warning,
            preserveRecovery: preserveRecovery || _audio?.Warning is not null, cancellationToken: _finishCancellation.Token);
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
        if (tracks.Count == 0)
        {
            Add("-an");
        }
        else if (tracks.Count == 1)
        {
            Add("-map", "1:a", "-af", "apad");
        }
        else
        {
            Add("-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:normalize=0,apad[mixed]", "-map", "[mixed]");
        }
        Add("-c:v", "copy");
        if (tracks.Count > 0)
        { Add("-ar", "48000", "-c:a", "aac", "-b:a", "192k"); }
        // Pad a short audio stream, or trim a final callback, to the shared
        // unpaused capture duration. Never shorten the video to audio.
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
        _control.Dispatcher.VerifyAccess();
        if (_disposed)
        {
            return;
        }
        // The tray awaits StopAsync while the dispatcher is running. If WPF
        // is already shutting down, wait only for the worker, never its UI continuation.
        Complete(BeginFinish().GetAwaiter().GetResult());
    }

    private void Complete(RecordingSaveResult result)
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _mouseHook?.Dispose();
            _hook?.Dispose();
            _audio?.Dispose();
            _recorder.Dispose();
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Recording resource cleanup failed after finalization", exception);
        }
        try
        {
            _control.Close();
            _border?.Close();
        }
        catch (InvalidOperationException exception)
        {
            SimpleLogger.LogError("Recording windows could not be closed after finalization", exception);
        }
        finally
        {
            _finishCancellation.Dispose();
            _completion.TrySetResult(result);
        }
    }
}
