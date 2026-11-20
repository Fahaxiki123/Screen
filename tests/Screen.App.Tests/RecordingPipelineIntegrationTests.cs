using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class RecordingPipelineIntegrationTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "Screen.Recording.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("bgra")]
    [DataRow("nv12")]
    public async Task EncodedRecordingSurvivesAtomicSaveAndDecodesCompletelyAsync(string pixelFormat)
    {
        const int Width = 64;
        const int Height = 64;
        const int FrameRate = 12;
        const int FrameCount = 12;
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        Assert.IsTrue(File.Exists(ffmpeg), "Integration acceptance requires the application's bundled FFmpeg.");
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "session")).FullName;
        var source = Path.Combine(scratch, "video.mp4");

        using (var pipeline = new RecordingPipeline(source, Width, Height, FrameRate, 400_000, pixelFormat))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (deadline.Token.Register(pipeline.Abort))
        {
            for (var index = 0; index < FrameCount; index++)
            {
                pipeline.WriteVideoFrame(CreateFrame(Width, Height, index, pixelFormat));
            }
            pipeline.FinalizeFile();
            Assert.IsFalse(deadline.IsCancellationRequested, "Encoding exceeded its deadline.");
        }

        var encoded = File.ReadAllBytes(source);
        Assert.IsTrue(encoded.Length > 0);
        var destination = Path.Combine(_root, "published.mp4");
        File.WriteAllBytes(destination, [1, 2, 3]);

        var saved = RecordingFileStore.Save(source, destination, scratch);

        Assert.IsTrue(saved.Saved, saved.Error);
        Assert.IsNull(saved.RecoveryDirectory);
        var actualPath = saved.SavedPath;
        Assert.IsNotNull(actualPath);
        Assert.AreEqual(Path.Combine(_root, "published (1).mp4"), actualPath);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        CollectionAssert.AreEqual(encoded, File.ReadAllBytes(actualPath));
        Assert.IsFalse(Directory.Exists(scratch));
        Assert.AreEqual(0, Directory.GetFiles(_root, ".screen-export-*.tmp").Length);

        // Decode every frame from the published MP4, rather than only probing
        // its header. -xerror turns damaged packets/frames into a failing exit.
        var (progress, errors) = await DecodeToNullAsync(ffmpeg, actualPath);
        Assert.AreEqual(string.Empty, errors.Trim(), errors);
        StringAssert.Contains(progress, "progress=end");
        var finalFrame = LastProgressValue(progress, "frame");
        var finalTime = LastProgressValue(progress, "out_time_us");
        Assert.AreEqual(FrameCount, int.Parse(finalFrame, CultureInfo.InvariantCulture));
        var durationSeconds = long.Parse(finalTime, CultureInfo.InvariantCulture) / 1_000_000.0;
        Assert.AreEqual(FrameCount / (double)FrameRate, durationSeconds, 1.0 / FrameRate,
            "Decoded timestamps must retain the complete recording duration.");
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task TwoSecondSilentRecordingExportsTheSelectedSecondAsAnimatedGifAsync()
    {
        const int width = 64, height = 48, sourceFps = 20, gifFps = 10;
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        var source = Path.Combine(_root, "silent.mp4");
        Assert.IsTrue(File.Exists(ffmpeg), "The bundled FFmpeg is required.");

        // Red/green/blue mark the first half-second, the desired second and the
        // last half-second. A moving stripe keeps otherwise identical frames distinct.
        using (var pipeline = new RecordingPipeline(source, width, height, sourceFps, 400_000, "bgra"))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (deadline.Token.Register(pipeline.Abort))
        {
            for (var i = 0; i < 2 * sourceFps; i++)
            {
                var pixels = new byte[width * height * 4];
                var activeChannel = i < 10 ? 2 : i < 30 ? 1 : 0;
                for (var p = 0; p < width * height; p++)
                {
                    var offset = p * 4;
                    pixels[offset + activeChannel] = 230;
                    pixels[offset + 3] = 255;
                    if (p % width / 4 == i % (width / 4))
                        pixels[offset + (activeChannel + 1) % 3] = 160;
                }
                pipeline.WriteVideoFrame(pixels);
            }
            pipeline.FinalizeFile();
            Assert.IsFalse(deadline.IsCancellationRequested, "Source recording did not finish in time.");
        }

        var gif = await ExportGifAsync(ffmpeg, source, new RecordingTrimRange(0.5, 1.5), gifFps, 32);
        Assert.AreEqual(gifFps, gif.Frames.Count, "Exactly the selected second should be sampled at 10 FPS.");
        var first = gif.Frames[0];
        Assert.AreEqual(32, first.PixelWidth);
        Assert.AreEqual(24, first.PixelHeight, "Downscaling must preserve the original 4:3 aspect ratio.");
        var delay = gif.Frames.Sum(frame =>
            Convert.ToInt32(((BitmapMetadata)frame.Metadata!).GetQuery("/grctlext/Delay"), CultureInfo.InvariantCulture));
        Assert.AreEqual(100, delay, "GIF frame delays are expressed in hundredths of a second.");

        var sample = new byte[4];
        new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0)
            .CopyPixels(new Int32Rect(2, 10, 1, 1), sample, 4, 0);
        Assert.IsTrue(sample[1] > 190 && sample[2] < 60,
            $"Expected the green part of the source, not the red intro: BGRA {string.Join(',', sample)}");
        Assert.IsTrue(File.Exists(source), "GIF export must not consume the recoverable MP4.");
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task GifTrimPreservesHeldVfrFramesAcrossBothRangeBoundariesAsync()
    {
        const int width = 64, height = 48;
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        var source = Path.Combine(_root, "sparse.mp4");
        using (var pipeline = new RecordingPipeline(source, width, height, 30, 400_000, "bgra"))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (deadline.Token.Register(pipeline.Abort))
        {
            // The green frame is held through the trim start (0.6s); blue is
            // held through the trim end (1.6s) until the next red frame at 1.9s.
            foreach (var (time, channel) in new[] { (0d, 2), (0.2, 1), (1.3, 0), (1.9, 2) })
            {
                var data = new byte[width * height * 4];
                for (var offset = 0; offset < data.Length; offset += 4)
                {
                    data[offset + channel] = 240;
                    data[offset + 3] = 255;
                }
                pipeline.WriteVideoFrame(data, time);
            }
            pipeline.FinalizeFile();
            Assert.IsFalse(deadline.IsCancellationRequested);
        }

        var gif = await ExportGifAsync(ffmpeg, source, new RecordingTrimRange(0.6, 1.6), 10, 0);
        var elapsed = 0;
        int? firstBlueTime = null;
        var seenGreen = false;
        foreach (var frame in gif.Frames)
        {
            // The GIF encoder may use tiny transparent difference images when
            // the frame has not changed. Whole-screen color changes are opaque.
            if (frame.PixelWidth == width && frame.PixelHeight == height)
            {
                var sample = new byte[4];
                new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0)
                    .CopyPixels(new Int32Rect(0, 0, 1, 1), sample, 4, 0);
                if (sample[3] != 0)
                {
                    Assert.IsLessThan((byte)60, sample[2], "Red frames fall outside both trim boundaries.");
                    if (sample[1] > 190)
                        seenGreen = true;
                    if (sample[0] > 190)
                        firstBlueTime ??= elapsed;
                }
            }
            elapsed += Convert.ToInt32(((BitmapMetadata)frame.Metadata!).GetQuery("/grctlext/Delay"), CultureInfo.InvariantCulture);
        }
        Assert.IsTrue(seenGreen, "A held frame beginning before the selected range must not be dropped.");
        Assert.IsNotNull(firstBlueTime, "The blue transition must appear inside the exported range.");
        Assert.AreEqual(70, firstBlueTime.Value, 10, "Color should switch 0.7 seconds after the trim starts.");
        Assert.AreEqual(100, elapsed, "The GIF must include the held tail, even though there is no new input sample there.");
    }

    private sealed class GifProgress(Action<(string Stage, double Fraction)> report) : IProgress<(string Stage, double Fraction)>
    {
        public void Report((string Stage, double Fraction) value) => report(value);
    }

    private async Task<GifBitmapDecoder> ExportGifAsync(string ffmpeg, string source, RecordingTrimRange range, int fps, int maxWidth)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var profile = new Screen.Core.RecordingProfile { GifFrameRate = fps, GifMaxWidth = maxWidth };
        string target;
        var updates = new List<(string Stage, double Fraction)>();
        try
        {
            target = await RecordingPreviewWindow.RenderGifScratchAsync(ffmpeg, source, _root, range, true, profile,
                deadline.Token, new GifProgress(updates.Add));
        }
        catch (NotSupportedException ex)
        {
            Assert.Inconclusive($"Bundled FFmpeg has no GIF support: {ex.Message}");
            throw;
        }

        Assert.IsTrue(updates.Any(p => p.Stage == "正在生成 GIF 调色板" && p.Fraction is > 0 and < 0.5));
        Assert.IsTrue(updates.Any(p => p.Stage == "正在编码 GIF" && p.Fraction > 0.5));
        Assert.IsTrue(updates.Select(p => p.Fraction).SequenceEqual(updates.Select(p => p.Fraction).Order()));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.bgra"), "A successful conversion must clean up its intermediate palette.");
        using var stream = File.OpenRead(target);
        return new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    [TestMethod]
    [DataRow("正在生成 GIF 调色板")]
    [DataRow("正在编码 GIF")]
    public async Task CancellingEitherGifPassKeepsOriginalAndRemovesTransientFilesAsync(string stage)
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        var source = Path.Combine(_root, "cancel.mp4");
        using (var input = new RecordingPipeline(source, 64, 64, 20, 400_000, "bgra"))
        {
            for (var i = 0; i < 40; i++)
                input.WriteVideoFrame(CreateFrame(64, 64, i, "bgra"));
            input.FinalizeFile();
        }
        var original = File.ReadAllBytes(source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var cancelAtProgress = new GifProgress(p =>
        {
            if (p.Stage == stage && p.Fraction > (stage == "正在编码 GIF" ? 0.5 : 0))
                canceled.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => RecordingPreviewWindow.RenderGifScratchAsync(ffmpeg, source, _root,
            new RecordingTrimRange(0, 2), false, new Screen.Core.RecordingProfile(), canceled.Token, cancelAtProgress));
        Assert.IsFalse(deadline.IsCancellationRequested, "User cancellation, not the test timeout, must stop each pass.");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(new[] { source }, Directory.GetFiles(_root));
    }

    [TestMethod]
    public void EncoderProbeHonorsPriorityOrderAndDimensionLimits()
    {
        Assert.IsFalse(FfmpegEncoderProbe.SupportsHardwareDimensions(64, 64));
        Assert.IsFalse(FfmpegEncoderProbe.SupportsHardwareDimensions(4098, 1080));
        Assert.IsTrue(FfmpegEncoderProbe.SupportsHardwareDimensions(256, 256));
        Assert.IsTrue(FfmpegEncoderProbe.SupportsHardwareDimensions(1920, 1080));

        Assert.AreEqual("h264_nvenc", FfmpegEncoderProbe.ProbePreferredEncoder("ffmpeg.exe", _ => true));
        Assert.AreEqual("h264_qsv", FfmpegEncoderProbe.ProbePreferredEncoder("ffmpeg.exe", name => name == "h264_qsv"));
        Assert.AreEqual("h264_amf", FfmpegEncoderProbe.ProbePreferredEncoder("ffmpeg.exe", name => name == "h264_amf"));
        Assert.AreEqual("libx264", FfmpegEncoderProbe.ProbePreferredEncoder("ffmpeg.exe", _ => false));
    }

    [TestMethod]
    [DataRow("nv12")]
    [DataRow("bgra")]
    public async Task HardwareCapableDimensionsEncodeAndDecodeCleanlyAsync(string pixelFormat)
    {
        const int Width = 256;
        const int Height = 256;
        const int FrameRate = 15;
        const int FrameCount = 10;
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        var destination = Path.Combine(_root, $"hw-{pixelFormat}.mp4");

        using (var pipeline = new RecordingPipeline(destination, Width, Height, FrameRate, 1_500_000, pixelFormat))
        {
            for (var index = 0; index < FrameCount; index++)
            {
                pipeline.WriteVideoFrame(CreateFrame(Width, Height, index, pixelFormat));
            }
            pipeline.FinalizeFile();
        }

        var (progress, errors) = await DecodeToNullAsync(ffmpeg, destination);
        Assert.AreEqual(string.Empty, errors.Trim(), errors);
        Assert.AreEqual(FrameCount, int.Parse(LastProgressValue(progress, "frame"), CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task FailingHardwareEncoderFallsBackToSoftwareWithoutDroppingStartupFramesAsync()
    {
        const int Width = 256;
        const int Height = 256;
        const int FrameRate = 12;
        const int FrameCount = 6;
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        var destination = Path.Combine(_root, "fallback.mp4");

        using (var pipeline = new RecordingPipeline(
                   destination,
                   Width,
                   Height,
                   FrameRate,
                   800_000,
                   "nv12",
                   encoderOverride: "nonexistent_hw_encoder"))
        {
            for (var index = 0; index < FrameCount; index++)
            {
                pipeline.WriteVideoFrame(CreateFrame(Width, Height, index, "nv12"));
            }
            pipeline.FinalizeFile();
            Assert.AreEqual("libx264", pipeline.SelectedEncoder);
        }

        var (progress, errors) = await DecodeToNullAsync(ffmpeg, destination);
        Assert.AreEqual(string.Empty, errors.Trim(), errors);
        Assert.AreEqual(FrameCount, int.Parse(LastProgressValue(progress, "frame"), CultureInfo.InvariantCulture));
    }

    private static byte[] CreateFrame(int width, int height, int index, string pixelFormat)
    {
        var pixels = width * height;
        if (pixelFormat == "nv12")
        {
            var frame = new byte[pixels * 3 / 2];
            frame.AsSpan(0, pixels).Fill((byte)(32 + index * 12));
            frame.AsSpan(pixels).Fill(128);
            return frame;
        }
        var bgra = new byte[pixels * 4];
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            bgra[pixel * 4] = (byte)(index * 16);
            bgra[pixel * 4 + 1] = (byte)(pixel % width * 4);
            bgra[pixel * 4 + 2] = (byte)(pixel / width * 4);
            bgra[pixel * 4 + 3] = 255;
        }
        return bgra;
    }

    private static async Task<(string Progress, string Errors)> DecodeToNullAsync(string ffmpeg, string path)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostdin", "-loglevel", "error", "-xerror", "-nostats",
                     "-progress", "pipe:1", "-i", path, "-map", "0:v:0", "-an", "-f", "null", "-"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg decoder did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var progress = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(deadline.Token);
            var output = await progress;
            var diagnostic = await errors;
            Assert.AreEqual(0, process.ExitCode, diagnostic);
            return (output, diagnostic);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static string LastProgressValue(string progress, string key)
    {
        var prefix = key + "=";
        var line = progress.Split('\n').LastOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        Assert.IsNotNull(line, $"FFmpeg progress did not include {key}: {progress}");
        return line[prefix.Length..].Trim();
    }
}
