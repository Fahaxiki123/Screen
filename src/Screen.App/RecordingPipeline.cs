using System.Diagnostics;
using System.Globalization;
using System.Text;

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
/// H.264 encoder backed by an ffmpeg child process: packed NV12 frames are
/// written to its stdin as rawvideo and ffmpeg emits a CFR .mp4.  This
/// replaces the original Media Foundation Sink Writer design — smoke testing
/// proved Sink Writer cannot encode H.264 on this Windows generation (its
/// only synchronous encoders left are legacy ones such as WMV3; the H.264
/// MFTs are async and the sink cannot drive them).  Single writer thread.
/// </summary>
internal sealed class RecordingPipeline : IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly StringBuilder _errorLog = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameByteSize;
    private bool _finalized;

    public RecordingPipeline(string filePath, int width, int height, int frameRate, int bitrate, string pixelFormat)
    {
        // Even dimensions keep chroma planes aligned; libx264 expects it.
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("录制分辨率的宽和高都必须是偶数。");
        }
        if (pixelFormat is not ("nv12" or "bgra"))
        {
            throw new ArgumentException($"不支持的像素格式：{pixelFormat}");
        }
        _width = width;
        _height = height;
        _frameByteSize = pixelFormat == "nv12"
            ? checked(width * height * 3 / 2)
            : checked(width * height * 4);

        var startInfo = new ProcessStartInfo(FfmpegLocator.Resolve())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "rawvideo", "-pix_fmt", pixelFormat,
                     "-video_size", $"{width}x{height}",
                     "-framerate", frameRate.ToString(CultureInfo.InvariantCulture),
                     "-i", "pipe:0",
                     "-c:v", "libx264", "-preset", "veryfast",
                     "-b:v", bitrate.ToString(CultureInfo.InvariantCulture),
                     "-pix_fmt", "yuv420p",
                     "-movflags", "+faststart",
                     filePath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("无法启动 ffmpeg 编码进程。");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (_errorLog)
                {
                    _errorLog.AppendLine(line);
                }
            }
        };
        _process.BeginErrorReadLine();
        _input = _process.StandardInput.BaseStream;
    }

    public int Width => _width;
    public int Height => _height;
    public int FrameByteSize => _frameByteSize;

    /// <summary>
    /// Writes one packed frame (NV12: Y plane followed by interleaved UV;
    /// BGRA: four bytes per pixel).
    /// Blocks while the encoder pipe is full — that backpressure is the CFR
    /// pacing signal for the capture loop.
    /// </summary>
    public void WriteVideoFrame(ReadOnlySpan<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_finalized, this);
        if (frame.Length != _frameByteSize)
        {
            throw new ArgumentException($"帧缓冲大小不匹配：期望 {_frameByteSize}，实际 {frame.Length}。");
        }
        _input.Write(frame);
    }

    /// <summary>Closes the pipe and waits for ffmpeg to finish the file. Idempotent.</summary>
    public void FinalizeFile()
    {
        if (_finalized)
        {
            return;
        }
        _finalized = true;
        try
        {
            _input.Flush();
            _input.Close();
        }
        catch (IOException)
        {
            // ffmpeg may already be gone; WaitForExit/exit code tell the truth.
        }
        if (!_process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            KillQuietly();
            throw new TimeoutException("ffmpeg 收尾超时，录制文件可能不完整。");
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
        try
        {
            _input.Dispose();
        }
        finally
        {
            _process.Dispose();
        }
    }

    public void Abort() => KillQuietly();

    private void KillQuietly()
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
