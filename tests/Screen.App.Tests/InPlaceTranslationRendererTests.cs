using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class InPlaceTranslationRendererTests
{
    [TestMethod]
    [DataRow((byte)244, (byte)18)]
    [DataRow((byte)24, (byte)238)]
    public void TranslationUsesLocalBackgroundAndReadableContrast(byte background, byte originalText) => OnSta(() =>
    {
        var source = Solid(240, 100, background);
        var bounds = new PixelRect(30, 25, 180, 40);
        // Original text pixels must be erased before painting the translation.
        for (var y = 30; y < 55; y++)
        {
            for (var x = 35; x < 60; x++)
            {
                SetGray(source, x, y, originalText);
            }
        }
        var original = (byte[])source.Pixels.Clone();
        var translated = InPlaceTranslationRenderer.Render(source, [new TranslatedRegion(new TranslationRegion("Original", bounds, 30), "翻译后的文字")]);

        CollectionAssert.AreEqual(original, source.Pixels, "Rendering must not mutate the captured source.");
        AssertOutsideUnchanged(source, translated, bounds);
        Assert.AreEqual(background, translated.Pixels[bounds.Y * translated.Stride + bounds.X * 4]);
        var contrastPixels = 0;
        for (var y = bounds.Y; y < bounds.Bottom; y++)
        {
            for (var x = bounds.X; x < bounds.Right; x++)
            {
                var value = translated.Pixels[y * translated.Stride + x * 4];
                if (background > 128 ? value < 70 : value > 190)
                {
                    contrastPixels++;
                }
            }
        }
        Assert.IsGreaterThan(20, contrastPixels, "Translation glyphs need high contrast on either theme.");
    });

    [TestMethod]
    public void LongTranslationFitsEntireTextWithoutEllipsisOrClipping() => OnSta(() =>
    {
        const string text = "A considerably longer translated sentence must remain complete, including its final words: THE END.";
        var layout = InPlaceTranslationRenderer.CreateFittedText(text, 95, 26, 24, Brushes.Black);
        Assert.AreEqual(text, layout.Text);
        Assert.IsTrue(layout.Height <= 26, $"Full text height {layout.Height} exceeds its region.");
        Assert.IsTrue(layout.WidthIncludingTrailingWhitespace <= 95);
        Assert.AreEqual(System.Windows.TextTrimming.None, layout.Trimming);
    });

    [TestMethod]
    public void RegionAtImageEdgeIsClippedAndOtherPixelsAreExact() => OnSta(() =>
    {
        var source = Solid(120, 80, 240);
        var result = InPlaceTranslationRenderer.Render(source,
            [new TranslatedRegion(new TranslationRegion("Edge", new PixelRect(-10, 65, 65, 30), 20), "边缘翻译")]);
        AssertOutsideUnchanged(source, result, new PixelRect(0, 65, 55, 15));
    });

    private static PixelBuffer Solid(int width, int height, byte value)
    {
        var source = new PixelBuffer(new byte[width * height * 4], width, height, width * 4);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                SetGray(source, x, y, value);
            }
        }
        return source;
    }

    private static void SetGray(PixelBuffer source, int x, int y, byte value)
    {
        var offset = y * source.Stride + x * 4;
        source.Pixels[offset] = value;
        source.Pixels[offset + 1] = value;
        source.Pixels[offset + 2] = value;
        source.Pixels[offset + 3] = 255;
    }

    private static void AssertOutsideUnchanged(PixelBuffer before, PixelBuffer after, PixelRect changed)
    {
        for (var y = 0; y < before.Height; y++)
        {
            for (var x = 0; x < before.Width; x++)
            {
                if (x >= changed.X && x < changed.Right && y >= changed.Y && y < changed.Bottom)
                {
                    continue;
                }
                var offset = y * before.Stride + x * 4;
                Assert.IsTrue(before.Pixels.AsSpan(offset, 4).SequenceEqual(after.Pixels.AsSpan(offset, 4)), $"Pixel ({x}, {y}) outside the text changed.");
            }
        }
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            { action(); }
            catch (Exception exception) { error = exception; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "STA renderer test timed out.");
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
