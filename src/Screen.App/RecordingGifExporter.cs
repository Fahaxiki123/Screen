using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screen.App;

internal static class RecordingGifExporter
{
    internal const int FramesPerSecond = 10;
    private const int FrameDelay = 100 / FramesPerSecond;
    internal const int MaxLongEdge = 1280;

    public static Task ExportAsync(string source, string destination, int width, int height, RecordingTrimRange range, CancellationToken token) =>
        Task.Run(() => Export(source, destination, width, height, range, token, overwrite: true), token);

    internal static void Export(string source, string destination, int width, int height, RecordingTrimRange range,
        CancellationToken token, bool overwrite = false)
    {
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue
            || !double.IsFinite(range.Start) || !double.IsFinite(range.End) || range.Start < 0 || range.End <= range.Start)
            throw new ArgumentOutOfRangeException(nameof(range), "GIF 尺寸或导出时间范围无效。");

        var target = Path.GetFullPath(destination);
        var staging = Path.Combine(Path.GetDirectoryName(target)!, $".screen-export-{Guid.NewGuid():N}.tmp");
        var decoder = new ProcessStartInfo(FfmpegLocator.Resolve())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var scale = Math.Min(1.0, (double)MaxLongEdge / Math.Max(width, height));
        int gifWidth = Math.Max(1, (int)Math.Round(width * scale));
        int gifHeight = Math.Max(1, (int)Math.Round(height * scale));
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
            "-ss", range.Start.ToString("0.######", CultureInfo.InvariantCulture), "-i", source,
            "-t", Math.Max(1d / FramesPerSecond, range.End - range.Start).ToString("0.######", CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-an", "-r", FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-fps_mode", "cfr"
        })
            decoder.ArgumentList.Add(argument);
        if (gifWidth != width || gifHeight != height)
        {
            decoder.ArgumentList.Add("-vf");
            decoder.ArgumentList.Add(FormattableString.Invariant($"scale={gifWidth}:{gifHeight}:flags=bilinear"));
        }
        foreach (string argument in new[] { "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1" })
            decoder.ArgumentList.Add(argument);

        token.ThrowIfCancellationRequested();
        using var process = Process.Start(decoder) ?? throw new InvalidOperationException("无法启动 GIF 解码器。");
        using var cancellation = token.Register(() => StopDecoder(process));
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
            {
                WriteHeader(writer, gifWidth, gifHeight);
                int stride = checked(gifWidth * 3);
                var pixels = new byte[checked(stride * gifHeight)];
                var previous = new byte[pixels.Length];
                using var still = new MemoryStream();
                long remaining = Math.Max(1L, checked((long)Math.Round((range.End - range.Start) * 100)));
                long pendingDelay = 0;
                var raw = process.StandardOutput.BaseStream;
                int read;
                while ((read = raw.ReadAtLeast(pixels, pixels.Length, throwOnEndOfStream: false)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (read != pixels.Length)
                        throw new InvalidDataException("GIF 解码器返回了不完整的画面。");
                    if (remaining == 0)
                        continue;

                    int delay = (int)Math.Min(FrameDelay, remaining);
                    remaining -= delay;
                    if (pendingDelay > 0 && pixels.AsSpan().SequenceEqual(previous))
                    {
                        pendingDelay += delay;
                        continue;
                    }
                    if (pendingDelay > 0)
                        WriteTimedFrame(writer, still, pendingDelay);
                    var frame = BitmapSource.Create(gifWidth, gifHeight, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
                    var palette = new BitmapPalette(frame, 256);
                    var indexed = new FormatConvertedBitmap(frame, PixelFormats.Indexed8, palette, 0);
                    var encoder = new GifBitmapEncoder { Palette = palette };
                    encoder.Frames.Add(BitmapFrame.Create(indexed));
                    still.SetLength(0);
                    encoder.Save(still);
                    (pixels, previous) = (previous, pixels);
                    pendingDelay = delay;
                }
                process.WaitForExit();
                var error = errors.GetAwaiter().GetResult().Trim();
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 || still.Length == 0)
                    throw new InvalidDataException(error.Length > 0 ? error : "所选时间范围没有可导出的画面。");

                // A partial final interval (or a trailing audio-only interval) holds
                // the final picture for the rest of the selected playback duration.
                WriteTimedFrame(writer, still, pendingDelay + remaining);
                writer.Write((byte)0x3B);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite);
        }
        finally
        {
            StopDecoder(process);
            RecordingFileStore.DeleteFileQuietly(staging);
        }
    }

    private static void WriteTimedFrame(BinaryWriter writer, MemoryStream still, long centiseconds)
    {
        while (centiseconds > 0)
        {
            int delay = (int)Math.Min(ushort.MaxValue, centiseconds);
            WriteFrame(writer, still.GetBuffer().AsSpan(0, (int)still.Length), delay);
            centiseconds -= delay;
        }
    }

    private static void WriteHeader(BinaryWriter output, int width, int height)
    {
        output.Write("GIF89a"u8);
        output.Write((ushort)width);
        output.Write((ushort)height);
        // Each frame has its own adaptive palette.
        output.Write(new byte[] { 0x70, 0, 0, 0x21, 0xFF, 0x0B });
        output.Write("NETSCAPE2.0"u8);
        output.Write(new byte[] { 3, 1, 0, 0, 0 }); // Loop indefinitely.
    }

    private static void WriteFrame(BinaryWriter output, ReadOnlySpan<byte> still, int delay)
    {
        // A WIC still contains one GIF image descriptor and its LZW sub-blocks.
        // Move its active palette into a local table, preserving interlacing.
        int flags = still[10];
        int offset = 13;
        int paletteSize = 3 << ((flags & 7) + 1);
        var palette = ReadOnlySpan<byte>.Empty;
        if ((flags & 0x80) != 0)
        {
            palette = still.Slice(offset, paletteSize);
            offset += paletteSize;
        }
        while (still[offset] == 0x21)
        {
            offset += 2;
            while (still[offset] != 0)
                offset += 1 + still[offset];
            offset++;
        }
        if (still[offset] != 0x2C)
            throw new InvalidDataException("GIF 编码器返回的图像描述符无效。");
        var descriptor = still.Slice(offset, 10);
        offset += descriptor.Length;
        int localFlags = descriptor[9];
        if ((localFlags & 0x80) != 0)
        {
            flags = localFlags;
            paletteSize = 3 << ((flags & 7) + 1);
            palette = still.Slice(offset, paletteSize);
            offset += paletteSize;
        }
        if (palette.IsEmpty || still[^1] != 0x3B)
            throw new InvalidDataException("GIF 编码器返回的调色板或结束标志无效。");

        output.Write(new byte[] { 0x21, 0xF9, 4, 4 }); // Keep the frame until the next one.
        output.Write((ushort)delay);
        output.Write((ushort)0);
        output.Write(descriptor[..9]);
        output.Write((byte)(0x80 | (localFlags & 0x40) | (flags & 7)));
        output.Write(palette);
        output.Write(still[offset..^1]);
    }

    private static void StopDecoder(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }
}
