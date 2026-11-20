using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class GlassBackdropProviderTests
{
    [TestMethod]
    public async Task PartialEdgeBlocksUseActualPixelCountsAndIgnoreRowPaddingAsync()
    {
        const int stride = 24;
        var pixels = Enumerable.Repeat((byte)230, stride * 5).ToArray();
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 5; x++)
            {
                var offset = y * stride + x * 4;
                pixels.AsSpan(offset, 3).Fill((byte)(x + 5 * y));
                pixels[offset + 3] = 255;
            }
        }
        var original = (byte[])pixels.Clone();
        // Isolate the 4x4 box sampler by using a scale whose rounded blur radius is zero.
        var result = await GlassBackdropProvider.CreateAsync(Frame(pixels, 5, 5, stride), 0.1, CancellationToken.None);
        Assert.AreEqual(2, result.PixelWidth);
        Assert.AreEqual(2, result.PixelHeight);
        Assert.IsTrue(result.IsFrozen);
        Assert.AreEqual(96d, result.DpiX);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9, 255, 12, 12, 12, 255, 22, 22, 22, 255, 24, 24, 24, 255 }, ReadPixels(result));
        CollectionAssert.AreEqual(original, pixels);
    }

    [TestMethod]
    [DataRow(100, 120, 140, 84, 118, 152)]
    [DataRow(0, 100, 255, 0, 82, 255)]
    public async Task SaturationUsesLuminanceWeightsAndClampsBgraAsync(int b, int g, int r, int expectedB, int expectedG, int expectedR)
    {
        var pixels = new byte[] { (byte)b, (byte)g, (byte)r, 245 };
        var result = await GlassBackdropProvider.CreateAsync(Frame(pixels, 1, 1, 4), 2, CancellationToken.None);
        CollectionAssert.AreEqual(new byte[] { (byte)expectedB, (byte)expectedG, (byte)expectedR, 245 }, ReadPixels(result));
    }

    [TestMethod]
    public async Task ThreeSeparablePassesSpreadAnImpulseInBothDirectionsAtTheScaledRadiusAsync()
    {
        const int size = 36;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var offset = (y * size + x) * 4;
                if (x / 4 == 4 && y / 4 == 4)
                {
                    pixels.AsSpan(offset, 3).Fill(255);
                }
                pixels[offset + 3] = 255;
            }
        }
        var frame = Frame(pixels, size, size, size * 4);
        var result = ReadPixels(await GlassBackdropProvider.CreateAsync(frame, 0.31, CancellationToken.None));
        // Three radius-one box convolutions have coefficients [1,3,6,7,6,3,1]/27 per axis.
        byte Gray(int x, int y) => result[(y * 9 + x) * 4];
        Assert.AreEqual((byte)17, Gray(4, 4));
        Assert.AreEqual((byte)15, Gray(5, 4));
        Assert.AreEqual((byte)15, Gray(4, 5));
        Assert.AreEqual((byte)3, Gray(2, 2));
        Assert.AreEqual((byte)0, Gray(0, 0));
        var wider = ReadPixels(await GlassBackdropProvider.CreateAsync(frame, 0.62, CancellationToken.None));
        Assert.IsLessThan(Gray(4, 4), wider[(4 * 9 + 4) * 4]);
    }

    [TestMethod]
    public async Task CanceledRequestDoesNotProduceABitmapAsync()
    {
        var frame = Frame(new byte[4], 1, 1, 4);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => GlassBackdropProvider.CreateAsync(frame, 1, cancel.Token));
    }

    private static CaptureFrame Frame(byte[] pixels, int width, int height, int stride) =>
        new(pixels, width, height, stride, 144, 144, new PixelRect(-100, 17, width, height), CaptureColorSpace.Srgb, DateTimeOffset.UnixEpoch);

    private static byte[] ReadPixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
