using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Lucide.NET;
using Microsoft.Win32;
using NAudio.Wave;

namespace Screen.App;

/// <summary>
/// Post-recording editor (Feishu 编辑录屏): playback with timeline, save-as
/// (下载) and copy-file-to-clipboard (复制到剪贴板).  Playback is driven by
/// the bundled ffmpeg decoding raw frames into a WriteableBitmap — WPF's
/// MediaElement relies on legacy Windows media components that are absent or
/// broken on recent Windows builds (silent black screen, no events).
/// Audio is decoded separately and tied to the same playback generation.
/// </summary>
internal sealed class RecordingPreviewWindow : Window
{
    private readonly string _filePath;
    private readonly Image _videoImage;
    private readonly TimelineBar _timeline;
    private readonly TextBlock _timeText;
    private readonly TextBlock _statusText;
    private readonly LucideIconElement _playIcon;
    private readonly DispatcherTimer _ticker;
    private bool _scrubPreviewRunning;
    private double? _pendingScrubPreviewSeconds;

    private WriteableBitmap? _bitmap;
    private double _durationSeconds;
    private int _videoWidth;
    private int _videoHeight;
    private double _fps = 30;
    private double _positionSeconds;
    private bool _playing;
    private bool _scrubbing;
    private bool _tickerUpdating;
    private bool _loaded;

    private CancellationTokenSource? _decodeCts;
    private Process? _decoderProcess;
    private Task? _decodeTask;
    private int _decodeGeneration;

    // Compact editor chrome: navy caption bar and moon-blue control strip.
    private const double TitleBarHeight = 44;
    private readonly Border _videoHost;
    private LucideIconElement _volumeIcon = null!;
    private TextBlock _speedLabel = null!;
    private Popup _volumePopup = null!;
    private Popup _speedPopup = null!;
    private double _volume = 1.0;
    private bool _muted;
    private double _speed = 1.0;

    // Audio output: ffmpeg decodes the soundtrack to raw PCM, NAudio plays
    // it.  Start/stop is tied to the video decoder generation so pause and
    // seek keep both streams together.
    private Process? _audioProcess;
    private WaveOut? _waveOut;
    private BufferedWaveProvider? _audioBuffer;
    private readonly RecordingExportCache _exportCache = new();
    private bool _closed;

    /// <summary>Diagnostics for the preview audio path (append-only, tiny).</summary>
    internal static void AudioDebug(string message)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Screen.Recording");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "preview-audio.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public RecordingPreviewWindow(string filePath)
    {
        _filePath = filePath;
        Title = "编辑录屏";
        Width = Math.Min(1040, SystemParameters.WorkArea.Width * 0.8);
        Height = Math.Min(700, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = RecordingTheme.Brush("Brush.WindowBackdrop", "#15223A");
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        UseLayoutRounding = true;

        // Borderless navy chrome: a calm caption bar, full-bleed
        // video, bottom control strip.  WindowChrome keeps native resize
        // borders, caption drag and double-click maximize.
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = new Thickness(5),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            UseAeroCaptionButtons = false
        });

        _videoImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _videoHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x06, 0x0B, 0x15)),
            Child = _videoImage
        };
        _videoHost.MouseLeftButtonDown += (_, e) =>
        {
            if (_loaded)
            {
                TogglePlayback();
                e.Handled = true;
            }
        };

        // --- Caption bar ------------------------------------------------
        var captionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        captionButtons.Children.Add(MakeCaptionButton(LucideIconKind.Minus, "最小化",
            () => WindowState = WindowState.Minimized));
        captionButtons.Children.Add(MakeCaptionButton(LucideIconKind.Square, "最大化 / 还原",
            ToggleMaximize));
        captionButtons.Children.Add(MakeCaptionButton(LucideIconKind.X, "关闭",
            Close, close: true));
        var titleBar = new Grid
        {
            Background = RecordingTheme.Brush("Brush.ChromeGradient", "#1B3049"),
            MinHeight = TitleBarHeight
        };
        titleBar.Children.Add(new TextBlock
        {
            Text = "编辑录屏",
            FontSize = 16,
            FontWeight = FontWeights.Medium,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });
        captionButtons.HorizontalAlignment = HorizontalAlignment.Right;
        titleBar.Children.Add(captionButtons);

        // --- Bottom control strip (Feishu layout) ------------------------
        _playIcon = new LucideIconElement
        {
            Kind = LucideIconKind.Play,
            Width = 18,
            Height = 18,
            StrokeThickness = 2,
            Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var playButton = MakeFlatButton(_playIcon, 36, 36, Brushes.Transparent, HoverBrush(),
            (_, _) => TogglePlayback());
        playButton.ToolTip = "播放 / 暂停";

        _timeText = new TextBlock
        {
            Text = "00:00:00 / 00:00:00",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Variable Text"),
            FontSize = 12.5,
            Foreground = RecordingTheme.Brush("Brush.TextSecondary", "#94AECE"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0)
        };
        _timeline = new TimelineBar();
        WindowChrome.SetIsHitTestVisibleInChrome(_timeline, true);
        _timeline.ToolTip = "拖动进度定位，拖动两侧浅蓝手柄裁剪录屏";
        _timeline.ScrubStarted += (_, _) =>
        {
            _scrubbing = true;
            _pendingScrubPreviewSeconds = null;
        };
        _timeline.ScrubCompleted += (_, _) =>
        {
            _scrubbing = false;
            _pendingScrubPreviewSeconds = null;
            if (_loaded)
            {
                Seek(_timeline.Value);
            }
        };
        _timeline.ValueChanged += (_, _) =>
        {
            if (_tickerUpdating || !_loaded)
            {
                return;
            }
            if (_scrubbing)
            {
                _positionSeconds = _timeline.Value;
                UpdateTimeText();
                if (!_playing)
                {
                    QueuePausedScrubPreview(_timeline.Value);
                }
                return;
            }
            Seek(_timeline.Value);
        };
        _timeline.TrimChanged += (_, _) => OnTrimChanged();

        var volumeHost = BuildVolumeControls();
        volumeHost.VerticalAlignment = VerticalAlignment.Center;
        var speedButton = BuildSpeedButton();
        speedButton.VerticalAlignment = VerticalAlignment.Center;

        _statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = RecordingTheme.Brush("Brush.Success", "#83C8B3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        rightButtons.Children.Add(_statusText);
        rightButtons.Children.Add(volumeHost);
        rightButtons.Children.Add(speedButton);
        var separator = new Border
        {
            Width = 1,
            Height = 18,
            Background = RecordingTheme.Brush("Brush.Border", "#39556F"),
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        rightButtons.Children.Add(separator);
        var downloadButton = MakeTextButton("下载", SaveAs, primary: false);
        var copyButton = MakeTextButton("复制", CopyToClipboard, primary: true);
        copyButton.Margin = new Thickness(6, 0, 0, 0);
        rightButtons.Children.Add(downloadButton);
        rightButtons.Children.Add(copyButton);

        var bottomBar = new DockPanel
        {
            Height = 52,
            Margin = new Thickness(14, 0, 14, 0),
            LastChildFill = false
        };
        playButton.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(rightButtons, Dock.Right);
        bottomBar.Children.Add(rightButtons);
        bottomBar.Children.Add(playButton);
        bottomBar.Children.Add(_timeText);

        // The reference's trim band is 33 physical pixels at this desktop's
        // 125% scale: 26.4 DIP, edge to edge, with no extra border or padding.
        var progressHost = new Border
        {
            Height = 30,
            Margin = new Thickness(14, 8, 14, 0),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Background = RecordingTheme.Brush("Brush.SurfaceSunken", "#080F1D"),
            BorderBrush = RecordingTheme.Brush("Brush.Border", "#2694C5FF"),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Child = _timeline
        };
        DockPanel.SetDock(progressHost, Dock.Top);
        var buttonHost = new Border
        {
            Child = bottomBar
        };
        var bottomHost = new DockPanel
        {
            Background = RecordingTheme.Brush("Brush.ChromeGradient", "#1B3049"),
            LastChildFill = true
        };
        bottomHost.Children.Add(progressHost);
        bottomHost.Children.Add(buttonHost);

        var root = new DockPanel
        {
            LastChildFill = true,
            Background = RecordingTheme.Brush("Brush.WindowBackground", "#15223A")
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        DockPanel.SetDock(bottomHost, Dock.Bottom);
        root.Children.Add(bottomHost);
        root.Children.Add(_videoHost);
        var windowFrame = new Border
        {
            Background = RecordingTheme.Brush("Brush.WindowBackground", "#0A1222"),
            BorderBrush = RecordingTheme.Brush("Brush.GlassRim", "#4594C5FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            SnapsToDevicePixels = true,
            Child = root
        };
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            windowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(16);
            windowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        };
        Content = windowFrame;

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (_, _) =>
        {
            if (!_loaded || _scrubbing)
            {
                return;
            }
            if (_playing && _positionSeconds >= _timeline.TrimEnd - 0.05)
            {
                // Feishu-style: loop inside the trimmed section.
                Seek(Math.Max(0, _timeline.TrimStart));
                return;
            }
            _tickerUpdating = true;
            try
            {
                _timeline.Value = Math.Min(_timeline.TrimEnd, _positionSeconds);
            }
            finally
            {
                _tickerUpdating = false;
            }
            UpdateTimeText();
        };
        _ticker.Start();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                TogglePlayback();
                e.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _ticker.Stop();
            StopDecoder();
            _exportCache.Dispose();
        };
        Loaded += async (_, _) => await LoadMediaInfoAsync();
    }

    // ------------------------------------------------------------------
    // Media info probe (ffmpeg -i stderr parsing)
    // ------------------------------------------------------------------

    private async Task LoadMediaInfoAsync()
    {
        string stderr;
        try
        {
            var startInfo = new ProcessStartInfo(FfmpegLocator.Resolve())
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(_filePath);
            using var probe = Process.Start(startInfo)!;
            stderr = await probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync();
        }
        catch (Exception exception)
        {
            ShowPlaybackError($"无法读取录屏信息：{exception.Message}");
            return;
        }

        if (_closed)
        {
            return;
        }
        var durationMatch = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):([\d.]+)");
        var sizeMatch = Regex.Match(stderr, @"Video:.*?(\d{2,5})x(\d{2,5})");
        if (!durationMatch.Success || !sizeMatch.Success)
        {
            ShowPlaybackError("无法解析录屏文件（文件可能损坏）。");
            return;
        }
        var fpsMatch = Regex.Match(stderr, @"([\d.]+)\s*fps");

        _durationSeconds = int.Parse(durationMatch.Groups[1].Value) * 3600
                           + int.Parse(durationMatch.Groups[2].Value) * 60
                           + double.Parse(durationMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        _videoWidth = int.Parse(sizeMatch.Groups[1].Value);
        _videoHeight = int.Parse(sizeMatch.Groups[2].Value);
        if (fpsMatch.Success
            && double.TryParse(fpsMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedFps)
            && parsedFps > 0)
        {
            _fps = parsedFps;
        }
        _bitmap = new WriteableBitmap(_videoWidth, _videoHeight, 96, 96, PixelFormats.Bgra32, null);
        _videoImage.Source = _bitmap;

        _timeline.Maximum = Math.Max(0.1, _durationSeconds);
        _loaded = true;
        UpdateTimeText();
        // Feishu-style: playback starts immediately after recording ends.
        StartDecoderFrom(0);
    }

    // ------------------------------------------------------------------
    // Playback control
    // ------------------------------------------------------------------

    private void TogglePlayback()
    {
        if (!_loaded)
        {
            return;
        }
        if (_playing)
        {
            PausePlayback();
        }
        else
        {
            var start = _positionSeconds;
            if (start < _timeline.TrimStart - 0.05 || start >= _timeline.TrimEnd - 0.05)
            {
                start = Math.Max(0, _timeline.TrimStart);
            }
            StartDecoderFrom(start);
        }
    }

    private void PausePlayback()
    {
        _playing = false;
        SetPlayingVisual(false);
        StopDecoder();
    }

    private void Seek(double seconds)
    {
        if (!_loaded)
        {
            return;
        }
        var target = Math.Clamp(seconds, _timeline.TrimStart, Math.Max(_timeline.TrimStart + 0.01, _timeline.TrimEnd));
        _positionSeconds = target;
        UpdateTimeText();
        if (_playing)
        {
            StartDecoderFrom(target);
        }
        else
        {
            // Show the seeked frame while staying paused.
            StartDecoderFrom(target, autoPlay: false);
        }
    }

    private void QueuePausedScrubPreview(double seconds)
    {
        _pendingScrubPreviewSeconds = seconds;
        if (!_scrubPreviewRunning)
        {
            _ = RunPausedScrubPreviewAsync();
        }
    }

    private async Task RunPausedScrubPreviewAsync()
    {
        if (_scrubPreviewRunning)
        {
            return;
        }

        _scrubPreviewRunning = true;
        try
        {
            // Decode one requested frame at a time and coalesce mouse-move
            // events to the latest position. This keeps the picture updating
            // during a paused drag without spawning an ffmpeg process for
            // every pixel crossed by the pointer.
            while (_scrubbing && !_playing && _pendingScrubPreviewSeconds is double target)
            {
                _pendingScrubPreviewSeconds = null;
                StartDecoderFrom(target, autoPlay: false);
                var previewTask = _decodeTask;
                if (previewTask is not null)
                {
                    await previewTask;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _scrubPreviewRunning = false;
        }
    }

    private void StartDecoderFrom(double startSeconds, bool autoPlay = true)
    {
        StopDecoder();
        _playing = autoPlay;
        SetPlayingVisual(autoPlay);
        var generation = Interlocked.Increment(ref _decodeGeneration);
        var cts = new CancellationTokenSource();
        _decodeCts = cts;
        _decodeTask = Task.Run(() => DecodeLoopAsync(startSeconds, autoPlay, generation, cts.Token));
        if (autoPlay)
        {
            StartAudioFrom(startSeconds, generation, cts.Token);
        }
    }

    private void StopDecoder()
    {
        Interlocked.Increment(ref _decodeGeneration);
        _decodeCts?.Cancel();
        StopAudioInternal();
        try
        {
            if (_decoderProcess is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        _decodeCts?.Dispose();
        _decodeCts = null;
    }

    private void StartAudioFrom(double startSeconds, int generation, CancellationToken cancellationToken)
    {
        StopAudioInternal();
        try
        {
            var format = new WaveFormat(48000, 16, 2);
            _audioBuffer = new BufferedWaveProvider(format)
            {
                ReadFully = false
            };
            _waveOut = new WaveOut();
            _waveOut.Init(_audioBuffer);
            _waveOut.Volume = (float)(_muted ? 0 : _volume);
            _waveOut.Play();
            AudioDebug($"start from={startSeconds:F2}s gen={generation} waveOut={_waveOut.PlaybackState}");

            var startInfo = new ProcessStartInfo(FfmpegLocator.Resolve())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (startSeconds > 0.05)
            {
                startInfo.ArgumentList.Add("-ss");
                startInfo.ArgumentList.Add(startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error",
                "-i", _filePath
            };
            if (Math.Abs(_speed - 1.0) > 0.001)
            {
                // atempo keeps pitch while changing playback rate (0.5–2x).
                arguments.Add("-filter:a");
                arguments.Add("atempo=" + _speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            arguments.AddRange(new[]
            {
                "-vn", "-f", "s16le", "-ar", "48000", "-ac", "2",
                "pipe:1"
            });
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            _audioProcess = Process.Start(startInfo);
            if (_audioProcess is null)
            {
                AudioDebug("audio process failed to start");
                StopAudioInternal();
                return;
            }
            AudioDebug($"audio process pid={_audioProcess.Id}");
            _ = Task.Run(() => AudioPumpAsync(generation, cancellationToken));
        }
        catch (Exception exception)
        {
            // Silent preview is preferable to sinking the editor.
            AudioDebug($"StartAudioFrom exception: {exception}");
            StopAudioInternal();
        }
    }

    private async Task AudioPumpAsync(int generation, CancellationToken cancellationToken)
    {
        var process = _audioProcess;
        var buffer = _audioBuffer;
        if (process is null || buffer is null)
        {
            AudioDebug("pump: no process/buffer");
            return;
        }
        var stream = process.StandardOutput.BaseStream;
        var chunk = new byte[32768];
        var totalBytes = 0L;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && generation == Volatile.Read(ref _decodeGeneration))
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read <= 0)
                {
                    AudioDebug($"pump: EOF after {totalBytes} bytes, ffmpeg exit={SafeExitCode(process)}");
                    break;
                }
                totalBytes += read;
                if (totalBytes == read)
                {
                    AudioDebug($"pump: first chunk {read} bytes, waveOut={_waveOut?.PlaybackState}");
                }
                // WaveOut auto-stops when the provider runs dry (ReadFully=false):
                // data typically arrives ~60 ms after Play(), so re-arm it
                // whenever a chunk lands while it is stopped.
                EnsureWaveOutPlaying();
                // Backpressure: let WaveOut drain before feeding more.
                while (!cancellationToken.IsCancellationRequested
                       && generation == Volatile.Read(ref _decodeGeneration)
                       && buffer.BufferedDuration > TimeSpan.FromMilliseconds(400))
                {
                    EnsureWaveOutPlaying();
                    await Task.Delay(20, cancellationToken);
                }
                if (cancellationToken.IsCancellationRequested
                    || generation != Volatile.Read(ref _decodeGeneration))
                {
                    break;
                }
                buffer.AddSamples(chunk, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            AudioDebug($"pump: cancelled after {totalBytes} bytes");
        }
        catch (IOException exception)
        {
            AudioDebug($"pump: IOException after {totalBytes} bytes: {exception.Message}");
        }
    }

    private void EnsureWaveOutPlaying()
    {
        try
        {
            if (_waveOut is { PlaybackState: NAudio.Wave.PlaybackState.Stopped })
            {
                _waveOut.Play();
                AudioDebug("waveOut re-armed");
            }
        }
        catch (Exception exception)
        {
            AudioDebug($"re-arm failed: {exception.Message}");
        }
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString() : "running";
        }
        catch
        {
            return "unknown";
        }
    }

    private void StopAudioInternal()
    {
        try
        {
            if (_audioProcess is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        _audioProcess?.Dispose();
        _audioProcess = null;
        try
        {
            _waveOut?.Stop();
        }
        catch
        {
        }
        _waveOut?.Dispose();
        _waveOut = null;
        _audioBuffer?.ClearBuffer();
        _audioBuffer = null;
    }

    private async Task DecodeLoopAsync(double startSeconds, bool autoPlay, int generation, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            var startInfo = new ProcessStartInfo(FfmpegLocator.Resolve())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            if (startSeconds > 0.05)
            {
                startInfo.ArgumentList.Add("-ss");
                startInfo.ArgumentList.Add(startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(_filePath);
            if (!autoPlay)
            {
                // A paused scrub needs exactly one visual frame. Declaring
                // that up front lets ffmpeg finish promptly so the coalesced
                // preview loop can move on to the pointer's latest position.
                startInfo.ArgumentList.Add("-an");
                startInfo.ArgumentList.Add("-sn");
                startInfo.ArgumentList.Add("-dn");
                startInfo.ArgumentList.Add("-frames:v");
                startInfo.ArgumentList.Add("1");
            }
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("rawvideo");
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("bgra");
            startInfo.ArgumentList.Add("pipe:1");
            process = Process.Start(startInfo)!;
        }
        catch
        {
            return;
        }
        _decoderProcess = process;

        var frameBytes = _videoWidth * _videoHeight * 4;
        var buffer = new byte[frameBytes];
        var frameDuration = TimeSpan.FromSeconds(1 / _fps);
        var stopwatch = Stopwatch.StartNew();
        long frameIndex = 0;
        var ended = false;

        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await ReadExactAsync(stream, buffer, frameBytes, cancellationToken);
                if (read < frameBytes)
                {
                    ended = true;
                    break;
                }
                if (Interlocked.CompareExchange(ref _decodeGeneration, generation, generation) != generation
                    || generation != Volatile.Read(ref _decodeGeneration))
                {
                    break;
                }

                var capturedPosition = startSeconds + frameIndex / _fps;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_bitmap is null)
                    {
                        return;
                    }
                    _bitmap.WritePixels(new Int32Rect(0, 0, _videoWidth, _videoHeight), buffer, _videoWidth * 4, 0);
                    if (!_scrubbing)
                    {
                        _positionSeconds = capturedPosition;
                        _tickerUpdating = true;
                        try
                        {
                            _timeline.Value = Math.Min(_timeline.TrimEnd, capturedPosition);
                        }
                        finally
                        {
                            _tickerUpdating = false;
                        }
                    }
                    UpdateTimeText();
                });

                frameIndex++;
                if (autoPlay)
                {
                    var target = TimeSpan.FromTicks((long)(frameIndex * frameDuration.Ticks / _speed));
                    var wait = target - stopwatch.Elapsed;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, cancellationToken);
                    }
                }
                else
                {
                    // Paused seek: render the first frame only, then stop.
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            ended = true;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
            if (_decoderProcess == process)
            {
                _decoderProcess = null;
            }
        }

        if (ended && generation == Volatile.Read(ref _decodeGeneration) && autoPlay)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _playing = false;
                SetPlayingVisual(false);
                _positionSeconds = Math.Max(_timeline.TrimStart, Math.Min(_durationSeconds, _timeline.TrimEnd));
                UpdateTimeText();
            });
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    // ------------------------------------------------------------------
    // UI helpers
    // ------------------------------------------------------------------

    private void SetPlayingVisual(bool playing) =>
        _playIcon.Kind = playing ? LucideIconKind.Pause : LucideIconKind.Play;

    private void UpdateTimeText()
    {
        _timeText.Text = $"{FormatTime(_positionSeconds)} / {FormatTime(_durationSeconds)}";
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");

    // ------------------------------------------------------------------
    // Chrome + control strip builders (shared moon-blue theme tokens)
    // ------------------------------------------------------------------

    private static Brush HoverBrush() => RecordingTheme.Brush("Brush.SurfaceHover", "#304E69");

    /// <summary>
    /// Flat button with a code-built template (Border + ContentPresenter):
    /// no default chrome, corner radius 8, explicit hover background, so it
    /// reads correctly on the navy control strip.
    /// </summary>
    private static Button MakeFlatButton(
        FrameworkElement content,
        double width,
        double height,
        Brush background,
        Brush hoverBackground,
        RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = height,
            Background = background,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        chrome.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBackground, "Chrome"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.78, "Chrome"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35, "Chrome"));
        template.Triggers.Add(disabled);
        button.Template = template;
        button.Click += handler;
        return button;
    }

    private Button MakeCaptionButton(LucideIconKind kind, string tip, Action action, bool close = false)
    {
        var icon = new LucideIconElement
        {
            Kind = kind,
            Width = 15,
            Height = 15,
            StrokeThickness = 1.8,
            Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var danger = RecordingTheme.Brush("Brush.Danger", "#E9959B");
        var button = MakeFlatButton(icon, 44, 32, Brushes.Transparent,
            close ? danger : HoverBrush(), (_, _) => action());
        if (close)
        {
            button.MouseEnter += (_, _) => icon.Stroke = RecordingTheme.Brush("Brush.OnAccent", "#15223A");
            button.MouseLeave += (_, _) => icon.Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7");
        }
        button.ToolTip = tip;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private Button MakeTextButton(string text, RoutedEventHandler handler, bool primary)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = primary
                ? Brushes.White
                : RecordingTheme.Brush("Brush.TextPrimary", "#F4F8FF")
        };
        Brush background = primary
            ? RecordingTheme.Brush("Brush.PrimaryAction", "#0EA5E9")
            : RecordingTheme.Brush("Brush.SurfaceRaised", "#13223D");
        Brush hoverBackground = primary
            ? RecordingTheme.Brush("Brush.PrimaryActionHover", "#38BDF8")
            : HoverBrush();
        var button = MakeFlatButton(label, double.NaN, 32, background, hoverBackground, handler);
        button.MinWidth = 76;
        button.Padding = new Thickness(16, 0, 16, 0);
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = primary
            ? RecordingTheme.Brush("Brush.GlassRim", "#50FFFFFF")
            : RecordingTheme.Brush("Brush.Border", "#2694C5FF");
        return button;
    }

    /// <summary>
    /// Volume control in the Feishu layout: icon button (click = mute,
    /// hover = slider popup above it).
    /// </summary>
    private Grid BuildVolumeControls()
    {
        _volumeIcon = new LucideIconElement
        {
            Kind = LucideIconKind.Volume2,
            Width = 16,
            Height = 16,
            StrokeThickness = 1.8,
            Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var button = MakeFlatButton(_volumeIcon, 36, 36, Brushes.Transparent, HoverBrush(),
            (_, _) =>
            {
                _muted = !_muted;
                ApplyVolume();
            });
        System.Windows.Automation.AutomationProperties.SetName(button, "音量");
        var bar = new VolumeBar { Width = 96, Height = 20, Value = _volume };
        bar.ValueChanged += (_, _) =>
        {
            _volume = bar.Value;
            _muted = false;
            ApplyVolume();
        };
        var popupBorder = new Border
        {
            Background = RecordingTheme.Brush("Brush.SurfaceRaised", "#243D57"),
            BorderBrush = RecordingTheme.Brush("Brush.Border", "#39556F"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = bar
        };
        _volumePopup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = -42,
            VerticalOffset = 2,
            Child = popupBorder
        };
        var host = new Grid();
        host.Children.Add(button);
        var closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            if (!host.IsMouseOver && !popupBorder.IsMouseOver)
            {
                _volumePopup.IsOpen = false;
            }
        };
        void KeepOpen()
        {
            closeTimer.Stop();
            _volumePopup.IsOpen = true;
        }
        void ScheduleClose()
        {
            closeTimer.Stop();
            closeTimer.Start();
        }
        host.MouseEnter += (_, _) => KeepOpen();
        host.MouseLeave += (_, _) => ScheduleClose();
        popupBorder.MouseEnter += (_, _) => KeepOpen();
        popupBorder.MouseLeave += (_, _) => ScheduleClose();
        _volumePopup.Closed += (_, _) => closeTimer.Stop();
        return host;
    }

    private Button BuildSpeedButton()
    {
        _speedLabel = new TextBlock
        {
            Text = "1x",
            FontSize = 12,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var button = MakeFlatButton(_speedLabel, 38, 36, Brushes.Transparent, HoverBrush(),
            (_, _) => OpenSpeedMenu());
        button.ToolTip = "倍速";
        _speedPopup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = -62,
            VerticalOffset = -4
        };
        return button;
    }

    private void OpenSpeedMenu()
    {
        _speedPopup.Child = BuildSpeedMenu();
        _speedPopup.IsOpen = true;
    }

    private Border BuildSpeedMenu()
    {
        var panel = new StackPanel();
        foreach (var speed in new[] { 0.5, 1.0, 1.5, 2.0 })
        {
            var value = speed;
            var selected = Math.Abs(value - _speed) < 0.01;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var slot = new Grid { Width = 16 };
            if (selected)
            {
                slot.Children.Add(new LucideIconElement
                {
                    Kind = LucideIconKind.Check,
                    Width = 14,
                    Height = 14,
                    StrokeThickness = 2,
                    Stroke = RecordingTheme.Brush("Brush.AccentText", "#C5E5F0")
                });
            }
            row.Children.Add(slot);
            row.Children.Add(new TextBlock
            {
                Text = speed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x",
                FontSize = 12,
                Foreground = selected
                    ? RecordingTheme.Brush("Brush.AccentText", "#C5E5F0")
                    : RecordingTheme.Brush("Brush.TextSecondary", "#B5C9D9"),
                Margin = new Thickness(6, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            var item = MakeFlatButton(row, double.NaN, 30,
                selected ? RecordingTheme.Brush("Brush.AccentSubtle", "#294960") : Brushes.Transparent,
                HoverBrush(),
                (_, _) =>
                {
                    SetSpeed(value);
                    _speedPopup.IsOpen = false;
                });
            item.MinWidth = 100;
            item.Padding = new Thickness(10, 0, 10, 0);
            panel.Children.Add(item);
        }
        return new Border
        {
            Background = RecordingTheme.Brush("Brush.SurfaceRaised", "#243D57"),
            BorderBrush = RecordingTheme.Brush("Brush.Border", "#39556F"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Child = panel
        };
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;
        _speedLabel.Text = speed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x";
        if (_loaded)
        {
            if (_playing)
            {
                StartDecoderFrom(_positionSeconds);
            }
            else
            {
                StartDecoderFrom(_positionSeconds, autoPlay: false);
            }
        }
    }

    private void ApplyVolume()
    {
        try
        {
            if (_waveOut is not null)
            {
                _waveOut.Volume = (float)(_muted ? 0 : _volume);
            }
        }
        catch
        {
        }
        if (_volumeIcon is not null)
        {
            _volumeIcon.Kind = _muted || _volume <= 0.001
                ? LucideIconKind.VolumeX
                : LucideIconKind.Volume2;
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    // ------------------------------------------------------------------
    // Borderless maximize: clamp to the monitor work area (the taskbar
    // would otherwise be covered).
    // ------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            // Ask DWM to clip the custom chrome to real Windows 11 rounded
            // corners. The WPF border supplies the visible one-pixel outline.
            var darkMode = 1;
            _ = NativeMethods.DwmSetWindowAttribute(source.Handle, 20, ref darkMode, sizeof(int));
            var cornerPreference = 2; // DWMWCP_ROUND
            _ = NativeMethods.DwmSetWindowAttribute(source.Handle, 33, ref cornerPreference, sizeof(int));
            source.AddHook(WndProc);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ClampMaximizeToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ClampMaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var info = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }
        info.ptMaxPosition.X = monitorInfo.rcWork.Left - monitorInfo.rcMonitor.Left;
        info.ptMaxPosition.Y = monitorInfo.rcWork.Top - monitorInfo.rcMonitor.Top;
        info.ptMaxSize.X = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
        info.ptMaxSize.Y = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
        Marshal.StructureToPtr(info, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private async void SaveAs(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closed)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "保存录屏",
            Filter = "MP4 视频 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_filePath)
                       + (_timeline.IsTrimmed ? "-裁切" : string.Empty) + ".mp4"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }
        SetStatus(_timeline.IsTrimmed ? "正在裁切导出…" : "正在导出…");
        try
        {
            var source = await GetTrimmedOrOriginalAsync();
            if (_closed)
            {
                return;
            }
            RecordingFileStore.CopyAtomically(source, dialog.FileName);
            SetStatus("已保存");
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception exception)
        {
            if (_closed)
            {
                return;
            }
            SetStatus("保存失败");
            MessageBox.Show(this, $"保存失败：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (sender is Button restore)
            {
                restore.IsEnabled = true;
            }
        }
    }

    private async void CopyToClipboard(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closed)
        {
            return;
        }
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }
        try
        {
            if (_timeline.IsTrimmed)
            {
                SetStatus("正在裁切…");
            }
            var source = await GetTrimmedOrOriginalAsync();
            if (_closed)
            {
                return;
            }
            if (!string.Equals(source, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                source = RecordingFileStore.CreateClipboardCopy(source, _filePath);
            }
            Clipboard.SetFileDropList(new StringCollection { source });
            SetStatus("已复制，可粘贴到聊天或文件夹");
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception exception)
        {
            if (_closed)
            {
                return;
            }
            SetStatus("复制失败");
            MessageBox.Show(this, $"复制失败：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (sender is Button restore)
            {
                restore.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// Returns the original file when no trim is set, otherwise re-encodes the
    /// trimmed section once per immutable range. Concurrent requests share a
    /// task, and changing the selection never reassigns an older export result.
    /// </summary>
    private Task<string> GetTrimmedOrOriginalAsync()
    {
        if (!_timeline.IsTrimmed)
        {
            return Task.FromResult(_filePath);
        }
        var range = new RecordingTrimRange(_timeline.TrimStart, _timeline.TrimEnd);
        return _exportCache.GetAsync(range, ExportTrimAsync);
    }

    private async Task<string> ExportTrimAsync(RecordingTrimRange range, CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Screen.Recording");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"trim-{Guid.NewGuid():N}.mp4");
        var startInfo = new ProcessStartInfo(FfmpegLocator.Resolve())
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-i", _filePath,
                     "-ss", range.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-to", range.End.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                     "-c:a", "aac", "-b:a", "192k",
                     tempPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        using var cancellation = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        });
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var tail = stderr.Length > 200 ? stderr[^200..] : stderr;
                throw new InvalidOperationException(tail.Trim());
            }
            return tempPath;
        }
        catch
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                }
            }
            RecordingFileStore.DeleteFileQuietly(tempPath);
            throw;
        }
    }

    private void OnTrimChanged()
    {
        // Each range owns its cache entry. An export already underway keeps
        // the selection it started with, even while the handles are moved.
        if (!_playing && (_positionSeconds < _timeline.TrimStart || _positionSeconds > _timeline.TrimEnd))
        {
            _positionSeconds = _timeline.TrimStart;
            _timeline.Value = _positionSeconds;
            UpdateTimeText();
        }
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text;
        var clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        clear.Tick += (_, _) =>
        {
            _statusText.Text = string.Empty;
            clear.Stop();
        };
        clear.Start();
    }

    private void ShowPlaybackError(string message)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 14,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        });
        var openFolderButton = MakeTextButton("在资源管理器中显示", (_, _) =>
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{_filePath}\"");
            }
            catch
            {
            }
        }, primary: true);
        openFolderButton.Margin = new Thickness(0, 12, 0, 0);
        openFolderButton.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(openFolderButton);
        _videoHost.Child = panel;
    }

}

/// <summary>
/// Minimal Feishu-style seek bar drawn directly instead of templating a
/// Slider (code-built Slider templates crash: Track does not implement
/// IAddChild under FrameworkElementFactory).  Includes the trim bar: two
/// draggable end handles delimit the section that plays and gets exported.
/// </summary>
internal sealed class TimelineBar : FrameworkElement
{
    private const double MinTrimGap = 0.2;
    private const double HandleWidth = 11;
    private const double HandleHitPadding = 5;
    private const double PlayheadWidth = 2.4;

    private static readonly Brush SurfaceBrush = CreateFrozenBrush(Color.FromRgb(0x07, 0x0E, 0x1B));
    private static readonly Brush ExcludedDimBrush = CreateFrozenBrush(Color.FromArgb(0xA8, 0x03, 0x06, 0x0E));
    private static readonly Brush ActiveZoneBrush = CreateFrozenGradient(
        Color.FromArgb(0x2E, 0x38, 0xBD, 0xF8),
        Color.FromArgb(0x16, 0x0E, 0xA5, 0xE9),
        horizontal: true);
    private static readonly Pen TickPen = CreateFrozenPen(Color.FromArgb(0x16, 0x94, 0xC5, 0xFF), 1);
    private static readonly Pen ActiveEdgePen = CreateFrozenPen(Color.FromRgb(0x38, 0xBD, 0xF8), 1.6);
    private static readonly Brush AccentBrush = CreateFrozenGradient(
        Color.FromRgb(0x7D, 0xD3, 0xFC),
        Color.FromRgb(0x0E, 0xA5, 0xE9),
        horizontal: false);
    private static readonly Brush AccentHoverBrush = CreateFrozenGradient(
        Color.FromRgb(0xBA, 0xE6, 0xFD),
        Color.FromRgb(0x38, 0xBD, 0xF8),
        horizontal: false);
    private static readonly Brush GripBrush = CreateFrozenBrush(Color.FromRgb(0x06, 0x13, 0x26));
    private static readonly Brush PlayheadBrush = Brushes.White;

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateFrozenGradient(Color start, Color end, bool horizontal)
    {
        var brush = new LinearGradientBrush(
            start,
            end,
            new Point(0, 0),
            horizontal ? new Point(1, 0) : new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var pen = new Pen(CreateFrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private enum DragMode { None, Thumb, TrimStart, TrimEnd }

    private double _value;
    private double _maximum = 0.1;
    private double _trimStart;
    private double _trimEnd = 0.1;
    private DragMode _dragMode;
    private DragMode _hoverMode;
    private double _dragOffsetX;

    public event EventHandler? ValueChanged;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCompleted;
    public event EventHandler? TrimChanged;

    public TimelineBar() => Cursor = System.Windows.Input.Cursors.Hand;

    public double Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(0.001, value);
            _trimStart = 0;
            _trimEnd = _maximum;
            _value = Math.Clamp(_value, 0, _maximum);
            InvalidateVisual();
        }
    }

    public double TrimStart => _trimStart;
    public double TrimEnd => _trimEnd;
    public bool IsTrimmed => _trimStart > 0.01 || _trimEnd < _maximum - 0.01;

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, _trimStart, Math.Max(_trimStart, _trimEnd));
            if (Math.Abs(clamped - _value) < 0.0001)
            {
                return;
            }
            _value = clamped;
            InvalidateVisual();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetTrimStart(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, Math.Max(0, _trimEnd - MinTrimGap));
        if (Math.Abs(clamped - _trimStart) < 0.0001)
        {
            return;
        }
        _trimStart = clamped;
        if (_value < _trimStart)
        {
            _value = _trimStart;
        }
        InvalidateVisual();
        TrimChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetTrimEnd(double seconds)
    {
        var clamped = Math.Clamp(seconds, Math.Min(_maximum, _trimStart + MinTrimGap), _maximum);
        if (Math.Abs(clamped - _trimEnd) < 0.0001)
        {
            return;
        }
        _trimEnd = clamped;
        if (_value > _trimEnd)
        {
            _value = _trimEnd;
        }
        InvalidateVisual();
        TrimChanged?.Invoke(this, EventArgs.Empty);
    }

    private static double EffectiveHandleWidth(double width) => Math.Min(HandleWidth, width / 2);

    private double XFor(double seconds, double width)
    {
        var handleWidth = EffectiveHandleWidth(width);
        return handleWidth + Math.Clamp(seconds / _maximum, 0, 1) * Math.Max(0, width - 2 * handleWidth);
    }

    private static Rect HandleRect(double x, double width, double height, DragMode mode)
    {
        var handleWidth = EffectiveHandleWidth(width);
        // Handles sit outside their trim boundaries so even a tiny selected
        // interval retains two separate grips instead of overlapping them.
        return new Rect(mode == DragMode.TrimStart ? x - handleWidth : x, 0, handleWidth, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var xs = XFor(_trimStart, width);
        var xe = XFor(_trimEnd, width);

        // Cinema filmstrip track + subtle vertical tick marks
        dc.DrawRectangle(SurfaceBrush, null, new Rect(0, 0, width, height));
        for (var tickX = 24.0; tickX < width - 8; tickX += 28.0)
        {
            dc.DrawLine(TickPen, new Point(tickX, 4), new Point(tickX, Math.Max(4, height - 4)));
        }

        var startHandle = HandleRect(xs, width, height, DragMode.TrimStart);
        var endHandle = HandleRect(xe, width, height, DragMode.TrimEnd);

        // Dim trimmed-out regions outside the handles
        if (startHandle.Left > 0.5)
        {
            dc.DrawRectangle(ExcludedDimBrush, null, new Rect(0, 0, startHandle.Left, height));
        }
        if (endHandle.Right < width - 0.5)
        {
            dc.DrawRectangle(ExcludedDimBrush, null, new Rect(endHandle.Right, 0, width - endHandle.Right, height));
        }

        // Luminous ice-cyan active interval highlight & top/bottom rails
        if (xe > xs)
        {
            dc.DrawRectangle(ActiveZoneBrush, null, new Rect(xs, 1, xe - xs, Math.Max(0, height - 2)));
            dc.DrawLine(ActiveEdgePen, new Point(xs, 0.8), new Point(xe, 0.8));
            dc.DrawLine(ActiveEdgePen, new Point(xs, height - 0.8), new Point(xe, height - 0.8));
        }

        var x = XFor(_value, width);
        DrawHandle(dc, startHandle, DragMode.TrimStart);
        DrawHandle(dc, endHandle, DragMode.TrimEnd);

        var playheadWidth = Math.Min(PlayheadWidth, Math.Max(0, xe - xs));
        var playheadX = Math.Clamp(x, xs, Math.Max(xs, xe - playheadWidth));
        var inset = Math.Min(1.5, height / 2);
        dc.DrawRoundedRectangle(
            PlayheadBrush, null,
            new Rect(playheadX, inset, playheadWidth, Math.Max(0, height - 2 * inset)),
            1.2, 1.2);
    }

    private void DrawHandle(DrawingContext dc, Rect rect, DragMode mode)
    {
        var brush = _dragMode == mode || _hoverMode == mode ? AccentHoverBrush : AccentBrush;
        dc.DrawRoundedRectangle(brush, null, rect, 4, 4);
        var squareHalf = mode == DragMode.TrimStart
            ? new Rect(rect.Left + rect.Width / 2, rect.Top, rect.Width / 2, rect.Height)
            : new Rect(rect.Left, rect.Top, rect.Width / 2, rect.Height);
        dc.DrawRectangle(brush, null, squareHalf);

        // Clean single slot pill inside the ice-cyan grip
        var center = rect.Left + rect.Width / 2;
        var slotHeight = Math.Clamp(rect.Height * 0.42, 4, 12);
        var slotTop = rect.Top + (rect.Height - slotHeight) / 2;
        dc.DrawRoundedRectangle(GripBrush, null, new Rect(center - 1, slotTop, 2, slotHeight), 1, 1);
    }

    private DragMode HitTest(Point point, double width)
    {
        var xs = XFor(_trimStart, width);
        var xe = XFor(_trimEnd, width);
        var startRect = HandleRect(xs, width, ActualHeight, DragMode.TrimStart);
        var endRect = HandleRect(xe, width, ActualHeight, DragMode.TrimEnd);
        var startCenter = startRect.Left + startRect.Width / 2;
        var endCenter = endRect.Left + endRect.Width / 2;
        startRect.Inflate(HandleHitPadding, 3);
        endRect.Inflate(HandleHitPadding, 3);
        if (startRect.Contains(point) && endRect.Contains(point))
        {
            return Math.Abs(point.X - startCenter) <= Math.Abs(point.X - endCenter)
                ? DragMode.TrimStart
                : DragMode.TrimEnd;
        }
        if (startRect.Contains(point))
        {
            return DragMode.TrimStart;
        }
        if (endRect.Contains(point))
        {
            return DragMode.TrimEnd;
        }
        return DragMode.Thumb;
    }

    private void ApplyDrag(Point point)
    {
        if (ActualWidth <= 0)
        {
            return;
        }
        var handleWidth = EffectiveHandleWidth(ActualWidth);
        var usableWidth = Math.Max(0.001, ActualWidth - 2 * handleWidth);
        var ratio = Math.Clamp((point.X - _dragOffsetX - handleWidth) / usableWidth, 0, 1);
        switch (_dragMode)
        {
            case DragMode.TrimStart:
                SetTrimStart(ratio * _maximum);
                break;
            case DragMode.TrimEnd:
                SetTrimEnd(ratio * _maximum);
                break;
            default:
                Value = ratio * _maximum;
                break;
        }
    }

    private void BeginDrag(Point point)
    {
        _dragMode = HitTest(point, ActualWidth);
        _dragOffsetX = _dragMode switch
        {
            DragMode.TrimStart => point.X - XFor(_trimStart, ActualWidth),
            DragMode.TrimEnd => point.X - XFor(_trimEnd, ActualWidth),
            _ => 0
        };
        Cursor = _dragMode is DragMode.TrimStart or DragMode.TrimEnd
            ? System.Windows.Input.Cursors.SizeWE
            : System.Windows.Input.Cursors.Hand;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        ApplyDrag(point);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!CaptureMouse())
        {
            return;
        }
        BeginDrag(e.GetPosition(this));
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            {
                CompleteDrag(e.GetPosition(this));
            }
            else
            {
                ApplyDrag(e.GetPosition(this));
            }
        }
        else
        {
            var hoverMode = HitTest(e.GetPosition(this), ActualWidth);
            if (hoverMode != _hoverMode)
            {
                _hoverMode = hoverMode;
                Cursor = hoverMode is DragMode.TrimStart or DragMode.TrimEnd
                    ? System.Windows.Input.Cursors.SizeWE
                    : System.Windows.Input.Cursors.Hand;
                InvalidateVisual();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            ApplyDrag(e.GetPosition(this));
            CompleteDrag(e.GetPosition(this));
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        CompleteDrag(null);
        base.OnLostMouseCapture(e);
    }

    private void CompleteDrag(Point? point)
    {
        if (_dragMode == DragMode.None)
        {
            return;
        }
        _dragMode = DragMode.None;
        _dragOffsetX = 0;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        _hoverMode = IsMouseOver && point.HasValue ? HitTest(point.Value, ActualWidth) : DragMode.None;
        Cursor = _hoverMode is DragMode.TrimStart or DragMode.TrimEnd
            ? System.Windows.Input.Cursors.SizeWE
            : System.Windows.Input.Cursors.Hand;
        InvalidateVisual();
        ScrubCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragMode == DragMode.None && _hoverMode != DragMode.None)
        {
            _hoverMode = DragMode.None;
            Cursor = System.Windows.Input.Cursors.Hand;
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }
}

/// <summary>
/// Horizontal volume slider drawn directly (code-built Slider templates are
/// off-limits, see TimelineBar).  Range 0..1 with live ValueChanged while
/// dragging, styled to match the moon-blue timeline.
/// </summary>
internal sealed class VolumeBar : FrameworkElement
{
    private static readonly Brush TrackBrush = RecordingTheme.Brush("Brush.Border", "#39556F");
    private static readonly Brush FillBrush = RecordingTheme.Brush("Brush.Accent", "#A7CFDF");
    private bool _dragging;
    private double _value;

    public event EventHandler? ValueChanged;

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(clamped - _value) < 0.001)
            {
                return;
            }
            _value = clamped;
            InvalidateVisual();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public VolumeBar() => Cursor = System.Windows.Input.Cursors.Hand;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var radius = height / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);
        var x = _value * width;
        if (x > 0.5)
        {
            dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, x, height), radius, radius);
        }
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        CaptureMouse();
        _dragging = true;
        ApplyDrag(e.GetPosition(this));
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging)
        {
            ApplyDrag(e.GetPosition(this));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }
        base.OnMouseLeftButtonUp(e);
    }

    private void ApplyDrag(Point point)
    {
        if (ActualWidth > 0)
        {
            Value = Math.Clamp(point.X / ActualWidth, 0, 1);
        }
    }
}

/// <summary>Shared recording chrome tokens, with frozen fallbacks for standalone control previews.</summary>
internal static class RecordingTheme
{
    internal static Brush Brush(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush resource)
        {
            return resource;
        }

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        brush.Freeze();
        return brush;
    }
}
