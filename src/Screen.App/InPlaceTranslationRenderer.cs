using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screen.Core;

namespace Screen.App;

internal static class InPlaceTranslationRenderer
{
    private static readonly Typeface TranslationTypeface = new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static PixelBuffer Render(PixelBuffer source, IReadOnlyList<TranslatedRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(regions);
        var result = new PixelBuffer((byte[])source.Pixels.Clone(), source.Width, source.Height, source.Stride);
        var imageBounds = new PixelRect(0, 0, source.Width, source.Height);
        foreach (var region in regions)
        {
            if (string.IsNullOrWhiteSpace(region.Text))
            {
                continue;
            }
            var bounds = region.Source.Bounds.Intersect(imageBounds);
            if (bounds.IsEmpty)
            {
                continue;
            }

            var background = SampleBackground(source, bounds);
            var luminance = 0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B);
            var foreground = new SolidColorBrush(luminance > 0.179 ? Colors.Black : Colors.White);
            var padding = Math.Min(2, Math.Min(bounds.Width, bounds.Height) * 0.06);
            var availableWidth = Math.Max(0.1, bounds.Width - padding * 2);
            var availableHeight = Math.Max(0.1, bounds.Height - padding * 2);
            var text = CreateFittedText(region.Text.Trim(), availableWidth, availableHeight, region.Source.LineHeight, foreground);

            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushClip(new RectangleGeometry(new Rect(0, 0, bounds.Width, bounds.Height)));
                drawing.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, bounds.Width, bounds.Height));
                drawing.DrawText(text, new Point(padding, padding + Math.Max(0, (availableHeight - text.Height) / 2)));
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var patchStride = checked(bounds.Width * 4);
            var patch = new byte[checked(patchStride * bounds.Height)];
            bitmap.CopyPixels(patch, patchStride, 0);
            for (var y = 0; y < bounds.Height; y++)
            {
                patch.AsSpan(y * patchStride, patchStride).CopyTo(result.Pixels.AsSpan((bounds.Y + y) * result.Stride + bounds.X * 4, patchStride));
            }
        }
        return result;
    }

    internal static FormattedText CreateFittedText(string text, double width, double height, double lineHeight, Brush foreground)
    {
        var largest = Math.Clamp(double.IsFinite(lineHeight) && lineHeight > 0 ? lineHeight * 0.85 : height * 0.8, 1, 96);
        var layout = CreateText(text, width, largest, foreground);
        if (Fits(layout, width, height))
        {
            return layout;
        }

        // Measure the entire wrapped translation. MaxTextHeight/ellipsis would silently discard words.
        var low = 0.01;
        var high = largest;
        for (var attempt = 0; attempt < 18; attempt++)
        {
            var candidate = (low + high) / 2;
            var measured = CreateText(text, width, candidate, foreground);
            if (Fits(measured, width, height))
            {
                low = candidate;
            }
            else
            {
                high = candidate;
            }
        }
        return CreateText(text, width, low, foreground);
    }

    private static bool Fits(FormattedText text, double width, double height) =>
        text.Height <= height && text.WidthIncludingTrailingWhitespace <= width;

    private static FormattedText CreateText(string text, double width, double fontSize, Brush foreground) => new(
        text, CultureInfo.CurrentUICulture, IsRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        TranslationTypeface, fontSize, foreground, 1.0)
    {
        MaxTextWidth = width,
        Trimming = TextTrimming.None
    };

    private static bool IsRightToLeft(string text)
    {
        foreach (var character in text)
        {
            if (character is >= '\u0590' and <= '\u08ff' or >= '\ufb1d' and <= '\ufdff' or >= '\ufe70' and <= '\ufeff')
            {
                return true;
            }
            if (char.IsLetter(character))
            {
                return false;
            }
        }
        return false;
    }

    private static Color SampleBackground(PixelBuffer source, PixelRect bounds)
    {
        // Quantization keeps anti-aliased glyph shades from displacing the dominant local background.
        var counts = new int[512];
        var red = new long[512];
        var green = new long[512];
        var blue = new long[512];
        var step = Math.Max(1, (int)Math.Sqrt((long)bounds.Width * bounds.Height / 12000.0));
        var winner = 0;
        for (var y = bounds.Y; y < bounds.Bottom; y += step)
        {
            for (var x = bounds.X; x < bounds.Right; x += step)
            {
                var offset = y * source.Stride + x * 4;
                var b = source.Pixels[offset];
                var g = source.Pixels[offset + 1];
                var r = source.Pixels[offset + 2];
                var bin = (r >> 5) * 64 + (g >> 5) * 8 + (b >> 5);
                counts[bin]++;
                red[bin] += r;
                green[bin] += g;
                blue[bin] += b;
                if (counts[bin] > counts[winner])
                {
                    winner = bin;
                }
            }
        }
        return Color.FromRgb((byte)(red[winner] / counts[winner]), (byte)(green[winner] / counts[winner]), (byte)(blue[winner] / counts[winner]));
    }

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
