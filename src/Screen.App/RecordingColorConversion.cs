using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Screen.App;

/// <summary>BT.601 limited-range conversion shared by GDI fallback and cursor compositing.</summary>
internal static class RecordingColorConversion
{
    public static byte RgbToY(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 16, 235);

    public static byte RgbToU(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 16, 240);

    public static byte RgbToV(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 16, 240);

    public static void CopyBgraToNv12(ReadOnlySpan<byte> bgra, Span<byte> nv12, int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0
            || bgra.Length != checked(width * height * 4) || nv12.Length != checked(width * height * 3 / 2))
        {
            throw new ArgumentException("NV12 转换需要偶数尺寸和完整的画面缓冲。");
        }
        var uvStart = width * height;
        for (var y = 0; y < height; y += 2)
        {
            var row = y * width;
            var uvRow = uvStart + y / 2 * width;
            var x = 0;
            if (Avx2.IsSupported)
            {
                x = ConvertVectorizedRows(bgra.Slice(row * 4, width * 4),
                    bgra.Slice((row + width) * 4, width * 4), nv12.Slice(row, width),
                    nv12.Slice(row + width, width), nv12.Slice(uvRow, width));
            }
            // Also handles the last 2, 4 or 6 pixels of a region on AVX2 machines.
            for (; x < width; x += 2)
            {
                var r = 0;
                var g = 0;
                var b = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var pixel = (y + dy) * width + x + dx;
                        var offset = pixel * 4;
                        var blue = bgra[offset];
                        var green = bgra[offset + 1];
                        var red = bgra[offset + 2];
                        nv12[pixel] = RgbToY(red, green, blue);
                        r += red;
                        g += green;
                        b += blue;
                    }
                }
                // One shared UV pair represents all four pixels; rounding the
                // accumulated components avoids last-pixel chroma and bias.
                var uv = uvRow + x;
                nv12[uv] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 512) >> 10) + 128, 16, 240);
                nv12[uv + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 512) >> 10) + 128, 16, 240);
            }
        }
    }

    // Process eight pixels from each of two rows. Each lane holds a whole BGRA
    // pixel, so masking/shifting extracts eight channels without temporary buffers.
    private static int ConvertVectorizedRows(ReadOnlySpan<byte> top, ReadOnlySpan<byte> bottom,
        Span<byte> topY, Span<byte> bottomY, Span<byte> uv)
    {
        var mask = Vector256.Create(255);
        var x = 0;
        for (; x <= topY.Length - 8; x += 8)
        {
            var a = MemoryMarshal.Read<Vector256<int>>(top[(x * 4)..]);
            var b = MemoryMarshal.Read<Vector256<int>>(bottom[(x * 4)..]);
            var topRed = (a >>> 16) & mask;
            var topGreen = (a >>> 8) & mask;
            var topBlue = a & mask;
            var bottomRed = (b >>> 16) & mask;
            var bottomGreen = (b >>> 8) & mask;
            var bottomBlue = b & mask;

            var luma = Sse2.PackUnsignedSaturate(Luma(topRed, topGreen, topBlue), Luma(bottomRed, bottomGreen, bottomBlue));
            luma.GetLower().CopyTo(topY[x..]);
            luma.GetUpper().CopyTo(bottomY[x..]);

            var red = SumAdjacentPairs(topRed + bottomRed);
            var green = SumAdjacentPairs(topGreen + bottomGreen);
            var blue = SumAdjacentPairs(topBlue + bottomBlue);
            var u = ((112 * blue - 74 * green - 38 * red + Vector128.Create(512)) >> 10) + Vector128.Create(128);
            var v = ((112 * red - 94 * green - 18 * blue + Vector128.Create(512)) >> 10) + Vector128.Create(128);
            var pairs = (u | (v << 8)).AsUInt32();
            Vector128.Narrow(pairs, pairs).AsByte().GetLower().CopyTo(uv[x..]);
        }
        return x;
    }

    private static Vector128<short> Luma(Vector256<int> red, Vector256<int> green, Vector256<int> blue)
    {
        var y = ((66 * red + 129 * green + 25 * blue + Vector256.Create(128)) >> 8) + Vector256.Create(16);
        return Sse2.PackSignedSaturate(y.GetLower(), y.GetUpper());
    }

    private static Vector128<int> SumAdjacentPairs(Vector256<int> verticalSums)
    {
        // HorizontalAdd repeats two pair sums in each 128-bit half. Keep the first two from each half.
        var pairs = Avx2.HorizontalAdd(verticalSums, verticalSums);
        return Sse2.UnpackLow(pairs.GetLower().AsInt64(), pairs.GetUpper().AsInt64()).AsInt32();
    }
}
