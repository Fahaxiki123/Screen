using System.Runtime.InteropServices;

namespace Screen.Core;

public sealed class VerticalScrollStitcher : IScrollStitcher
{
    private const int MinimumOverlap = 48;
    private const int MaximumFixedBand = 600;
    private const int MaximumHeight = 100_000;
    private const long MaximumOutputBytes = 512L * 1024 * 1024;
    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private const long MaximumWorkingBytes = 768L * 1024 * 1024;
    private const double MaximumAcceptedError = 18.0;
    private const int OverlayTileSize = 8;
    private const int HorizontalSamples = 96;

    public StitchResult Stitch(IReadOnlyList<PixelBuffer> frames, CancellationToken cancellationToken = default)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(frames));
        }

        var width = frames[0].Width;
        var height = frames[0].Height;
        if (frames.Any(frame => frame.Width != width || frame.Height != height || frame.Stride < width * 4))
        {
            throw new ArgumentException("All frames must have the same BGRA dimensions.", nameof(frames));
        }

        var session = new Session();
        session.AddFirstFrame(frames[0], cancellationToken);
        string? failureReason = null;
        for (var index = 1; index < frames.Count; index++)
        {
            (_, failureReason) = session.TryAppend(frames[index], cancellationToken);
            if (failureReason is not null)
            {
                break;
            }
        }
        return Compose(session.Snapshot(), cancellationToken, failureReason);
    }

    internal readonly record struct FixedBands(int Header, int Footer);

    internal sealed record Layout(
        PixelBuffer[] Frames, int[] Origins, StitchSeam[] Seams, FixedBands[] Bands)
    {
        public int Width => Frames[0].Width;
        public int FrameHeight => Frames[0].Height;
        public int Height => checked(Origins[^1] + FrameHeight);
        public int FirstHeader => Bands.Length == 0 ? 0 : Bands[0].Header;
        public int LastFooter => Bands.Length == 0 ? 0 : Bands[^1].Footer;

        // Each join trims its incoming header and outgoing footer. Applying a
        // later, larger header to already placed viewports can leave uncovered rows.
        public int ContentTop(int index) => index == 0
            ? 0
            : Math.Max(Bands[index - 1].Header, FirstHeader - Origins[index]);

        public int ContentBottom(int index) => index == Frames.Length - 1
            ? FrameHeight
            : FrameHeight - Bands[index].Footer;
    }

    /// <summary>Matches each new viewport once. One writer admits frames while preview can take snapshots.</summary>
    internal sealed class Session
    {
        private readonly object _gate = new();
        private readonly List<PixelBuffer> _frames = [];
        private readonly List<int> _origins = [];
        private readonly List<StitchSeam> _seams = [];
        private readonly List<FixedBands> _bands = [];
        private byte[]? _previousSamples;
        private long _inputBytes;

        public int Count
        {
            get { lock (_gate) return _frames.Count; }
        }

        public PixelBuffer? LastFrame
        {
            get { lock (_gate) return _frames.Count == 0 ? null : _frames[^1]; }
        }

        public Layout Snapshot()
        {
            lock (_gate)
            {
                return new Layout(_frames.ToArray(), _origins.ToArray(), _seams.ToArray(), _bands.ToArray());
            }
        }

        public (bool Added, string? FailureReason) TryAppend(PixelBuffer current, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var height = current.Height;
            var width = current.Width;
            var nextInputBytes = checked(_inputBytes + (long)current.Stride * height);
            if (_frames.Count == 0)
            {
                if (nextInputBytes > MaximumInputBytes)
                {
                    return (false, "The long screenshot input-frame memory limit was reached.");
                }
                if (ExceedsOutputLimit(width, height, nextInputBytes))
                {
                    return (false, "The long screenshot memory limit was reached.");
                }
                AddFirstFrame(current, cancellationToken);
                return (true, null);
            }

            var previous = _frames[^1];
            var index = _frames.Count;
            if (width != previous.Width || height != previous.Height || current.Stride < width * 4)
            {
                throw new ArgumentException("All frames must have the same BGRA dimensions.", nameof(current));
            }
            var currentSamples = SampleRows(current);
            var previousSamples = _previousSamples!;
            var sampleWidth = Math.Min(width, HorizontalSamples);
            var (stationaryError, hasStationaryAgreement) = CompareStationary(previousSamples, currentSamples);
            if (stationaryError == 0)
            {
                return HasVerticalDetail(previousSamples, sampleWidth)
                    ? (false, null)
                    : (false, $"Frame {index + 1} could not be joined reliably (too little vertical detail).");
            }

            var fixedHeader = DetectFixedBand(previous, current, fromTop: true);
            var fixedFooter = DetectFixedBand(previous, current, fromTop: false);
            if (fixedHeader + fixedFooter > height / 2)
            {
                fixedHeader = 0;
                fixedFooter = 0;
            }

            var match = FindBestOverlap(previousSamples, currentSamples, sampleWidth, height, fixedHeader, fixedFooter, reverse: false, cancellationToken);
            // An exact, unambiguous downward match cannot be improved by an
            // upward search. This is the common browser-scrolling path.
            var reverseMatch = match.Overlap >= MinimumOverlap && match.Error == 0
                ? (Overlap: 0, Error: double.MaxValue)
                : FindBestOverlap(previousSamples, currentSamples, sampleWidth, height, fixedHeader, fixedFooter, reverse: true, cancellationToken);
            // Local animation can change viewport pixels without scrolling. A
            // shifted candidate must beat the unshifted viewport convincingly.
            if (hasStationaryAgreement && stationaryError <= MaximumAcceptedError
                && stationaryError - Math.Min(match.Error, reverseMatch.Error) <= AmbiguityMargin(stationaryError))
            {
                return (false, null);
            }
            if (reverseMatch.Overlap >= MinimumOverlap
                && reverseMatch.Error <= MaximumAcceptedError
                && (match.Overlap < MinimumOverlap
                    || match.Error > MaximumAcceptedError
                    || reverseMatch.Error + AmbiguityMargin(reverseMatch.Error) < match.Error))
            {
                return (false, $"Frame {index + 1} moved upward; scroll capture only accepts downward movement.");
            }
            if (match.Overlap < MinimumOverlap || match.Error > MaximumAcceptedError)
            {
                return (false, $"Frame {index + 1} could not be joined reliably (error {match.Error:F1}).");
            }

            var previousContentBottom = height - fixedFooter;
            var scrollDistance = previousContentBottom - match.Overlap - fixedHeader;
            if (nextInputBytes > MaximumInputBytes)
            {
                return (false, "The long screenshot input-frame memory limit was reached.");
            }

            var origin = checked(_origins[^1] + scrollDistance);
            var outputHeight = checked(origin + height);
            if (ExceedsOutputLimit(width, outputHeight, nextInputBytes))
            {
                return (false, "The long screenshot memory limit was reached.");
            }

            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _frames.Add(current);
                _origins.Add(origin);
                _seams.Add(new StitchSeam(index, fixedHeader, match.Overlap, match.Error, origin));
                _bands.Add(new FixedBands(fixedHeader, fixedFooter));
                _inputBytes = nextInputBytes;
                _previousSamples = currentSamples;
            }
            return (true, null);
        }

        internal void AddFirstFrame(PixelBuffer frame, CancellationToken cancellationToken)
        {
            var samples = SampleRows(frame);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _frames.Add(frame);
                _origins.Add(0);
                _inputBytes = checked((long)frame.Stride * frame.Height);
                _previousSamples = samples;
            }
        }

        private static bool ExceedsOutputLimit(int width, int height, long inputBytes)
        {
            var outputBytes = checked((long)width * height * 4);
            return height > MaximumHeight || outputBytes > MaximumOutputBytes || inputBytes + outputBytes > MaximumWorkingBytes;
        }
    }

    internal static StitchResult Compose(Layout layout, CancellationToken cancellationToken, string? failureReason = null)
    {
        if (layout.Frames.Length == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(layout));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var overlays = layout.Frames.Length < 2
            ? Array.Empty<PixelRect>()
            : DetectStableOverlays(layout, cancellationToken);
        var image = BuildResult(layout, overlays, cancellationToken);
        return new StitchResult(image, layout.Seams, failureReason is null, failureReason);
    }

    private static PixelBuffer BuildResult(
        Layout layout,
        IReadOnlyList<PixelRect> overlays,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frames = layout.Frames;
        var origins = layout.Origins;
        var width = layout.Width;
        var frameHeight = layout.FrameHeight;
        var height = layout.Height;
        var stride = checked(width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        FillBackground(pixels, stride, height, frames[0].Pixels);

        // The overlay exclusion machinery below only matters when overlays were
        // detected; without them every row is a single full-width copy.
        var hasOverlays = overlays.Count > 0;
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[frameIndex];
            var bottom = layout.ContentBottom(frameIndex);
            for (var y = layout.ContentTop(frameIndex); y < bottom; y++)
            {
                var sourceRow = y * frame.Stride;
                var targetRow = (origins[frameIndex] + y) * stride;
                if (!hasOverlays
                    || (frameIndex == 0 && y < layout.FirstHeader)
                    || (frameIndex == frames.Length - 1 && y >= frameHeight - layout.LastFooter))
                {
                    CopySpan(frame.Pixels, sourceRow, pixels, targetRow, 0, width);
                    continue;
                }
                var intervals = GetExcludedIntervals(
                    overlays,
                    y,
                    width,
                    frameIndex,
                    layout);
                var startX = 0;
                foreach (var interval in intervals)
                {
                    CopySpan(frame.Pixels, sourceRow, pixels, targetRow, startX, interval.X);
                    startX = interval.Right;
                }
                CopySpan(frame.Pixels, sourceRow, pixels, targetRow, startX, width);
            }
        }

        foreach (var overlay in overlays)
        {
            var useFirstFrame = overlay.Y + overlay.Height / 2 < frameHeight / 3;
            var sourceIndex = useFirstFrame ? 0 : frames.Length - 1;
            var top = useFirstFrame ? layout.FirstHeader : layout.ContentTop(sourceIndex);
            var bottom = useFirstFrame ? layout.ContentBottom(sourceIndex) : frameHeight - layout.LastFooter;
            var edgeBody = new PixelRect(0, top, width, bottom - top);
            CopyRectangle(frames[sourceIndex], overlay.Intersect(edgeBody), pixels, stride, origins[sourceIndex]);
        }

        return new PixelBuffer(pixels, width, height, stride);
    }

    private static void FillBackground(byte[] pixels, int stride, int height, byte[] sample)
    {
        var b = sample.Length >= 4 ? sample[0] : (byte)0;
        var g = sample.Length >= 4 ? sample[1] : (byte)0;
        var r = sample.Length >= 4 ? sample[2] : (byte)0;
        var pattern = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, checked(stride * height))).Fill(pattern);
    }

    private static IReadOnlyList<PixelRect> GetExcludedIntervals(
        IReadOnlyList<PixelRect> overlays,
        int y,
        int width,
        int frameIndex,
        Layout layout)
    {
        var intervals = new List<PixelRect>();
        foreach (var rect in overlays)
        {
            if (y < rect.Y || y >= rect.Bottom)
            {
                continue;
            }
            if (!HasAlternateCoverage(
                    rect,
                    layout.Origins[frameIndex] + y,
                    frameIndex,
                    layout,
                    overlays))
            {
                continue;
            }
            var left = Math.Clamp(rect.X, 0, width);
            var right = Math.Clamp(rect.Right, 0, width);
            if (right > left)
            {
                intervals.Add(new PixelRect(left, 0, right - left, 1));
            }
        }
        if (intervals.Count == 0)
        {
            return Array.Empty<PixelRect>();
        }

        intervals.Sort((first, second) => first.X - second.X);
        var write = 0;
        for (var index = 1; index < intervals.Count; index++)
        {
            if (intervals[index].X <= intervals[write].Right)
            {
                intervals[write] = new PixelRect(
                    intervals[write].X,
                    0,
                    Math.Max(intervals[write].Right, intervals[index].Right) - intervals[write].X,
                    1);
            }
            else
            {
                intervals[++write] = intervals[index];
            }
        }
        intervals.RemoveRange(write + 1, intervals.Count - write - 1);
        return intervals;
    }

    private static bool HasAlternateCoverage(
        PixelRect rectangle,
        int outputY,
        int excludedFrame,
        Layout layout,
        IReadOnlyList<PixelRect> overlays)
    {
        for (var index = 0; index < layout.Frames.Length; index++)
        {
            if (index == excludedFrame)
            {
                continue;
            }
            var sourceY = outputY - layout.Origins[index];
            if (sourceY < layout.ContentTop(index) || sourceY >= layout.ContentBottom(index))
            {
                continue;
            }
            var coveredByAnotherOverlay = overlays.Any(overlay =>
                sourceY >= overlay.Y && sourceY < overlay.Bottom
                && rectangle.X < overlay.Right && rectangle.Right > overlay.X);
            if (!coveredByAnotherOverlay)
            {
                return true;
            }
        }
        return false;
    }

    private static void CopySpan(byte[] source, int sourceRow, byte[] target, int targetRow, int startX, int endX)
    {
        if (endX <= startX)
        {
            return;
        }
        source.AsSpan(sourceRow + startX * 4, (endX - startX) * 4)
            .CopyTo(target.AsSpan(targetRow + startX * 4, (endX - startX) * 4));
    }

    private static void CopyRectangle(PixelBuffer source, PixelRect rectangle, byte[] target, int targetStride, int originY)
    {
        var clipped = rectangle.Intersect(new PixelRect(0, 0, source.Width, source.Height));
        if (clipped.IsEmpty)
        {
            return;
        }
        var rowBytes = clipped.Width * 4;
        for (var row = 0; row < clipped.Height; row++)
        {
            source.Pixels.AsSpan((clipped.Y + row) * source.Stride + clipped.X * 4, rowBytes)
                .CopyTo(target.AsSpan((originY + clipped.Y + row) * targetStride + clipped.X * 4, rowBytes));
        }
    }

    private static int DetectFixedBand(PixelBuffer previous, PixelBuffer current, bool fromTop)
    {
        var limit = Math.Min(Math.Min(previous.Height, current.Height) / 3, MaximumFixedBand);
        var matchingRows = 0;
        for (var offset = 0; offset < limit; offset++)
        {
            var row = fromTop ? offset : previous.Height - 1 - offset;
            if (CompareRows(previous, row, current, row) > 5.0)
            {
                break;
            }
            matchingRows = offset + 1;
        }
        if (matchingRows < 16)
        {
            return 0;
        }

        matchingRows = Math.Min(matchingRows, previous.Height - 1);
        var boundary = fromTop ? matchingRows : previous.Height - matchingRows;
        var inside = fromTop ? Math.Max(0, boundary - 1) : Math.Min(previous.Height - 1, boundary);
        var outside = fromTop ? Math.Min(previous.Height - 1, boundary) : Math.Max(0, boundary - 1);
        // A text line may begin beside the band in only one viewport. Looking
        // only at the first frame mistakes its blank margin for a missing edge
        // and includes the entire fixed toolbar in the scrolling comparison.
        return Math.Max(CompareRows(previous, inside, previous, outside),
                CompareRows(current, inside, current, outside)) >= 2.0
            ? matchingRows
            : 0;
    }

    private static (int Overlap, double Error) FindBestOverlap(
        byte[] previous,
        byte[] current,
        int sampleWidth,
        int frameHeight,
        int fixedHeader,
        int fixedFooter,
        bool reverse,
        CancellationToken cancellationToken)
    {
        var contentTop = fixedHeader;
        var contentBottom = frameHeight - fixedFooter;
        var contentHeight = contentBottom - contentTop;
        var maximum = contentHeight - 1;
        if (maximum < MinimumOverlap)
        {
            return (0, double.MaxValue);
        }

        var bestOverlap = 0;
        var bestError = double.MaxValue;
        var slotCount = maximum - MinimumOverlap + 1;
        var errors = new double[slotCount];
        // Manual scrolling can stop at any pixel. Searching only even offsets
        // and refining a single winner misses sharp, odd-offset matches.
        for (var overlap = maximum; overlap >= MinimumOverlap; overlap--)
        {
            if ((overlap & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var previousY = reverse ? contentTop : contentBottom - overlap;
            var currentY = reverse ? contentBottom - overlap : contentTop;
            var error = CompareAreas(previous, previousY, current, currentY, sampleWidth, overlap,
                bestError + AmbiguityMargin(bestError));
            errors[overlap - MinimumOverlap] = error;
            // A tolerance here lets successively worse matches replace the
            // winner and drift by whole text lines. Keep the actual minimum.
            if (error < bestError)
            {
                bestError = error;
                bestOverlap = overlap;
            }
        }
        var alternateError = double.MaxValue;
        for (var overlap = MinimumOverlap; overlap <= maximum; overlap++)
        {
            if (Math.Abs(overlap - bestOverlap) >= 20)
            {
                alternateError = Math.Min(alternateError, errors[overlap - MinimumOverlap]);
            }
        }
        if (alternateError <= bestError + AmbiguityMargin(bestError))
        {
            return (0, bestError);
        }
        return (bestOverlap, bestError);
    }

    private static double AmbiguityMargin(double error) => Math.Min(0.75, error * 0.15 + 0.00001);

    private static (double Error, bool HasAgreement) CompareStationary(byte[] previous, byte[] current)
    {
        long difference = 0;
        var changedSamples = 0;
        for (var index = 0; index < previous.Length; index++)
        {
            var delta = Math.Abs(previous[index] - current[index]);
            difference += delta;
            if (delta > 2)
            {
                changedSamples++;
            }
        }

        // A moving badge can change a small part of an otherwise stationary
        // viewport. A low average alone could also hide a whole new pale page.
        return (difference / (double)previous.Length, changedSamples <= previous.Length / 8);
    }

    private static bool HasVerticalDetail(byte[] samples, int width)
    {
        var firstRow = samples.AsSpan(0, width);
        for (var offset = width; offset < samples.Length; offset += width)
        {
            if (!firstRow.SequenceEqual(samples.AsSpan(offset, width)))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] SampleRows(PixelBuffer frame)
    {
        var sampleWidth = Math.Min(frame.Width, HorizontalSamples);
        var samples = new byte[checked(sampleWidth * frame.Height)];
        for (var y = 0; y < frame.Height; y++)
        {
            for (var column = 0; column < sampleWidth; column++)
            {
                var start = column * frame.Width / sampleWidth;
                var end = (column + 1) * frame.Width / sampleWidth;
                // Vary the position within each column without depending on
                // the row, so thin vertical strokes cannot all fall between
                // regularly spaced samples and shifted rows remain comparable.
                var x = start + (column * 13 + 7) % (end - start);
                var offset = y * frame.Stride + x * 4;
                samples[y * sampleWidth + column] = (byte)((frame.Pixels[offset] * 29
                    + frame.Pixels[offset + 1] * 150 + frame.Pixels[offset + 2] * 77) >> 8);
            }
        }
        return samples;
    }

    private static double CompareAreas(
        byte[] first, int firstY, byte[] second, int secondY, int width, int height, double maximumError)
    {
        // Check every row: a fixed ~48-row grid aliases with browser line
        // heights and can compare only whitespace at an incorrect offset.
        var count = checked(width * height);
        var firstOffset = firstY * width;
        var secondOffset = secondY * width;
        var maximumDifference = maximumError * count;
        long difference = 0;
        for (var offset = 0; offset < count; offset++)
        {
            difference += Math.Abs(first[firstOffset + offset] - second[secondOffset + offset]);
            if ((offset & 255) == 255 && difference > maximumDifference)
            {
                // This lower bound already exceeds both the current best and
                // its ambiguity margin; no remaining row can make it win.
                break;
            }
        }
        return difference / (double)count;
    }

    private static double CompareRows(PixelBuffer first, int firstRow, PixelBuffer second, int secondRow)
    {
        const int horizontalSamples = 96;
        var step = Math.Max(1, first.Width / horizontalSamples);
        long sum = 0;
        var count = 0;
        var aRow = firstRow * first.Stride;
        var bRow = secondRow * second.Stride;
        for (var x = 0; x < first.Width; x += step)
        {
            var a = aRow + x * 4;
            var b = bRow + x * 4;
            var grayA = (first.Pixels[a] * 29 + first.Pixels[a + 1] * 150 + first.Pixels[a + 2] * 77) >> 8;
            var grayB = (second.Pixels[b] * 29 + second.Pixels[b + 1] * 150 + second.Pixels[b + 2] * 77) >> 8;
            sum += Math.Abs(grayA - grayB);
            count++;
        }
        return sum / (double)Math.Max(1, count);
    }

    private static PixelRect[] DetectStableOverlays(
        Layout layout,
        CancellationToken cancellationToken)
    {
        var frames = layout.Frames;
        var origins = layout.Origins;
        var width = layout.Width;
        var height = layout.FrameHeight;
        var fixedHeader = layout.Bands.Min(band => band.Header);
        var fixedFooter = layout.Bands.Min(band => band.Footer);
        var columns = (width + OverlayTileSize - 1) / OverlayTileSize;
        var rows = (height + OverlayTileSize - 1) / OverlayTileSize;
        var seeds = new bool[columns, rows];

        for (var tileY = 0; tileY < rows; tileY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y = tileY * OverlayTileSize;
            if (y < fixedHeader || y >= height - fixedFooter)
            {
                continue;
            }
            for (var tileX = 0; tileX < columns; tileX++)
            {
                var x = tileX * OverlayTileSize;
                var tileWidth = Math.Min(OverlayTileSize, width - x);
                var tileHeight = Math.Min(OverlayTileSize, height - y);
                double sameError = 0;
                double movedError = 0;
                var comparisons = 0;
                for (var index = 1; index < frames.Length; index++)
                {
                    var delta = origins[index] - origins[index - 1];
                    var band = layout.Bands[index - 1];
                    if (y < band.Header || y + delta + tileHeight > height - band.Footer)
                    {
                        continue;
                    }
                    sameError += CompareTile(frames[index - 1], x, y, frames[index], x, y, tileWidth, tileHeight);
                    movedError += CompareTile(frames[index - 1], x, y + delta, frames[index], x, y, tileWidth, tileHeight);
                    comparisons++;
                }
                if (comparisons == 0)
                {
                    continue;
                }
                sameError /= comparisons;
                movedError /= comparisons;
                var texture = TileTexture(frames[0], x, y, tileWidth, tileHeight);
                seeds[tileX, tileY] = sameError <= 2.5
                    && movedError >= Math.Max(7.0, sameError + 4.0)
                    && texture >= 2.0;
            }
        }

        var expanded = Dilate(seeds, columns, rows, radius: 1);
        return FindOverlayRectangles(seeds, expanded, columns, rows, width, height, fixedHeader, fixedFooter);
    }

    private static double CompareTile(
        PixelBuffer first, int firstX, int firstY,
        PixelBuffer second, int secondX, int secondY,
        int width, int height)
    {
        long difference = 0;
        var count = 0;
        var step = Math.Max(1, Math.Min(width, height) / 4);
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var a = (firstY + y) * first.Stride + (firstX + x) * 4;
                var b = (secondY + y) * second.Stride + (secondX + x) * 4;
                difference += Math.Abs(first.Pixels[a] - second.Pixels[b]);
                difference += Math.Abs(first.Pixels[a + 1] - second.Pixels[b + 1]);
                difference += Math.Abs(first.Pixels[a + 2] - second.Pixels[b + 2]);
                count += 3;
            }
        }
        return difference / (double)Math.Max(1, count);
    }

    private static double TileTexture(PixelBuffer frame, int x, int y, int width, int height)
    {
        var minimum = 255;
        var maximum = 0;
        for (var row = 0; row < height; row += 2)
        {
            for (var column = 0; column < width; column += 2)
            {
                var offset = (y + row) * frame.Stride + (x + column) * 4;
                var gray = (frame.Pixels[offset] * 29 + frame.Pixels[offset + 1] * 150 + frame.Pixels[offset + 2] * 77) >> 8;
                minimum = Math.Min(minimum, gray);
                maximum = Math.Max(maximum, gray);
            }
        }
        return maximum - minimum;
    }

    private static bool[,] Dilate(bool[,] source, int columns, int rows, int radius)
    {
        var result = new bool[columns, rows];
        for (var x = 0; x < columns; x++)
        {
            for (var y = 0; y < rows; y++)
            {
                if (!source[x, y])
                {
                    continue;
                }
                for (var dx = -radius; dx <= radius; dx++)
                {
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        var targetX = x + dx;
                        var targetY = y + dy;
                        if (targetX >= 0 && targetX < columns && targetY >= 0 && targetY < rows)
                        {
                            result[targetX, targetY] = true;
                        }
                    }
                }
            }
        }
        return result;
    }

    private static PixelRect[] FindOverlayRectangles(
        bool[,] seeds,
        bool[,] expanded,
        int columns,
        int rows,
        int width,
        int height,
        int fixedHeader,
        int fixedFooter)
    {
        var visited = new bool[columns, rows];
        var rectangles = new List<PixelRect>();
        var directions = new[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        for (var startX = 0; startX < columns; startX++)
        {
            for (var startY = 0; startY < rows; startY++)
            {
                if (!expanded[startX, startY] || visited[startX, startY])
                {
                    continue;
                }
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((startX, startY));
                visited[startX, startY] = true;
                var minX = startX;
                var maxX = startX;
                var minY = startY;
                var maxY = startY;
                var seedCount = 0;
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    seedCount += seeds[current.X, current.Y] ? 1 : 0;
                    minX = Math.Min(minX, current.X);
                    maxX = Math.Max(maxX, current.X);
                    minY = Math.Min(minY, current.Y);
                    maxY = Math.Max(maxY, current.Y);
                    foreach (var (dx, dy) in directions)
                    {
                        var nextX = current.X + dx;
                        var nextY = current.Y + dy;
                        if (nextX >= 0 && nextX < columns && nextY >= 0 && nextY < rows
                            && expanded[nextX, nextY] && !visited[nextX, nextY])
                        {
                            visited[nextX, nextY] = true;
                            queue.Enqueue((nextX, nextY));
                        }
                    }
                }
                if (seedCount < 2)
                {
                    continue;
                }
                var x = Math.Max(0, minX * OverlayTileSize - OverlayTileSize);
                var y = Math.Max(fixedHeader, minY * OverlayTileSize - OverlayTileSize);
                var right = Math.Min(width, (maxX + 1) * OverlayTileSize + OverlayTileSize);
                var bottom = Math.Min(height - fixedFooter, (maxY + 1) * OverlayTileSize + OverlayTileSize);
                var rectangle = new PixelRect(x, y, right - x, bottom - y);
                if (!rectangle.IsEmpty && checked((long)rectangle.Width * rectangle.Height) <= checked((long)width * height / 3))
                {
                    rectangles.Add(rectangle);
                }
            }
        }
        return rectangles.ToArray();
    }
}
