using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screen.Core;

namespace Screen.App;

/// <summary>Prepares the fixed screenshot behind the overlay; never samples annotations or live windows.</summary>
internal static class GlassBackdropProvider
{
    internal const int DownsampleFactor = 4;

    internal static Task<BitmapSource> CreateAsync(CaptureFrame frame, double dpiScale, CancellationToken token) =>
        Task.Run(() => Create(frame, dpiScale, token), token);

    private static BitmapSource Create(CaptureFrame frame, double dpiScale, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Height);
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }
        if (frame.Stride < (long)frame.Width * 4 || frame.Pixels.LongLength < (long)(frame.Height - 1) * frame.Stride + (long)frame.Width * 4)
        {
            throw new ArgumentException("The capture buffer does not cover the frame.", nameof(frame));
        }

        var width = (frame.Width - 1) / DownsampleFactor + 1;
        var height = (frame.Height - 1) / DownsampleFactor + 1;
        var image = GC.AllocateUninitializedArray<Vector4>(checked(width * height));
        var scratch = GC.AllocateUninitializedArray<Vector4>(image.Length);
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) };
        Parallel.For(0, height, options, y => DownsampleRow(frame, image, width, y));

        var radius = checked((int)Math.Round(26 * dpiScale / DownsampleFactor / 2));
        if (radius > 0)
        {
            // Each iteration is a separable box filter. Repeat edge pixels instead of
            // pulling black pixels into panels at the edge of the monitor.
            for (var pass = 0; pass < 3; pass++)
            {
                Parallel.For(0, height, options, y => BlurRow(image, scratch, y, width, radius));
                const int stripe = 32;
                Parallel.For(0, (width + stripe - 1) / stripe, options, i =>
                    BlurColumns(scratch, image, width, height, i * stripe, Math.Min(width, (i + 1) * stripe), radius, token));
            }
        }

        var pixels = GC.AllocateUninitializedArray<byte>(checked(image.Length * 4));
        Parallel.For(0, height, options, y =>
        {
            for (var i = y * width; i < (y + 1) * width; i++)
            {
                var c = image[i]; // B, G, R, A
                var gray = c.Y + 0.2126f * (c.Z - c.Y) + 0.0722f * (c.X - c.Y);
                var bgra = pixels.AsSpan(i * 4, 4);
                bgra[0] = ToByte(gray + (c.X - gray) * 1.7f);
                bgra[1] = ToByte(gray + (c.Y - gray) * 1.7f);
                bgra[2] = ToByte(gray + (c.Z - gray) * 1.7f);
                bgra[3] = ToByte(c.W);
            }
        });

        token.ThrowIfCancellationRequested();
        // At 96 DPI an absolute ImageBrush viewbox can address thumbnail pixels directly.
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static void DownsampleRow(CaptureFrame frame, Vector4[] target, int width, int y)
    {
        var sourceY = y * DownsampleFactor;
        var rows = Math.Min(DownsampleFactor, frame.Height - sourceY);
        for (var x = 0; x < width; x++)
        {
            var sourceX = x * DownsampleFactor;
            var columns = Math.Min(DownsampleFactor, frame.Width - sourceX);
            var blue = 0;
            var green = 0;
            var red = 0;
            var alpha = 0;
            for (var dy = 0; dy < rows; dy++)
            {
                var offset = (sourceY + dy) * frame.Stride + sourceX * 4;
                for (var dx = 0; dx < columns; dx++, offset += 4)
                {
                    blue += frame.Pixels[offset];
                    green += frame.Pixels[offset + 1];
                    red += frame.Pixels[offset + 2];
                    alpha += frame.Pixels[offset + 3];
                }
            }
            target[y * width + x] = new Vector4(blue, green, red, alpha) / (rows * columns);
        }
    }

    private static void BlurRow(Vector4[] source, Vector4[] destination, int y, int width, int radius)
    {
        var offset = y * width;
        var sum = source[offset] * (radius + 1);
        for (var dx = 1; dx <= radius; dx++)
        {
            sum += source[offset + Math.Min(dx, width - 1)];
        }
        var weight = 1f / (2 * radius + 1);
        for (var x = 0; x < width; x++)
        {
            destination[offset + x] = sum * weight;
            sum += source[offset + Math.Min(width - 1, x + radius + 1)] - source[offset + Math.Max(0, x - radius)];
        }
    }

    private static void BlurColumns(Vector4[] source, Vector4[] destination, int width, int height,
        int left, int right, int radius, CancellationToken token)
    {
        // Keep a stripe of column sums hot and read contiguous pixels within each row.
        Span<Vector4> sums = stackalloc Vector4[right - left];
        for (var x = left; x < right; x++)
        {
            var sum = source[x] * (radius + 1);
            for (var dy = 1; dy <= radius; dy++)
            {
                sum += source[Math.Min(dy, height - 1) * width + x];
            }
            sums[x - left] = sum;
        }
        var weight = 1f / (2 * radius + 1);
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var add = Math.Min(height - 1, y + radius + 1) * width;
            var remove = Math.Max(0, y - radius) * width;
            for (var x = left; x < right; x++)
            {
                ref var sum = ref sums[x - left];
                destination[y * width + x] = sum * weight;
                sum += source[add + x] - source[remove + x];
            }
        }
    }
}
