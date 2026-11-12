using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;

namespace Screen.App;

/// <summary>
/// Playback half of the recording editor: media probe, video decode and audio
/// output. Every decoder/audio process is owned by the playback generation
/// that started it and is killed through that generation's cancellation token,
/// so the UI thread never shares a process handle with a decode task.
/// </summary>
internal sealed partial class RecordingPreviewWindow
{
    // Large sources decode on the GPU (D3D11VA) during continuous playback;
    // ffmpeg falls back to software decoding by itself when that is unavailable.
    private const int HardwareDecodePixelThreshold = 2560 * 1440;

    private WriteableBitmap? _bitmap;
    private (int Width, int Height) _frameSize;
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;
    private int _decodeGeneration;
    private bool _scrubPreviewRunning;
    private double? _pendingScrubPreviewSeconds;
    private DispatcherTimer? _resizeDebounce;

    // Audio output: ffmpeg decodes the soundtrack to raw PCM, NAudio plays it.
    // WaveOut is created, re-armed and disposed on the UI thread only.
    private WaveOut? _waveOut;

    private sealed record DecodeRequest(
        double StartSeconds,
        bool AutoPlay,
        int Generation,
        int Width,
        int Height,
        bool Scaled,
        double FramesPerSecond,
        double Speed);

    // ------------------------------------------------------------------
    // Media info probe (ffmpeg -i stderr parsing)
    // ------------------------------------------------------------------

    private async Task LoadMediaInfoAsync()
    {
        string stderr;
        try
        {
            var startInfo = CreateFfmpegStartInfo(redirectOutput: false);
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
        if (RecordingMediaInfo.TryParse(stderr) is not { } info)
        {
            ShowPlaybackError("无法解析录屏文件（文件可能损坏）。");
            return;
        }

        _durationSeconds = info.DurationSeconds;
        _videoWidth = info.Width;
        _videoHeight = info.Height;
        _fps = info.FramesPerSecond;
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
        // While paused, show the seeked frame without starting playback.
        StartDecoderFrom(target, autoPlay: _playing);
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
        var size = EnsureFrameBitmap();
        var generation = Interlocked.Increment(ref _decodeGeneration);
        var cts = new CancellationTokenSource();
        _decodeCts = cts;
        var request = new DecodeRequest(
            startSeconds,
            autoPlay,
            generation,
            size.Width,
            size.Height,
            size != (_videoWidth, _videoHeight),
            _fps,
            _speed);
        _decodeTask = Task.Run(() => DecodeLoopAsync(request, cts.Token));
        if (autoPlay)
        {
            StartAudioFrom(startSeconds, generation, cts.Token);
        }
    }

    private void StopDecoder()
    {
        Interlocked.Increment(ref _decodeGeneration);
        // Cancellation kills this generation's processes through their token
        // registrations. The source is not disposed: a decode task may still
        // be observing its token while it unwinds.
        _decodeCts?.Cancel();
        _decodeCts = null;
        StopAudioInternal();
    }

    // ------------------------------------------------------------------
    // Decode size follows the on-screen video area
    // ------------------------------------------------------------------

    private (int Width, int Height) DesiredFrameSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return RecordingMediaInfo.FitDecodeSize(
            _videoWidth,
            _videoHeight,
            _videoHost.ActualWidth * dpi.DpiScaleX,
            _videoHost.ActualHeight * dpi.DpiScaleY);
    }

    private (int Width, int Height) EnsureFrameBitmap()
    {
        var size = DesiredFrameSize();
        if (_bitmap is null || _frameSize != size)
        {
            _frameSize = size;
            _bitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null);
            _videoImage.Source = _bitmap;
        }
        return size;
    }

    private void OnVideoHostSizeChanged()
    {
        if (!_loaded || _closed)
        {
            return;
        }
        if (_resizeDebounce is null)
        {
            _resizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _resizeDebounce.Tick += (_, _) =>
            {
                _resizeDebounce.Stop();
                if (!_loaded || _closed || _scrubbing
                    || !RecordingMediaInfo.ShouldRedecode(_frameSize, DesiredFrameSize()))
                {
                    return;
                }
                StartDecoderFrom(_positionSeconds, autoPlay: _playing);
            };
        }
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    // ------------------------------------------------------------------
    // Audio
    // ------------------------------------------------------------------

    private void StartAudioFrom(double startSeconds, int generation, CancellationToken cancellationToken)
    {
        StopAudioInternal();
        WaveOut? waveOut = null;
        Process? process = null;
        try
        {
            var buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                ReadFully = false
            };
            waveOut = new WaveOut();
            waveOut.Init(buffer);
            waveOut.Volume = (float)(_muted ? 0 : _volume);
            waveOut.Play();

            var startInfo = CreateFfmpegStartInfo(redirectOutput: true);
            if (startSeconds > 0.05)
            {
                startInfo.ArgumentList.Add("-ss");
                startInfo.ArgumentList.Add(startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", _filePath })
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (Math.Abs(_speed - 1.0) > 0.001)
            {
                // atempo keeps pitch while changing playback rate (0.5–2x).
                startInfo.ArgumentList.Add("-filter:a");
                startInfo.ArgumentList.Add("atempo=" + _speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            foreach (var argument in new[] { "-vn", "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1" })
            {
                startInfo.ArgumentList.Add(argument);
            }
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start.");
            var errors = ProcessErrorTail.Attach(process);
            _waveOut = waveOut;
            var ownedProcess = process;
            var ownedWaveOut = waveOut;
            _ = Task.Run(() => AudioPumpAsync(ownedProcess, errors, buffer, ownedWaveOut, generation, cancellationToken));
        }
        catch (Exception exception)
        {
            // Silent preview is preferable to sinking the editor.
            SimpleLogger.LogWarning($"Preview audio unavailable: {exception.Message}");
            TryKill(process);
            process?.Dispose();
            if (waveOut is not null && !ReferenceEquals(waveOut, _waveOut))
            {
                waveOut.Dispose();
            }
        }
    }

    private async Task AudioPumpAsync(
        Process process,
        ProcessErrorTail errors,
        BufferedWaveProvider buffer,
        WaveOut waveOut,
        int generation,
        CancellationToken cancellationToken)
    {
        using var kill = cancellationToken.Register(() => TryKill(process));
        var stream = process.StandardOutput.BaseStream;
        var chunk = new byte[32768];
        try
        {
            while (IsCurrent(generation, cancellationToken))
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read <= 0)
                {
                    break;
                }
                // WaveOut auto-stops when the provider runs dry (ReadFully=false):
                // data typically arrives ~60 ms after Play(), so re-arm it
                // whenever a chunk lands while it is stopped.
                RearmWaveOut(waveOut, generation);
                // Backpressure: let WaveOut drain before feeding more.
                while (IsCurrent(generation, cancellationToken)
                       && buffer.BufferedDuration > TimeSpan.FromMilliseconds(400))
                {
                    RearmWaveOut(waveOut, generation);
                    await Task.Delay(20, cancellationToken);
                }
                if (!IsCurrent(generation, cancellationToken))
                {
                    break;
                }
                buffer.AddSamples(chunk, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (IsCurrent(generation, cancellationToken))
            {
                SimpleLogger.LogWarning($"Preview audio stopped: {exception.Message} {errors.Text}");
            }
        }
        finally
        {
            TryKill(process);
            process.Dispose();
        }
    }

    private void RearmWaveOut(WaveOut waveOut, int generation)
    {
        if (waveOut.PlaybackState != PlaybackState.Stopped)
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_waveOut, waveOut) || generation != Volatile.Read(ref _decodeGeneration))
            {
                return;
            }
            try
            {
                if (waveOut.PlaybackState == PlaybackState.Stopped)
                {
                    waveOut.Play();
                }
            }
            catch (Exception exception)
            {
                SimpleLogger.LogWarning($"Preview audio re-arm failed: {exception.Message}");
            }
        });
    }

    private void StopAudioInternal()
    {
        var waveOut = _waveOut;
        _waveOut = null;
        if (waveOut is null)
        {
            return;
        }
        try
        {
            waveOut.Stop();
        }
        catch
        {
        }
        waveOut.Dispose();
    }

    // ------------------------------------------------------------------
    // Video
    // ------------------------------------------------------------------

    private async Task DecodeLoopAsync(DecodeRequest request, CancellationToken cancellationToken)
    {
        Process process;
        ProcessErrorTail errors;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = CreateFfmpegStartInfo(redirectOutput: true);
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            if (request.AutoPlay && (long)_videoWidth * _videoHeight >= HardwareDecodePixelThreshold)
            {
                startInfo.ArgumentList.Add("-hwaccel");
                startInfo.ArgumentList.Add("d3d11va");
            }
            if (request.StartSeconds > 0.05)
            {
                startInfo.ArgumentList.Add("-ss");
                startInfo.ArgumentList.Add(request.StartSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(_filePath);
            if (!request.AutoPlay)
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
            else
            {
                startInfo.ArgumentList.Add("-an");
            }
            if (request.Scaled)
            {
                // Decoding at display size instead of source size cuts the
                // per-frame pipe and WritePixels traffic by the square of the
                // downscale (a 4K recording in a 1000px window: ~16x less).
                startInfo.ArgumentList.Add("-vf");
                startInfo.ArgumentList.Add($"scale={request.Width}:{request.Height}:flags=bilinear");
            }
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("rawvideo");
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("bgra");
            startInfo.ArgumentList.Add("pipe:1");
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start.");
            errors = ProcessErrorTail.Attach(process);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogWarning($"Preview decoder unavailable: {exception.Message}");
            return;
        }

        using var kill = cancellationToken.Register(() => TryKill(process));
        var frameBytes = checked(request.Width * request.Height * 4);
        var buffer = new byte[frameBytes];
        var frameDuration = TimeSpan.FromSeconds(1 / request.FramesPerSecond);
        var stopwatch = Stopwatch.StartNew();
        long frameIndex = 0;
        var ended = false;

        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (IsCurrent(request.Generation, cancellationToken))
            {
                var read = await ReadExactAsync(stream, buffer, frameBytes, cancellationToken);
                if (read < frameBytes)
                {
                    ended = true;
                    break;
                }
                if (!IsCurrent(request.Generation, cancellationToken))
                {
                    break;
                }

                var capturedPosition = request.StartSeconds + frameIndex / request.FramesPerSecond;
                await Dispatcher.InvokeAsync(() =>
                {
                    // A seek or resize may have replaced the bitmap since this
                    // frame was read; only the current generation may draw.
                    if (_bitmap is null
                        || request.Generation != Volatile.Read(ref _decodeGeneration)
                        || _bitmap.PixelWidth != request.Width
                        || _bitmap.PixelHeight != request.Height)
                    {
                        return;
                    }
                    _bitmap.WritePixels(new Int32Rect(0, 0, request.Width, request.Height), buffer, request.Width * 4, 0);
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
                if (!request.AutoPlay)
                {
                    // Paused seek: render the first frame only, then stop.
                    break;
                }
                var target = TimeSpan.FromTicks((long)(frameIndex * frameDuration.Ticks / request.Speed));
                var wait = target - stopwatch.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            ended = true;
        }
        finally
        {
            TryKill(process);
            process.Dispose();
        }

        if (ended && frameIndex == 0 && IsCurrent(request.Generation, cancellationToken))
        {
            SimpleLogger.LogWarning($"Preview decoder produced no frame: {errors.Text}");
        }
        if (ended && request.AutoPlay && IsCurrent(request.Generation, cancellationToken))
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (request.Generation != Volatile.Read(ref _decodeGeneration))
                {
                    return;
                }
                _playing = false;
                SetPlayingVisual(false);
                _positionSeconds = Math.Max(_timeline.TrimStart, Math.Min(_durationSeconds, _timeline.TrimEnd));
                UpdateTimeText();
            });
        }
    }

    // ------------------------------------------------------------------
    // Process helpers
    // ------------------------------------------------------------------

    private bool IsCurrent(int generation, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && generation == Volatile.Read(ref _decodeGeneration);

    private static ProcessStartInfo CreateFfmpegStartInfo(bool redirectOutput) =>
        new(FfmpegLocator.Resolve())
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
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
}
