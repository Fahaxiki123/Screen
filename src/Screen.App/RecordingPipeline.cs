using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Locates the bundled ffmpeg.exe (publish layout), a loose copy next to the
/// executable, or a machine-wide installation on PATH.
/// </summary>
internal static class FfmpegLocator
{
    public static string Resolve()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }
        var loose = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(loose))
        {
            return loose;
        }
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entries must not break the search.
            }
        }
        throw new FileNotFoundException("录屏需要 ffmpeg.exe：请将其放入程序的 ffmpeg\\bin 目录，或安装 FFmpeg 并加入 PATH。");
    }
}

/// <summary>
/// Probes available H.264 encoders once per FFmpeg binary and caches the fastest
/// working option (NVENC → QSV → AMF → libx264).
/// </summary>
internal static class FfmpegEncoderProbe
{
    internal const string SoftwareEncoder = "libx264";
    internal const int MinHardwareWidth = 160;
    internal const int MinHardwareHeight = 128;
    internal const int MaxHardwareDimension = 4096;

    private static readonly string[] HardwareCandidates = ["h264_nvenc", "h264_qsv", "h264_amf"];
    private static readonly ConcurrentDictionary<string, string> CachedEncoders = new(StringComparer.OrdinalIgnoreCase);

    public static bool SupportsHardwareDimensions(int width, int height) =>
        width >= MinHardwareWidth
        && height >= MinHardwareHeight
        && width <= MaxHardwareDimension
        && height <= MaxHardwareDimension;

    public static string SelectEncoder(string ffmpegPath, int width, int height)
    {
        if (!SupportsHardwareDimensions(width, height))
        {
            return SoftwareEncoder;
        }
        var fullPath = Path.GetFullPath(ffmpegPath);
        var stamp = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : 0;
        var cacheKey = $"{fullPath}|{stamp}";
        return CachedEncoders.GetOrAdd(cacheKey, _ => ProbePreferredEncoder(fullPath));
    }

    internal static IReadOnlyList<string> BuildEncoderArguments(string encoderName, int bitrate)
    {
        var bitrateText = bitrate.ToString(CultureInfo.InvariantCulture);
        return encoderName switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-b:v", bitrateText, "-pix_fmt", "nv12"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "veryfast", "-b:v", bitrateText, "-pix_fmt", "nv12"],
            "h264_amf" => ["-c:v", "h264_amf", "-quality", "balanced", "-b:v", bitrateText, "-pix_fmt", "nv12"],
            SoftwareEncoder => ["-c:v", SoftwareEncoder, "-preset", "veryfast", "-b:v", bitrateText, "-pix_fmt", "yuv420p"],
            _ => ["-c:v", encoderName, "-b:v", bitrateText, "-pix_fmt", "yuv420p"]
        };
    }

    internal static string ProbePreferredEncoder(string ffmpegPath, Func<string, bool>? tryProbe = null)
    {
        var probe = tryProbe ?? (candidate => TryEncodeSingleFrame(ffmpegPath, candidate));
        foreach (var candidate in HardwareCandidates)
        {
            try
            {
                if (probe(candidate))
                {
                    SimpleLogger.LogInfo($"Recording H.264 encoder selected: {candidate}");
                    return candidate;
                }
            }
            catch (Exception exception)
            {
                SimpleLogger.LogWarning($"Hardware encoder probe failed for {candidate}: {exception.Message}");
            }
        }
        SimpleLogger.LogInfo($"Recording H.264 encoder selected: {SoftwareEncoder} (software fallback)");
        return SoftwareEncoder;
    }

    private static bool TryEncodeSingleFrame(string ffmpegPath, string encoderName)
    {
        const int ProbeWidth = 256;
        const int ProbeHeight = 256;
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-f", "rawvideo", "-pix_fmt", "nv12",
                     "-video_size", $"{ProbeWidth}x{ProbeHeight}",
                     "-framerate", "30",
                     "-i", "pipe:0"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var argument in BuildEncoderArguments(encoderName, 1_000_000))
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }
        _ = process.StandardError.ReadToEndAsync();
        using var probeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var stopProbe = probeDeadline.Token.Register(() =>
        {
            try
            { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var frame = new byte[ProbeWidth * ProbeHeight * 3 / 2];
        frame.AsSpan(0, ProbeWidth * ProbeHeight).Fill(16);
        frame.AsSpan(ProbeWidth * ProbeHeight).Fill(128);
        try
        {
            process.StandardInput.BaseStream.Write(frame);
            process.StandardInput.BaseStream.Flush();
            process.StandardInput.BaseStream.Close();
        }
        catch (IOException)
        {
            // Encoder failed before reading the entire probe frame.
        }
        if (!process.WaitForExit(3000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            return false;
        }
        return process.ExitCode == 0;
    }
}

/// <summary>
/// H.264 encoder backed by an ffmpeg child process: packed NV12 or BGRA frames
/// carry capture timestamps through fragmented MOV input into a VFR .mp4. Hardware
/// encoders (NVENC/QSV/AMF) are preferred when available, with automatic
/// startup replay fallback to <c>libx264</c> if a hardware session fails to open.
/// </summary>
internal sealed class RecordingPipeline : IDisposable
{
    private const int MaxStartupReplayFrames = 8;

    private readonly object _processGate = new();
    private readonly string _ffmpegPath;
    private readonly string _filePath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameRate;
    private readonly int _bitrate;
    private readonly string _pixelFormat;
    private readonly int _frameByteSize;
    private readonly StringBuilder _errorLog = new();
    private Process _process;
    private Stream _input;
    private RecordingTimestampStream _timestamps;
    private List<(byte[] Frame, double Seconds)>? _startupFrames;
    private long _submittedFrames;
    private double _lastTimestamp;
    private double? _endTimestamp;
    private bool _canFallbackToSoftware;
    private bool _aborted;
    private bool _finalized;

    public RecordingPipeline(
        string filePath,
        int width,
        int height,
        int frameRate,
        int bitrate,
        string pixelFormat,
        string? encoderOverride = null)
    {
        // Even dimensions keep chroma planes aligned; H.264 4:2:0 expects it.
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("录制分辨率的宽和高都必须是偶数。");
        }
        if (pixelFormat is not ("nv12" or "bgra"))
        {
            throw new ArgumentException($"不支持的像素格式：{pixelFormat}");
        }
        _ffmpegPath = FfmpegLocator.Resolve();
        _filePath = filePath;
        _width = width;
        _height = height;
        _frameRate = frameRate;
        _bitrate = bitrate;
        _pixelFormat = pixelFormat;
        _frameByteSize = pixelFormat == "nv12"
            ? checked(width * height * 3 / 2)
            : checked(width * height * 4);

        var encoder = encoderOverride ?? FfmpegEncoderProbe.SelectEncoder(_ffmpegPath, width, height);
        SelectedEncoder = encoder;
        _canFallbackToSoftware = !string.Equals(encoder, FfmpegEncoderProbe.SoftwareEncoder, StringComparison.OrdinalIgnoreCase);
        if (_canFallbackToSoftware)
        {
            _startupFrames = new List<(byte[], double)>(MaxStartupReplayFrames);
        }
        (_process, _input) = StartEncoderProcess(encoder);
        _timestamps = new RecordingTimestampStream(_input, width, height, _pixelFormat);
    }

    public int Width => _width;
    public int Height => _height;
    public int FrameByteSize => _frameByteSize;
    public string SelectedEncoder { get; private set; }

    /// <summary>
    /// Writes one packed frame (NV12: Y plane followed by interleaved UV;
    /// BGRA: four bytes per pixel).
    /// Blocks while the encoder pipe is full; the capture loop may skip work
    /// while each accepted frame retains its actual unpaused timestamp.
    /// </summary>
    public void WriteVideoFrame(ReadOnlySpan<byte> frame) => WriteVideoFrame(frame, _submittedFrames / (double)_frameRate);

    public void WriteVideoFrame(ReadOnlySpan<byte> frame, double captureSeconds)
    {
        ObjectDisposedException.ThrowIf(_finalized, this);
        if (frame.Length != _frameByteSize)
        {
            throw new ArgumentException($"帧缓冲大小不匹配：期望 {_frameByteSize}，实际 {frame.Length}。");
        }

        if (!double.IsFinite(captureSeconds) || captureSeconds < 0
            || (_submittedFrames > 0 && captureSeconds <= _lastTimestamp))
        {
            throw new ArgumentException("采集时间必须严格递增。", nameof(captureSeconds));
        }
        var pixels = frame;
        if (_canFallbackToSoftware && !_aborted && HasProcessFaultedEarly())
        {
            FallbackToSoftwareEncoder(pixels, captureSeconds);
            AcceptTimestamp(captureSeconds);
            return;
        }

        try
        {
            _timestamps.WriteFrame(pixels, captureSeconds);
        }
        catch (IOException) when (_canFallbackToSoftware && !_aborted)
        {
            FallbackToSoftwareEncoder(pixels, captureSeconds);
            AcceptTimestamp(captureSeconds);
            return;
        }

        if (_canFallbackToSoftware && _startupFrames is not null)
        {
            _startupFrames.Add((pixels.ToArray(), captureSeconds));
            if ((_startupFrames.Count >= MaxStartupReplayFrames
                 || (long)_startupFrames.Count * pixels.Length >= 64 * 1024 * 1024)
                && !HasProcessFaultedEarly())
            {
                _canFallbackToSoftware = false;
                _startupFrames = null;
            }
        }
        AcceptTimestamp(captureSeconds);
    }

    private void AcceptTimestamp(double seconds)
    {
        _lastTimestamp = seconds;
        _submittedFrames++;
    }

    public void SetEndTimestamp(double seconds) => _endTimestamp = Math.Max(seconds, _lastTimestamp + 0.000001);

    /// <summary>Closes the pipe and waits for ffmpeg to finish the file. Idempotent.</summary>
    public void FinalizeFile()
    {
        if (_finalized)
        {
            return;
        }
        _finalized = true;
        var timedOut = 0;
        using var deadline = new Timer(_ =>
        {
            Interlocked.Exchange(ref timedOut, 1);
            Abort();
        }, null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        CloseInputAndWait();

        if (_process.ExitCode != 0 && _canFallbackToSoftware && !_aborted && _startupFrames is { Count: > 0 } buffered)
        {
            SimpleLogger.LogWarning(
                $"Hardware encoder '{SelectedEncoder}' failed during finalization (exit {_process.ExitCode}); replaying {buffered.Count} frame(s) with {FfmpegEncoderProbe.SoftwareEncoder}.");
            SwitchEncoderProcess(FfmpegEncoderProbe.SoftwareEncoder);
            _canFallbackToSoftware = false;
            _startupFrames = null;
            foreach (var saved in buffered)
            {
                _timestamps.WriteFrame(saved.Frame, saved.Seconds);
            }
            CloseInputAndWait();
        }

        if (Volatile.Read(ref timedOut) != 0)
        {
            throw new TimeoutException("视频收尾超时（2 分钟），素材已保留。");
        }
        if (_aborted)
        {
            throw new OperationCanceledException("已取消视频收尾，素材已保留。");
        }
        if (_process.ExitCode != 0)
        {
            string tail;
            lock (_errorLog)
            {
                tail = _errorLog.ToString();
            }
            tail = tail.Length > 500 ? tail[^500..] : tail;
            throw new InvalidOperationException($"ffmpeg 编码失败（退出码 {_process.ExitCode}）：{tail.Trim()}");
        }
    }

    public void Dispose()
    {
        // Also kill after a failed finalization: _finalized means the pipe is
        // no longer writable, not necessarily that the child has exited.
        KillQuietly();
        lock (_processGate)
        {
            try
            {
                _input.Dispose();
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    public void Abort()
    {
        _aborted = true;
        KillQuietly();
    }

    private (Process Process, Stream Input) StartEncoderProcess(string encoderName)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-probesize", "32", "-analyzeduration", "1", "-fpsprobesize", "0",
                     "-f", "mov",
                     "-i", "pipe:0"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var argument in FfmpegEncoderProbe.BuildEncoderArguments(encoderName, _bitrate))
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var argument in new[] { "-fps_mode", "vfr", "-enc_time_base", "1:1000000", "-bf", "0", "-video_track_timescale", "1000000", "-colorspace", "smpte170m", "-color_range", "tv" })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add(_filePath);

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法启动 ffmpeg 编码进程。");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (_errorLog)
                {
                    _errorLog.AppendLine(line);
                }
            }
        };
        process.BeginErrorReadLine();
        return (process, process.StandardInput.BaseStream);
    }

    private bool HasProcessFaultedEarly()
    {
        try
        {
            return _process.HasExited && _process.ExitCode != 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void FallbackToSoftwareEncoder(ReadOnlySpan<byte> currentFrame, double captureSeconds)
    {
        var previousEncoder = SelectedEncoder;
        var buffered = _startupFrames;
        _canFallbackToSoftware = false;
        _startupFrames = null;

        SimpleLogger.LogWarning(
            $"Hardware encoder '{previousEncoder}' failed to open at {_width}x{_height}; falling back to {FfmpegEncoderProbe.SoftwareEncoder}.");
        SwitchEncoderProcess(FfmpegEncoderProbe.SoftwareEncoder);

        if (buffered is not null)
        {
            foreach (var saved in buffered)
            {
                _timestamps.WriteFrame(saved.Frame, saved.Seconds);
            }
        }
        _timestamps.WriteFrame(currentFrame, captureSeconds);
    }

    private void SwitchEncoderProcess(string encoderName)
    {
        KillQuietly();
        lock (_processGate)
        {
            if (_aborted)
            {
                throw new OperationCanceledException("编码已中止。");
            }
            try
            {
                _input.Dispose();
            }
            catch
            {
            }
            _process.Dispose();
            lock (_errorLog)
            {
                _errorLog.Clear();
            }
            SelectedEncoder = encoderName;
            (_process, _input) = StartEncoderProcess(encoderName);
            _timestamps = new RecordingTimestampStream(_input, _width, _height, _pixelFormat);
        }
    }

    private void CloseInputAndWait()
    {
        try
        {
            _timestamps.Finish(_endTimestamp ?? (_lastTimestamp + 1.0 / _frameRate));
            _input.Flush();
            _input.Close();
        }
        catch (IOException)
        {
            // ffmpeg may already be gone; WaitForExit/exit code tell the truth.
        }
        if (!_process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            KillQuietly();
            throw new TimeoutException("ffmpeg 收尾超时，录制文件可能不完整。");
        }
    }

    private void KillQuietly()
    {
        lock (_processGate)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The join timeout still reports failure if the worker cannot exit.
            }
        }
    }
}
