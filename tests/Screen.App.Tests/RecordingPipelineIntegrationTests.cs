using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        CollectionAssert.AreEqual(encoded, File.ReadAllBytes(destination));
        Assert.IsFalse(Directory.Exists(scratch));
        Assert.AreEqual(0, Directory.GetFiles(_root, ".screen-export-*.tmp").Length);

        // Decode every frame from the published MP4, rather than only probing
        // its header. -xerror turns damaged packets/frames into a failing exit.
        var (progress, errors) = await DecodeToNullAsync(ffmpeg, destination);
        Assert.AreEqual(string.Empty, errors.Trim(), errors);
        StringAssert.Contains(progress, "progress=end");
        var finalFrame = LastProgressValue(progress, "frame");
        var finalTime = LastProgressValue(progress, "out_time_us");
        Assert.AreEqual(FrameCount, int.Parse(finalFrame, CultureInfo.InvariantCulture));
        var durationSeconds = long.Parse(finalTime, CultureInfo.InvariantCulture) / 1_000_000.0;
        Assert.AreEqual(FrameCount / (double)FrameRate, durationSeconds, 1.0 / FrameRate,
            "Decoded timestamps must retain the complete recording duration.");
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
