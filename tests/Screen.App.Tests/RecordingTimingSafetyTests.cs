using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace Screen.App.Tests;

[TestClass]
public sealed class RecordingTimingSafetyTests
{
    [TestMethod]
    [DataRow("bgra")]
    [DataRow("nv12")]
    public async Task CaptureTimestampsPreserveGapsPauseExclusionAndFinalHeldFrame(string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "Screen.Timing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "timed.mp4");
            long ticks = 0;
            var timeline = new RecordingTimeline(() => ticks);
            timeline.Start();
            using (var pipeline = new RecordingPipeline(path, 64, 64, 30, 400_000, format, "libx264"))
            {
                pipeline.WriteVideoFrame(Frame(format, 0), timeline.ElapsedSeconds);
                ticks += Stopwatch.Frequency / 10;
                pipeline.WriteVideoFrame(Frame(format, 1), timeline.ElapsedSeconds);
                timeline.Pause();
                ticks += Stopwatch.Frequency * 5;
                timeline.Resume();
                // No captured frames during a 3.1s stall. Arrival timing must
                // not compress the actual capture timestamps to three ticks.
                ticks += Stopwatch.Frequency * 31 / 10;
                pipeline.WriteVideoFrame(Frame(format, 2), timeline.ElapsedSeconds);
                ticks += Stopwatch.Frequency * 19 / 10;
                pipeline.SetEndTimestamp(timeline.ElapsedSeconds);
                pipeline.FinalizeFile();
            }
            var (frames, errors) = await DecodePreviewAsync(path);
            Assert.AreEqual(153, frames.Length / (64 * 64 * 4), errors);
            StringAssert.Contains(errors, "Duration: 00:00:05.10");
            Assert.IsTrue(frames[0] > 230 && frames[2] < 20, "First capture must remain the first preview frame.");
            var last = frames.Length - 64 * 64 * 4;
            Assert.IsTrue(frames[last + 2] > 230 && frames[last] < 20, "The last frame must be held through the stop boundary.");
            AssertFrameColor(frames, 30, 1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnexpectedAudioStopIsRetainedEvenWithoutAnException(bool withException)
    {
        var capture = new TestCapture();
        using var output = new MemoryStream();
        using var track = new RecordingAudioTrack(capture, "system-audio.raw", "s16le", 48000, 2,
            new RecordingStopSignal(), output);
        capture.Emit(new byte[1920]);
        track.Paused = true;
        capture.End(withException ? new IOException("device disconnected") : null);
        Assert.IsNotNull(track.Fault, "Pausing only discards samples; an unsolicited device stop is still a fault.");
        track.Paused = false;
        capture.Emit(new byte[1920]);
        Assert.AreEqual(1920L, track.BytesWritten);
        using var service = new RecordingAudioService([track]);
        StringAssert.Contains(service.Warning!, "系统声音采集中断");
    }

    [TestMethod]
    public void DeliberateAudioStopDoesNotProduceAnUnexpectedStopWarning()
    {
        var capture = new TestCapture();
        using var track = new RecordingAudioTrack(capture, "microphone.raw", "s16le", 48000, 2,
            new RecordingStopSignal(), new MemoryStream());
        track.Stop();
        Assert.IsNull(track.Fault);
    }

    [TestMethod]
    public void AudioCallbackWriteFailureBecomesAVisibleFault()
    {
        var capture = new TestCapture();
        using var track = new RecordingAudioTrack(capture, "microphone.raw", "s16le", 48000, 2,
            new RecordingStopSignal(), new FailingStream());
        capture.Emit(new byte[1920]);
        Assert.IsInstanceOfType<IOException>(track.Fault);
        using var service = new RecordingAudioService([track]);
        StringAssert.Contains(service.Warning!, "disk full");
    }

    [TestMethod]
    public void AudioStartupFailureDisposesTheDeviceAndRetainsItsReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "Screen.AudioStart.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var capture = new TestCapture { FailStart = true };
            var track = RecordingAudioTrack.TryCreate(() => capture, Path.Combine(root, "mic.raw"),
                new RecordingStopSignal(), null, out var warning);
            Assert.IsNull(track);
            Assert.IsTrue(capture.Disposed);
            StringAssert.Contains(warning!, "microphone denied");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void SilentFinalPublicationRetainsFaststartWithoutInventingAudioInputs()
    {
        var info = RecordingController.BuildMuxStartInfo("ffmpeg.exe", "video.mp4", [], 5.1, "final.mp4");
        var args = info.ArgumentList.ToArray();
        CollectionAssert.Contains(args, "+faststart");
        CollectionAssert.Contains(args, "-an");
        CollectionAssert.DoesNotContain(args, "amix");
        Assert.AreEqual(1, args.Count(item => item == "-i"));
        Assert.IsTrue(RecordingProcessRunner.FinalizationTimeout(20L * 1024 * 1024 * 1024) > TimeSpan.FromSeconds(30));
        Assert.IsTrue(RecordingProcessRunner.FinalizationTimeout(long.MaxValue) <= TimeSpan.FromMinutes(30));
    }

    [TestMethod]
    public void GdiNv12ConversionAveragesEachTwoByTwoChromaBlock()
    {
        // Red / green / blue / white have neutral average chroma. Sampling
        // only the final pixel or overwriting UV per pixel would fail this.
        byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255];
        var nv12 = new byte[6];
        RecordingColorConversion.CopyBgraToNv12(bgra, nv12, 2, 2);
        CollectionAssert.AreEqual(new byte[] { 82, 144, 41, 235, 128, 128 }, nv12);
    }

    [TestMethod]
    [DataRow(0, 0, 0, 16, 128, 128)]
    [DataRow(128, 128, 128, 126, 128, 128)]
    [DataRow(255, 255, 255, 235, 128, 128)]
    [DataRow(255, 0, 0, 82, 90, 240)]
    [DataRow(0, 255, 0, 144, 54, 34)]
    [DataRow(0, 0, 255, 41, 240, 110)]
    public void GdiNv12ConversionUsesTheDeclaredLimitedRangeMatrix(int r, int g, int b, int y, int u, int v)
    {
        var bgra = new byte[16];
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            bgra[offset] = (byte)b;
            bgra[offset + 1] = (byte)g;
            bgra[offset + 2] = (byte)r;
            bgra[offset + 3] = 255;
        }
        var nv12 = new byte[6];
        RecordingColorConversion.CopyBgraToNv12(bgra, nv12, 2, 2);
        CollectionAssert.AreEqual(new byte[] { (byte)y, (byte)y, (byte)y, (byte)y, (byte)u, (byte)v }, nv12);
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(10)]
    [DataRow(12)]
    [DataRow(14)]
    [DataRow(3840)]
    public void WideNv12RowsMatchIndividualScalarChromaBlocks(int width)
    {
        const int height = 6;
        var bgra = new byte[width * height * 4];
        new Random(731).NextBytes(bgra);
        var expected = new byte[width * height * 3 / 2];
        var block = new byte[16];
        var encoded = new byte[6];
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                var first = y * width + x;
                var second = first + width;
                bgra.AsSpan(first * 4, 8).CopyTo(block);
                bgra.AsSpan(second * 4, 8).CopyTo(block.AsSpan(8));
                // A 2x2 input always takes the scalar path, including on AVX2 machines.
                RecordingColorConversion.CopyBgraToNv12(block, encoded, 2, 2);
                encoded.AsSpan(0, 2).CopyTo(expected.AsSpan(first));
                encoded.AsSpan(2, 2).CopyTo(expected.AsSpan(second));
                encoded.AsSpan(4, 2).CopyTo(expected.AsSpan(width * height + y / 2 * width + x));
            }
        }
        var actual = new byte[expected.Length];
        RecordingColorConversion.CopyBgraToNv12(bgra, actual, width, height);
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void ConvertedFallbackDesktopShowsExactlyOneMovingCursorAndNoneWhenHidden()
    {
        const int width = 3840;
        const int height = 4;
        var frames = new ScreenRecorder.FrameBuffer(width, height, true);
        RecordingColorConversion.CopyBgraToNv12(new byte[width * height * 4], frames.Desktop, width, height);
        var clean = frames.Desktop.ToArray();
        var cursor = new ScreenRecorder.CursorBitmap([255, 255, 255, 255], new Screen.Core.PixelRect(0, 1, 1, 1));
        foreach (var x in new[] { 7, 7, 8, width - 1 })
        {
            var visible = frames.GetEncodingFrame(cursor with { Bounds = cursor.Bounds with { X = x } });
            Assert.AreEqual((byte)235, visible[width + x]);
            Assert.AreEqual(1, visible.AsSpan(0, width * height).Count((byte)235));
        }
        CollectionAssert.AreEqual(clean, frames.GetEncodingFrame(null));
    }

    [TestMethod]
    public async Task Nv12RangeSurvivesActualFfmpegEncodingAndDecoding()
    {
        var root = Path.Combine(Path.GetTempPath(), "Screen.Nv12Range.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "gray.mp4");
            byte[] shades = [0, 128, 255];
            using (var pipeline = new RecordingPipeline(path, 64, 64, 30, 400_000, "nv12", "libx264"))
            {
                for (var index = 0; index < shades.Length; index++)
                {
                    var bgra = new byte[64 * 64 * 4];
                    for (var offset = 0; offset < bgra.Length; offset += 4)
                    {
                        bgra[offset] = bgra[offset + 1] = bgra[offset + 2] = shades[index];
                        bgra[offset + 3] = 255;
                    }
                    var nv12 = new byte[64 * 64 * 3 / 2];
                    RecordingColorConversion.CopyBgraToNv12(bgra, nv12, 64, 64);
                    pipeline.WriteVideoFrame(nv12, index / 10.0);
                }
                pipeline.SetEndTimestamp(0.3);
                pipeline.FinalizeFile();
            }
            var (frames, errors) = await DecodePreviewAsync(path);
            Assert.AreEqual(9, frames.Length / (64 * 64 * 4), errors);
            StringAssert.Contains(errors, "smpte170m");
            for (var index = 0; index < shades.Length; index++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var decoded = frames[index * 3 * 64 * 64 * 4 + channel];
                    Assert.IsTrue(Math.Abs(decoded - shades[index]) <= 3,
                        $"NV12 shade {shades[index]} decoded as {decoded}; full/limited range must not drift.");
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(2.0, 4.0, 30.0, 60, true)]
    [DataRow(2.137, 4.381, 30.0, 68, false)]
    [DataRow(4.0, 4.5, 30.0, 15, false)]
    [DataRow(2.0, 2.8, 30.0, 24, false)]
    [DataRow(2.0, 4.0, 60.0, 120, false)]
    public async Task TrimKeepsTheFrameCoveringItsStartAndAccuratelySeeksAudio(
        double start, double end, double frameRate, int expectedFrames, bool withAudio)
    {
        var root = Path.Combine(Path.GetTempPath(), "Screen.VfrTrim.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mp4");
            using (var pipeline = new RecordingPipeline(source, 64, 64, 30, 400_000, "bgra", "libx264"))
            {
                pipeline.WriteVideoFrame(Frame("bgra", 0), 0);
                pipeline.WriteVideoFrame(Frame("bgra", 1), 0.1);
                pipeline.WriteVideoFrame(Frame("bgra", 2), 3.2);
                pipeline.SetEndTimestamp(5.1);
                pipeline.FinalizeFile();
            }
            if (withAudio)
            {
                var raw = Path.Combine(root, "system-audio.raw");
                var capture = new TestCapture();
                using var audio = new RecordingAudioTrack(capture, raw, "s16le", 48000, 1, new RecordingStopSignal());
                using var pcm = new MemoryStream();
                using (var writer = new BinaryWriter(pcm, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    // A chirp makes a wrong starting time detectable, unlike a
                    // constant tone whose periods can hide a seek offset.
                    for (var n = 0; n < 48000 * 5.1; n++)
                    {
                        var t = n / 48000.0;
                        writer.Write((short)(12000 * Math.Sin(2 * Math.PI * (200 * t + 40 * t * t))));
                    }
                }
                capture.Emit(pcm.ToArray());
                audio.Stop();
                var muxed = Path.Combine(root, "source-audio.mp4");
                RecordingProcessRunner.Run(RecordingController.BuildMuxStartInfo(FfmpegLocator.Resolve(), source, [audio], 5.1, muxed),
                    TimeSpan.FromSeconds(20), CancellationToken.None);
                source = muxed;
            }
            var trimmed = Path.Combine(root, "trimmed.mp4");
            RecordingProcessRunner.Run(RecordingPreviewWindow.BuildTrimStartInfo(FfmpegLocator.Resolve(), source, trimmed,
                new RecordingTrimRange(start, end), frameRate), TimeSpan.FromSeconds(20), CancellationToken.None);
            var (frames, errors) = await DecodePreviewAsync(trimmed, frameRate);
            Assert.AreEqual(expectedFrames, frames.Length / (64 * 64 * 4), errors);
            AssertFrameColor(frames, 0, start < 3.2 ? 1 : 2);
            AssertFrameColor(frames, expectedFrames - 1, end <= 3.2 ? 1 : 2);
            if (start < 3.2 && end > 3.2)
            {
                var transition = (3.2 - start) * frameRate;
                AssertFrameColor(frames, Math.Max(0, (int)Math.Floor(transition) - 1), 1);
                AssertFrameColor(frames, Math.Min(expectedFrames - 1, (int)Math.Ceiling(transition) + 1), 2);
            }
            if (withAudio)
            {
                var reference = Path.Combine(root, "reference.m4a");
                var info = new ProcessStartInfo(FfmpegLocator.Resolve())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", start.ToString(CultureInfo.InvariantCulture),
                    "-i", source, "-t", (end - start).ToString(CultureInfo.InvariantCulture), "-map", "0:a:0", "-c:a", "aac", "-b:a", "192k", reference })
                    info.ArgumentList.Add(arg);
                RecordingProcessRunner.Run(info, TimeSpan.FromSeconds(20), CancellationToken.None);
                var actualAudio = await DecodeAudioAsync(trimmed);
                var referenceAudio = await DecodeAudioAsync(reference);
                Assert.AreEqual(96000 * 2, actualAudio.Length);
                CollectionAssert.AreEqual(referenceAudio, actualAudio, "Video preroll must not retain earlier audio or shift the selected soundtrack.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void AssertFrameColor(byte[] frames, int frame, int channel)
    {
        var offset = checked(frame * 64 * 64 * 4);
        Assert.IsTrue(frames[offset + channel] > 230 && frames[offset + (channel == 1 ? 2 : 1)] < 20,
            $"Frame {frame} must contain the held source color, not a future frame.");
    }

    private static async Task<byte[]> DecodeAudioAsync(string path)
    {
        var (bytes, _) = await CaptureOutputAsync(["-hide_banner", "-loglevel", "error", "-i", path,
            "-map", "0:a:0", "-ar", "48000", "-ac", "1", "-f", "s16le", "pipe:1"]);
        return bytes;
    }

    private static byte[] Frame(string format, int index)
    {
        var frame = new byte[format == "bgra" ? 64 * 64 * 4 : 64 * 64 * 3 / 2];
        if (format == "bgra")
        {
            for (var offset = 0; offset < frame.Length; offset += 4)
            {
                frame[offset + index] = 255;
                frame[offset + 3] = 255;
            }
        }
        else
        {
            RecordingColorConversion.CopyBgraToNv12(Frame("bgra", index), frame, 64, 64);
        }
        return frame;
    }

    private static Task<(byte[] Frames, string Errors)> DecodePreviewAsync(string path, double frameRate = 30) =>
        CaptureOutputAsync(["-hide_banner", "-i", path, "-r", frameRate.ToString(CultureInfo.InvariantCulture),
            "-fps_mode", "cfr", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"]);

    private static async Task<(byte[] Frames, string Errors)> CaptureOutputAsync(string[] arguments)
    {
        var info = new ProcessStartInfo(FfmpegLocator.Resolve())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        var copying = process.StandardOutput.BaseStream.CopyToAsync(output);
        try
        { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        await copying;
        var text = await errors;
        Assert.AreEqual(0, process.ExitCode, text);
        return (output.ToArray(), text);
    }

    private sealed class TestCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(48000, 16, 2);
        public bool FailStart { get; init; }
        public bool Disposed { get; private set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void Emit(byte[] bytes) => DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        public void End(Exception? exception) => RecordingStopped?.Invoke(this, new StoppedEventArgs(exception));
        public void StartRecording() { if (FailStart) { throw new IOException("microphone denied"); } }
        public void StopRecording() => End(null);
        public void Dispose() => Disposed = true;
    }

    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");
    }
}
