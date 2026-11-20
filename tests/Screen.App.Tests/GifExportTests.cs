using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class GifExportTests
{
    [TestMethod]
    public void PaletteExportUsesSinglePathArgumentsInputTrimAndNoUpscaling()
    {
        var source = Path.Combine(Path.GetTempPath(), "screen source & sample.mp4");
        var target = Path.Combine(Path.GetTempPath(), "screen target & sample.gif");
        var (palette, encode) = RecordingPreviewWindow.BuildGifStartInfo("ffmpeg.exe", source, target, new RecordingTrimRange(1.25, 2.5), 20, 960);
        const string sampling = "[0:v:0]fps=fps=20:start_time=0:eof_action=pass,trim=end_frame=25,scale='min(iw,960)':-2:flags=lanczos,format=bgra";
        foreach (var info in new[] { palette, encode })
        {
            var options = info.ArgumentList.ToList();
            Assert.IsFalse(info.UseShellExecute);
            Assert.IsTrue(info.RedirectStandardOutput);
            Assert.IsTrue(info.RedirectStandardError);
            Assert.AreEqual(source, options[options.IndexOf("-i") + 1]);
            Assert.AreEqual("1.25", options[options.IndexOf("-ss") + 1]);
            Assert.DoesNotContain("-to", options, "Do not discard the timestamp that determines a held frame's duration.");
            Assert.Contains("-an", options);
            StringAssert.StartsWith(options[options.IndexOf("-filter_complex") + 1], sampling);
        }
        var paletteArgs = palette.ArgumentList.ToList();
        Assert.Contains("palettegen=stats_mode=diff", paletteArgs[paletteArgs.IndexOf("-filter_complex") + 1]);
        Assert.DoesNotContain("paletteuse", paletteArgs[paletteArgs.IndexOf("-filter_complex") + 1]);
        var args = encode.ArgumentList.ToList();
        Assert.AreEqual(paletteArgs[^1], args[args.LastIndexOf("-i") + 1]);
        Assert.AreEqual("16x16", args[args.IndexOf("-video_size") + 1]);
        Assert.AreEqual("bgra", args[args.IndexOf("-pixel_format") + 1]);
        Assert.AreEqual(target, args[^1]);
        Assert.AreEqual("gif", args[args.IndexOf("-c:v") + 1]);
        Assert.AreEqual("gif", args[args.LastIndexOf("-f") + 1]);
        Assert.AreEqual("0", args[args.IndexOf("-loop") + 1]);
        Assert.AreEqual(sampling + "[frames];[frames][1:v:0]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[out]",
            args[args.IndexOf("-filter_complex") + 1]);
    }

    [TestMethod]
    public void OriginalWidthAndEntireRecordingSkipScalingAndSeeking()
    {
        var (palette, encode) = RecordingPreviewWindow.BuildGifStartInfo("ffmpeg", "recording.mp4", "export.gif", null, 15, 0);
        foreach (var info in new[] { palette, encode })
        {
            var args = info.ArgumentList.ToList();
            Assert.DoesNotContain("-ss", args);
            Assert.DoesNotContain("-to", args);
            Assert.DoesNotContain("-frames:v", args);
            StringAssert.StartsWith(args[args.IndexOf("-filter_complex") + 1], "[0:v:0]fps=fps=15:eof_action=pass,format=bgra");
        }
    }

    [TestMethod]
    public void RejectsAnInvalidRangeRateWidthAndNonGifDestination()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RecordingPreviewWindow.ValidateGifDestination("a.mp4", "A.MP4"));
        Assert.ThrowsExactly<ArgumentException>(() => RecordingPreviewWindow.ValidateGifDestination("a.mp4", "other.mp4"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RecordingPreviewWindow.BuildGifStartInfo("ffmpeg", "a.mp4", "b.gif", new RecordingTrimRange(1, 1), 20, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RecordingPreviewWindow.BuildGifStartInfo("ffmpeg", "a.mp4", "b.gif", null, 31, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RecordingPreviewWindow.BuildGifStartInfo("ffmpeg", "a.mp4", "b.gif", null, 15, -1));
    }

    [TestMethod]
    [DataRow("Unrecognized option 'loop'.\nError splitting the argument list: Option not found", true)]
    [DataRow("Unknown encoder 'gif'", true)]
    [DataRow("No such filter: 'palettegen'", true)]
    [DataRow("[AVFilterGraph] No such filter: 'fps'\nError : Filter not found", true)]
    [DataRow("Filter 'paletteuse' not found", true)]
    [DataRow("Requested output format 'gif' is not known.", true)]
    [DataRow("Unrecognized option 'unknown'.\nOption not found", false)]
    [DataRow("paletteuse: Error reading input", false)]
    [DataRow("No space left on device", false)]
    public void OnlyMissingGifCapabilitiesTriggerTheUpdateMessage(string error, bool unsupported) =>
        Assert.AreEqual(unsupported, RecordingPreviewWindow.IsGifUnavailable(error));

    [TestMethod]
    public void GifClipboardFilesSurvivePreviewCleanupAndKeepTheirOwnExtension()
    {
        var root = Directory.CreateTempSubdirectory("screen-gif-lifetime-");
        try
        {
            var original = Path.Combine(root.FullName, "movie.mp4");
            var delivered = RecordingFileStore.ClipboardCopyPath(original, RecordingFormat.Gif);
            var cached = Path.Combine(root.FullName, $"gif-preview-{Guid.NewGuid():N}.gif");
            var palette = Path.ChangeExtension(cached, ".bgra");
            var unrelated = Path.Combine(root.FullName, "gif-preview-local.bgra");
            Assert.AreEqual(".gif", Path.GetExtension(delivered));
            Assert.AreEqual(root.FullName, Path.GetDirectoryName(delivered));
            File.WriteAllText(cached, "cached");
            File.WriteAllText(palette, "scratch");
            File.WriteAllText(unrelated, "not a Screen palette");
            RecordingFileStore.CopyAtomically(cached, delivered);
            var age = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(cached, age);
            File.SetLastWriteTimeUtc(delivered, age);
            File.SetLastWriteTimeUtc(palette, age);
            File.SetLastWriteTimeUtc(unrelated, age);
            Assert.AreEqual(2, RecordingTempCleaner.Clean(root.FullName, DateTime.UtcNow));
            Assert.IsFalse(File.Exists(cached));
            Assert.IsFalse(File.Exists(palette));
            Assert.IsTrue(File.Exists(unrelated));
            Assert.AreEqual("cached", File.ReadAllText(delivered));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
