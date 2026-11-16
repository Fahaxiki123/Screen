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
    private int[] _frameForRow = [];
    private int _frameCount;
    private int _factor;

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
        if (_factor == factor && _frameCount > 0)
        {
            firstDirtyRow = height;
            if (_frameCount < layout.Frames.Length)
            {
                var oldTail = _frameCount - 1;
                var firstDirtySourceRow = layout.Origins[oldTail] + layout.ContentBottom(oldTail);
                for (var index = _frameCount; index < layout.Frames.Length; index++)
                {
                    firstDirtySourceRow = Math.Min(firstDirtySourceRow, layout.Origins[index] + layout.ContentTop(index));
                }
                firstDirtyRow = firstDirtySourceRow / factor;
            }
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

        UpdateRowCoverage(layout, Math.Min(firstDirtyRow * factor, fullHeight));
        var firstPixel = layout.Frames[0].Pixels;
        for (var y = firstDirtyRow; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var startY = y * factor;
            var rowCount = Math.Min(factor, fullHeight - startY);
            for (var row = 0; row < rowCount; row++)
            {
                var yInLayout = startY + row;
                var frameIndex = _frameForRow[yInLayout];
                _sourceRows[row] = frameIndex < 0
                    ? default
                    : new SourceRow(layout.Frames[frameIndex].Pixels,
                        (yInLayout - layout.Origins[frameIndex]) * layout.Frames[frameIndex].Stride);
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
        _factor = factor;
        return new PixelBuffer(_pixels, width, height, stride);
    }

    private void UpdateRowCoverage(VerticalScrollStitcher.Layout layout, int firstDirty)
    {
        if (_frameForRow.Length < layout.Height)
        {
            Array.Resize(ref _frameForRow, Math.Max(layout.Height, _frameForRow.Length * 2));
        }
        _frameForRow.AsSpan(firstDirty, layout.Height - firstDirty).Fill(-1);
        // Paint the dirty ranges in capture order, just like final composition.
        // Header changes can make adjacent ranges overlap in either order.
        for (var index = 0; index < layout.Frames.Length; index++)
        {
            var start = Math.Max(firstDirty, layout.Origins[index] + layout.ContentTop(index));
            var end = layout.Origins[index] + layout.ContentBottom(index);
            if (start < end)
            {
                _frameForRow.AsSpan(start, end - start).Fill(index);
            }
        }
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
