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
    private readonly ScreenRecorder _recorder;
    private readonly RecordingControlWindow _control;
    private readonly RecordingBorderWindow? _border;
    private readonly LowLevelKeyboardHook _hook;
    private readonly LowLevelMouseHook? _mouseHook;
    private readonly RecordingAudioService? _audio;
    private readonly DispatcherTimer _timer;
    private readonly PixelRect _region;
    private readonly string _filePath;
    private readonly string _tempDir;
    private readonly string _videoTempPath;
    private readonly RecordingStopSignal _stopSignal;
    private Task<RecordingSaveResult>? _finishTask;
    private bool _finished;
    private bool _disposed;
    private bool _uiClosed;

    public event EventHandler? Completed;
    public event EventHandler<string>? Failed;

    private RecordingController(
        ScreenRecorder recorder,
        PixelRect region,
        string filePath,
        string tempDir,
        string videoTempPath,
        RecordingAudioService? audio,
        RecordingBorderWindow? border,
        bool showClickEffects,
        RecordingStopSignal stopSignal)
    {
        _recorder = recorder;
        _region = region;
        _filePath = filePath;
        _tempDir = tempDir;
        _videoTempPath = videoTempPath;
        _audio = audio;
        _stopSignal = stopSignal;

        _control = new RecordingControlWindow(
            hasAudio: audio is not null,
            levelProvider: audio is null ? null : () => audio.CurrentPeakLevel);
        _control.PauseToggled += (_, _) => TogglePause();
        _control.StopRequested += (_, _) => _ = StopAsync();
        _control.Show();
        _control.Reposition(region);

        // The border is usually the persistent one created the moment the
        // user confirmed the region (before the overlay closed), so the
        // outline never blinks; fall back to creating it here for callers
        // that skipped that step.
        _border = border ?? new RecordingBorderWindow(region);
        _border.Show();

        // Esc stops recording from anywhere: the user is interacting with the
        // app being recorded, so the hook must not require our own foreground.
        _hook = new LowLevelKeyboardHook(0, requireForegroundWindow: false);
        _hook.EscapePressed += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _ = StopAsync());

        if (showClickEffects)
        {
            try
            {
                _mouseHook = new LowLevelMouseHook();
                _mouseHook.LeftButtonDown += (_, point) =>
                    Application.Current.Dispatcher.BeginInvoke(() => new ClickRippleWindow(point.X, point.Y).Show());
            }
            catch
            {
                // Hook failure must not sink the recording itself.
            }
        }

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
        _timer.Start();
    }

    public string FilePath => _filePath;

    public static async Task<RecordingController?> StartAsync(
        JsonSettingsStore settings,
        PixelRect? region,
        bool? showCursor = null,
        bool? showClickEffects = null,
        bool? recordSystemAudio = null,
        bool? recordMicrophone = null,
        RecordingBorderWindow? persistentBorder = null)
    {
        RecordingProfile profile;
        PixelRect target;
        string filePath;
        string tempDir;
        try
        {
            var appSettings = settings.Load();
            profile = appSettings.Recording;
            if (showCursor is { } cursor)
            {
                profile = profile with { ShowCursor = cursor };
            }
            if (showClickEffects is { } clickEffects)
            {
                profile = profile with { ShowClickEffects = clickEffects };
            }
            if (recordSystemAudio is { } systemAudio)
            {
                profile = profile with { RecordSystemAudio = systemAudio };
            }
            if (recordMicrophone is { } microphone)
            {
                profile = profile with { RecordMicrophone = microphone };
            }
            target = region ?? PrimaryMonitorBounds();
            // Encoders need even dimensions; the crop keeps the top-left.
            target = new PixelRect(target.X, target.Y, target.Width & ~1, target.Height & ~1);
            if (target.Width < 64 || target.Height < 64)
            {
                throw new InvalidOperationException("录制区域太小。");
            }
            FfmpegLocator.Resolve();
            Directory.CreateDirectory(appSettings.SaveDirectory);
            filePath = Path.Combine(
                appSettings.SaveDirectory,
                $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            // Scratch space for the video-only intermediate and raw audio
            // tracks; muxed into the final file at stop, then deleted.
            tempDir = Path.Combine(
                Path.GetTempPath(),
                "Screen.Recording",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法开始录屏：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        ScreenRecorder recorder;
        var stopSignal = new RecordingStopSignal();
        try
        {
            // Video goes to the scratch file first; audio is muxed in after
            // both streams are complete (see StopAsync).
            recorder = new ScreenRecorder(Path.Combine(tempDir, "video.mp4"), target, profile, stopSignal);
        }
        catch (Exception exception)
        {
            DeleteQuietly(tempDir);
            MessageBox.Show($"无法开始录屏：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        RecordingAudioService? audio = null;
        if (profile.RecordSystemAudio || profile.RecordMicrophone)
        {
            audio = RecordingAudioService.Start(tempDir, profile.RecordSystemAudio, profile.RecordMicrophone, stopSignal);
            if (audio.Tracks.Count == 0)
            {
                audio.Dispose();
                audio = null;
            }
        }

        // The persistent border already covers the region; show the control
        // bar BEFORE the early-fault wait so the takeover from the countdown
        // phase is seamless.
        var controller = new RecordingController(
            recorder,
            target,
            filePath,
            tempDir,
            Path.Combine(tempDir, "video.mp4"),
            audio,
            persistentBorder,
            profile.ShowClickEffects,
            stopSignal);

        // Give the capture session a moment to fail loudly (HDR output,
        // duplicated desktop, missing ffmpeg input).
        await Task.Delay(500);
        if (recorder.Fault is { } earlyFault)
        {
            controller.AbortStartup();
            MessageBox.Show($"无法开始录屏：{earlyFault.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        return controller;
    }

    /// <summary>
    /// Early-fault path: tear the UI down, discard scratch data, and release
    /// everything without producing a file.
    /// </summary>
    public void AbortStartup()
    {
        _finished = true; // keeps StopForShutdown inside Dispose from finalizing
        _stopSignal.Request();
        _timer.Stop();
        TeardownUi();
        _mouseHook?.Dispose();
        _hook.Dispose();
        _audio?.Dispose();
        _recorder.Dispose();
        if (!_recorder.IsRunning)
        {
            DeleteQuietly(_tempDir);
        }
    }

    private static PixelRect PrimaryMonitorBounds()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                     ?? throw new InvalidOperationException("未找到主显示器。");
        return new PixelRect(screen.X, screen.Y, screen.Width, screen.Height);
    }

    private void TogglePause()
    {
        if (_finished)
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

    private async Task StopAsync()
    {
        if (_finished)
        {
            return;
        }
        try
        {
            var result = await BeginFinish();
            if (_disposed)
            {
                return;
            }
            TeardownUi();
            if (!result.Saved)
            {
                Failed?.Invoke(this, $"{result.Error}\n录制素材已保留，可从以下位置恢复：\n{result.RecoveryDirectory}");
                return;
            }
            if (result.Warning is not null)
            {
                HintToastWindow.Show(result.RecoveryDirectory is null
                    ? "声音处理失败，已保存无声视频"
                    : $"声音处理失败，已保存无声视频。原始素材保留在：{result.RecoveryDirectory}");
            }
            Completed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            // Dispose NO MATTER WHAT — the mouse hook's native callback must
            // be unhooked while its delegate is still rooted; a leaked hook
            // whose delegate gets GC'd crashes the whole process (FailFast)
            // on the next mouse click.
            Dispose();
        }
    }

    private Task<RecordingSaveResult> BeginFinish()
    {
        if (_finishTask is not null)
        {
            return _finishTask;
        }
        _finished = true;
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
                if (audioError is not null)
                {
                    SimpleLogger.LogError($"Recording audio finalization failed; recovery directory: {_tempDir}", audioError);
                    return RecordingFileStore.Save(_videoTempPath, _filePath, _tempDir,
                        audioError.Message, preserveRecovery: true);
                }
                return MuxAudioAndFinish();
            }
            catch (Exception exception)
            {
                SimpleLogger.LogError("Recording could not be saved; recovery materials retained", exception);
                return new RecordingSaveResult(false, null, exception.Message, _tempDir);
            }
        });
        return _finishTask;
    }

    /// <summary>
    /// Muxes the video-only scratch file with the raw audio tracks into the
    /// final MP4. Scratch files are consumed only after a successful file commit.
    /// </summary>
    private RecordingSaveResult MuxAudioAndFinish()
    {
        var tracks = _audio?.Tracks.Where(track => track.BytesWritten > 0).ToList() ?? [];
        if (tracks.Count == 0)
        {
            return RecordingFileStore.Save(_videoTempPath, _filePath, _tempDir);
        }
        var muxPath = Path.Combine(_tempDir, "muxed.mp4");
        string? muxError = null;
        try
        {
            var startInfo = new ProcessStartInfo(FfmpegLocator.Resolve())
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
            Add("-i", _videoTempPath);
            foreach (var track in tracks)
            {
                Add("-f", track.FfmpegFormat, "-ar", track.SampleRate.ToString(), "-ac", track.Channels.ToString(), "-i", track.RawPath);
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
            Add("-t", _recorder.EncodedDurationSeconds.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture));
            Add(muxPath);

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
            SimpleLogger.LogError("Recording audio mux failed; attempting video-only save", exception);
        }
        // A failed mux may fall back to the intact silent video. A failed file
        // commit may not claim success or remove either recoverable source.
        return RecordingFileStore.Save(muxError is null ? muxPath : _videoTempPath, _filePath, _tempDir, muxError);
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

    /// <summary>
    /// Best-effort synchronous stop for process shutdown: finalize what has
    /// been encoded so far instead of leaking an orphaned ffmpeg process.
    /// </summary>
    public void StopForShutdown()
    {
        if (_disposed || (_finished && _finishTask is null))
        {
            return;
        }
        try
        {
            // The background finish task never waits for the dispatcher, so
            // shutdown can join it without racing disposal against mux/encoding.
            var result = BeginFinish().GetAwaiter().GetResult();
            if (!result.Saved)
            {
                SimpleLogger.LogError($"Recording recovery directory: {result.RecoveryDirectory}", new IOException(result.Error));
            }
        }
        catch
        {
            // Shutting down; nothing useful left to report.
        }
        TeardownUi();
    }

    private void TeardownUi()
    {
        if (_uiClosed)
        {
            return;
        }
        _uiClosed = true;
        _control.Close();
        _border?.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        StopForShutdown();
        _disposed = true;
        _timer.Stop();
        _mouseHook?.Dispose();
        _hook.Dispose();
        _audio?.Dispose();
        _recorder.Dispose();
    }
}
