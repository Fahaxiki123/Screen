using Screen.Core;

namespace Screen.Core.Tests;

[TestClass]
public sealed class ScrollStitchRegressionTests
{
    [TestMethod]
    [DataRow(61)]
    [DataRow(79)]
    [DataRow(103)]
    public void SharpContentAtOddScrollOffsetsPreservesEveryPixel(int distance)
    {
        var source = Pattern(192, 420);
        AssertExactJoin(source, 240, distance);
    }

    [TestMethod]
    public void ThinDocumentLinesAreNotLostBetweenSampledRows()
    {
        var source = Document(384, 1200, lowContrast: false);
        AssertExactJoin(source, 720, 240);
    }

    [TestMethod]
    public void LowContrastDocumentStillUsesTheExactOverlap()
    {
        var source = Document(384, 1200, lowContrast: true);
        AssertExactJoin(source, 720, 240);
    }

    [TestMethod]
    public void UpwardMovementInLowContrastTextCannotBecomeDuplicatedDownwardContent()
    {
        var source = Document(384, 1200, lowContrast: true);
        var first = source.Crop(new PixelRect(0, 240, source.Width, 720));
        var movedUp = source.Crop(new PixelRect(0, 0, source.Width, 720));

        var result = new VerticalScrollStitcher().Stitch([first, movedUp]);

        Assert.IsFalse(result.IsComplete);
        StringAssert.Contains(result.FailureReason, "moved upward");
        CollectionAssert.AreEqual(first.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public void FixedFooterBesideAWhiteMarginInTheFirstFrameIsStillRecognized()
    {
        const int width = 192;
        const int height = 240;
        const int distance = 80;
        const int footer = 23;
        var source = Pattern(width, height + distance);
        // Both viewports have a white margin above the footer; only the second
        // starts a text line immediately above it. The band edge must be read
        // from both frames, otherwise the first frame's white rows hide it.
        PaintRows(source, height - footer - 8, 8, 255);
        PaintRows(source, height + distance - footer - 4, 4, 255);
        var first = source.Crop(new PixelRect(0, 0, width, height));
        var second = source.Crop(new PixelRect(0, distance, width, height));
        PaintBands(first, 0, footer);
        PaintBands(second, 0, footer);
        PaintBands(source, 0, footer);

        var result = new VerticalScrollStitcher().Stitch([first, second]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        CollectionAssert.AreEqual(source.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    [DataRow(21, 0)]
    [DataRow(0, 23)]
    [DataRow(21, 23)]
    public void OddHeightFixedBandsPreserveTheFirstAndLastContentRows(int header, int footer)
    {
        const int width = 192;
        const int frameHeight = 240;
        const int distance = 80;
        var source = Pattern(width, frameHeight + distance);
        if (header > 0)
        {
            PaintRows(source, header, 2, 255);
        }
        if (footer > 0)
        {
            PaintRows(source, frameHeight - footer - 2, 2, 255);
        }
        var first = source.Crop(new PixelRect(0, 0, width, frameHeight));
        var second = source.Crop(new PixelRect(0, distance, width, frameHeight));
        PaintBands(first, header, footer);
        PaintBands(second, header, footer);
        PaintBands(source, header, footer);

        var result = new VerticalScrollStitcher().Stitch([first, second]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        Assert.AreEqual(source.Height, result.Image.Height);
        CollectionAssert.AreEqual(source.Pixels, result.Image.Pixels);
    }

    private static void AssertExactJoin(PixelBuffer source, int frameHeight, int distance)
    {
        var first = source.Crop(new PixelRect(0, 0, source.Width, frameHeight));
        var second = source.Crop(new PixelRect(0, distance, source.Width, frameHeight));

        var result = new VerticalScrollStitcher().Stitch([first, second]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        Assert.AreEqual(frameHeight + distance, result.Image.Height);
        CollectionAssert.AreEqual(
            source.Crop(new PixelRect(0, 0, source.Width, frameHeight + distance)).Pixels,
            result.Image.Pixels);
    }

    private static PixelBuffer Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(816);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)random.Next(256);
            pixels[offset + 1] = (byte)random.Next(256);
            pixels[offset + 2] = (byte)random.Next(256);
            pixels[offset + 3] = 255;
        }
        return new PixelBuffer(pixels, width, height, width * 4);
    }

    private static PixelBuffer Document(int width, int height, bool lowContrast)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        var random = new Random(934);
        for (var y = 3; y < height; y += 12)
        {
            var length = random.Next(width / 3, width - 24);
            for (var x = 20; x < length; x++)
            {
                var value = lowContrast ? (byte)250 : (byte)40;
                var offset = (y * width + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
            }
        }
        return new PixelBuffer(pixels, width, height, width * 4);
    }

    private static void PaintBands(PixelBuffer frame, int header, int footer)
    {
        for (var y = 0; y < frame.Height; y++)
        {
            if (y >= header && y < frame.Height - footer)
            {
                continue;
            }
            for (var x = 0; x < frame.Width; x++)
            {
                var offset = y * frame.Stride + x * 4;
                frame.Pixels[offset] = 20;
                frame.Pixels[offset + 1] = 60;
                frame.Pixels[offset + 2] = 100;
            }
        }
    }

    private static void PaintRows(PixelBuffer frame, int top, int height, byte value)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var offset = y * frame.Stride + x * 4;
                frame.Pixels[offset] = value;
                frame.Pixels[offset + 1] = value;
                frame.Pixels[offset + 2] = value;
            }
        }
    }
}
