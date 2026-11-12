using System.Runtime.InteropServices;
using Screen.Core;

namespace Screen.App;

/// <summary>Updates a bounded overview from already matched viewport positions.</summary>
internal sealed class ScrollPreviewBuilder
{
    private const int MaxWidth = 512;
    private const int MaxHeight = 4096;
    private byte[] _pixels = [];
    private SourceRow[] _sourceRows = [];
    private int _frameCount;
    private int _fullHeight;
    private int _factor;
    private int _header;
    private int _footer;

    // The returned pixels belong to this builder. The caller copies them into
    // a frozen bitmap before starting another update.
    public PixelBuffer Update(VerticalScrollStitcher.Layout layout, CancellationToken token)
    {
        var fullWidth = layout.Width;
        var fullHeight = layout.Height;
        var factor = Math.Max(DivideRoundUp(fullWidth, MaxWidth), DivideRoundUp(fullHeight, MaxHeight));
        var width = DivideRoundUp(fullWidth, factor);
        var height = DivideRoundUp(fullHeight, factor);
        var stride = width * 4;
        var firstDirtyRow = 0;
        if (_factor == factor && _header == layout.FixedHeader && _footer == layout.FixedFooter)
        {
            // New viewports can replace part of the old overlap. The previous
            // footer must also be repainted even when no new frame covers it.
            firstDirtyRow = _frameCount == layout.Frames.Length
                ? height
                : Math.Min(_fullHeight - _footer, layout.Origins[_frameCount] + _header) / factor;
        }

        var required = stride * height;
        if (_pixels.Length < required)
        {
            Array.Resize(ref _pixels, Math.Min(MaxWidth * MaxHeight * 4, Math.Max(required, _pixels.Length * 2)));
        }
        var rowsPerBlock = Math.Min(factor, fullHeight);
        if (_sourceRows.Length < rowsPerBlock)
        {
            _sourceRows = new SourceRow[rowsPerBlock];
        }

        var firstPixel = layout.Frames[0].Pixels;
        var frameIndex = 0;
        for (var y = firstDirtyRow; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var startY = y * factor;
            var rowCount = Math.Min(factor, fullHeight - startY);
            for (var row = 0; row < rowCount; row++)
            {
                _sourceRows[row] = GetSourceRow(layout, startY + row, ref frameIndex);
            }
            var destination = _pixels.AsSpan(y * stride, stride);
            if (factor == 1)
            {
                var source = _sourceRows[0];
                if (source.Pixels is { } pixels)
                {
                    pixels.AsSpan(source.Offset, stride).CopyTo(destination);
                }
                else
                {
                    var background = 0xFF000000u | ((uint)firstPixel[2] << 16) | ((uint)firstPixel[1] << 8) | firstPixel[0];
                    MemoryMarshal.Cast<byte, uint>(destination).Fill(background);
                }
            }
            else
            {
                FilterRow(destination, _sourceRows.AsSpan(0, rowCount), firstPixel, fullWidth, factor);
            }
        }

        _frameCount = layout.Frames.Length;
        _fullHeight = fullHeight;
        _factor = factor;
        _header = layout.FixedHeader;
        _footer = layout.FixedFooter;
        return new PixelBuffer(_pixels, width, height, stride);
    }

    private static SourceRow GetSourceRow(VerticalScrollStitcher.Layout layout, int y, ref int frameIndex)
    {
        var frames = layout.Frames;
        var origins = layout.Origins;
        if (y < layout.FixedHeader)
        {
            return new SourceRow(frames[0].Pixels, y * frames[0].Stride);
        }
        if (y >= layout.Height - layout.FixedFooter)
        {
            return new SourceRow(frames[^1].Pixels, (y - origins[^1]) * frames[^1].Stride);
        }
        while (frameIndex + 1 < frames.Length && origins[frameIndex + 1] + layout.FixedHeader <= y)
        {
            frameIndex++;
        }
        var frame = frames[frameIndex];
        var localY = y - origins[frameIndex];
        return localY < frame.Height - layout.FixedFooter
            ? new SourceRow(frame.Pixels, localY * frame.Stride)
            : default;
    }

    private static void FilterRow(
        Span<byte> destination, ReadOnlySpan<SourceRow> rows, byte[] background, int fullWidth, int factor)
    {
        for (var x = 0; x < destination.Length / 4; x++)
        {
            var startX = x * factor;
            var columns = Math.Min(factor, fullWidth - startX);
            long blue = 0;
            long green = 0;
            long red = 0;
            foreach (var row in rows)
            {
                if (row.Pixels is not { } pixels)
                {
                    blue += (long)background[0] * columns;
                    green += (long)background[1] * columns;
                    red += (long)background[2] * columns;
                    continue;
                }
                var start = row.Offset + startX * 4;
                var end = start + columns * 4;
                for (var offset = start; offset < end; offset += 4)
                {
                    blue += pixels[offset];
                    green += pixels[offset + 1];
                    red += pixels[offset + 2];
                }
            }
            var count = (long)rows.Length * columns;
            var target = x * 4;
            destination[target] = (byte)(blue / count);
            destination[target + 1] = (byte)(green / count);
            destination[target + 2] = (byte)(red / count);
            destination[target + 3] = 255;
        }
    }

    private static int DivideRoundUp(int value, int divisor) => (value - 1) / divisor + 1;

    private readonly record struct SourceRow(byte[]? Pixels, int Offset);
}
