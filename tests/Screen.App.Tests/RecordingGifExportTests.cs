using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class RecordingGifExportTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Screen.Gif.Tests", Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_root);

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task TrimmedGifLoopsWithCorrectFramesAndClipboardLifetimeAsync()
    {
        const int size = 64;
        var source = Path.Combine(_root, "source.mp4");
        using (var video = new RecordingPipeline(source, size, size, 10, 400000, "bgra"))
        {
            var pixels = new byte[size * size * 4];
            for (int frame = 0; frame < 12; frame++)
            {
                // The red section precedes the trim. Green and blue must both survive.
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = (byte)(frame >= 6 ? 240 : 5);
                    pixels[i + 1] = (byte)(frame is >= 3 and < 6 ? 240 : 5);
                    pixels[i + 2] = (byte)(frame < 3 ? 240 : 5);
                    pixels[i + 3] = 255;
                }
                video.WriteVideoFrame(pixels);
            }
            video.FinalizeFile();
        }
        var range = new RecordingTrimRange(.30, .97);
        var temp = Path.Combine(_root, "trim.gif");
        string clipboard;
        using (var cache = new RecordingExportCache())
        {
            var exported = await cache.GetAsync(range, async (requested, token) =>
            {
                await RecordingGifExporter.ExportAsync(source, temp, size, size, requested, token);
                return temp;
            });
            clipboard = RecordingFileStore.CreateClipboardCopy(exported, source);
        }

        Assert.IsFalse(File.Exists(temp));
        Assert.AreEqual(".gif", Path.GetExtension(clipboard));
        var bytes = File.ReadAllBytes(clipboard);
        Assert.IsTrue(bytes.AsSpan().StartsWith("GIF89a"u8));
        Assert.IsTrue(bytes.AsSpan().IndexOf("NETSCAPE2.0\x03\x01\x00\x00\x00"u8) >= 0, "GIF must loop indefinitely.");
        using var stream = new MemoryStream(bytes);
        var gif = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.IsTrue(gif.Frames.Count >= 2, "The GIF must be genuinely animated.");
        var delays = gif.Frames.Select(f => (ushort)((BitmapMetadata)f.Metadata).GetQuery("/grctlext/Delay")).ToArray();
        Assert.IsTrue(delays.All(delay => delay > 0));
        Assert.AreEqual(67, delays.Sum(d => (int)d), "GIF frame delays must span the selected interval.");
        var first = Bgr(gif.Frames[0]);
        var last = Bgr(gif.Frames[^1]);
        Assert.IsTrue(first[1] > 180 && first[0] < 60 && first[2] < 60, "Trim must start on green.");
        Assert.IsTrue(last[0] > 180 && last[1] < 60 && last[2] < 60, "Trim must end on blue.");
        Assert.AreEqual(0, Directory.GetFiles(_root, ".screen-export-*.tmp").Length);
    }

    private static byte[] Bgr(BitmapSource frame)
    {
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        var pixel = new byte[3];
        bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 3, 0);
        return pixel;
    }

    [TestMethod]
    public async Task FailedAndCanceledExportsLeaveExistingFileUntouchedAsync()
    {
        var corrupt = Path.Combine(_root, "corrupt.mp4");
        File.WriteAllBytes(corrupt, [9, 8, 7]);
        var destination = Path.Combine(_root, "existing.gif");
        byte[] previous = [1, 2, 3];
        File.WriteAllBytes(destination, previous);
        var range = new RecordingTrimRange(0, 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => RecordingGifExporter.ExportAsync(corrupt, destination, 64, 64, range, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(() => RecordingGifExporter.ExportAsync(corrupt, destination, 64, 64, range, new CancellationToken(true)));

        CollectionAssert.AreEqual(previous, File.ReadAllBytes(destination));
        Assert.AreEqual(0, Directory.GetFiles(_root, ".screen-export-*.tmp").Length);
    }
}
