using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Screen.Core;

namespace Screen.App;

internal static class DirectTextExtractor
{
    private static readonly TimeSpan DefaultExtractionBudget = TimeSpan.FromMilliseconds(550);

    public static Task<OcrDocument?> TryExtractAsync(
        WindowTarget? window,
        PixelRect screenSelection,
        CancellationToken cancellationToken)
    {
        if (window is null || window.Handle == 0)
        {
            return Task.FromResult<OcrDocument?>(null);
        }
        return RunWithBudgetAsync(
            token => ExtractCore(window, screenSelection, token),
            DefaultExtractionBudget,
            cancellationToken);
    }

    internal static async Task<OcrDocument?> RunWithBudgetAsync(
        Func<CancellationToken, OcrDocument?> work,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        try
        {
            // Cancel the underlying UIA work when the time budget expires instead
            // of leaving a background task running against a possibly hung window.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);
            return await Task.Run(
                    () => work(timeout.Token),
                    timeout.Token)
                .WaitAsync(budget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Internal budget expiry must fall back to image OCR instead of
            // masquerading as caller cancellation.
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static OcrDocument? ExtractCore(
        WindowTarget window,
        PixelRect screenSelection,
        CancellationToken cancellationToken)
    {
        var root = AutomationElement.FromHandle(window.Handle);
        if (root is null)
        {
            return null;
        }

        var textPatternResult = TryReadDocumentText(root, screenSelection, cancellationToken, out var needsImageOcr);
        if (textPatternResult is not null || needsImageOcr)
        {
            return textPatternResult;
        }

        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var lines = new List<OcrLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(elements.Count, 600); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = elements[index];
            string text;
            System.Windows.Rect rectangle;
            bool offscreen;
            try
            {
                var current = element.Current;
                text = current.Name?.Trim() ?? string.Empty;
                rectangle = current.BoundingRectangle;
                offscreen = current.IsOffscreen;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            if (offscreen || text.Length == 0 || text.Length > 1200 || rectangle.IsEmpty)
            {
                continue;
            }

            var bounds = ToPixelRect(rectangle);
            var intersection = bounds.Intersect(screenSelection);
            if (intersection.IsEmpty)
            {
                continue;
            }
            // Name is indivisible: clipping its bounding box does not clip
            // the string. Let image OCR handle a partially selected control.
            if (!IsTextFullySelected(bounds, screenSelection))
            {
                return null;
            }
            var localBounds = new PixelRect(
                bounds.X - screenSelection.X,
                bounds.Y - screenSelection.Y,
                bounds.Width,
                bounds.Height);
            var key = $"{localBounds.X}:{localBounds.Y}:{text}";
            if (!seen.Add(key))
            {
                continue;
            }
            lines.Add(new OcrLine(
                text,
                localBounds,
                [new OcrWord(text, localBounds, 1)],
                1));
        }

        if (lines.Sum(line => line.Text.Count(character => !char.IsWhiteSpace(character))) < 3)
        {
            return null;
        }
        var ordered = lines.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).ToArray();
        var combined = string.Join(Environment.NewLine, ordered.Select(line => line.Text));
        return new OcrDocument(OcrQualityEvaluator.DetectLanguage(combined), combined, ordered)
        {
            Confidence = 1,
            ProviderName = "直接读取界面文字"
        };
    }

    private static OcrDocument? TryReadDocumentText(
        AutomationElement root,
        PixelRect selection,
        CancellationToken cancellationToken,
        out bool needsImageOcr)
    {
        needsImageOcr = false;
        if (!root.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
            || patternObject is not TextPattern pattern)
        {
            return null;
        }

        try
        {
            var rootBounds = ToPixelRect(root.Current.BoundingRectangle);
            if (rootBounds.Intersect(selection).IsEmpty)
            {
                return null;
            }

            var visibleRanges = pattern.GetVisibleRanges();
            if (visibleRanges is null || visibleRanges.Length == 0)
            {
                return null;
            }

            var candidates = new List<(string Text, PixelRect ScreenBounds)>();
            foreach (var visibleRange in visibleRanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryUnionBounds(visibleRange.GetBoundingRectangles()) is { } rangeBounds
                    && rangeBounds.Intersect(selection).IsEmpty)
                {
                    continue;
                }

                var cursor = visibleRange.Clone();
                cursor.MoveEndpointByRange(
                    TextPatternRangeEndpoint.End,
                    cursor,
                    TextPatternRangeEndpoint.Start);

                for (var step = 0; step < 250; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cursor.CompareEndpoints(
                            TextPatternRangeEndpoint.Start,
                            visibleRange,
                            TextPatternRangeEndpoint.End) >= 0)
                    {
                        break;
                    }

                    var lineRange = cursor.Clone();
                    lineRange.ExpandToEnclosingUnit(TextUnit.Line);
                    var wholeLineVisible = lineRange.CompareEndpoints(
                            TextPatternRangeEndpoint.Start, visibleRange, TextPatternRangeEndpoint.Start) >= 0
                        && lineRange.CompareEndpoints(
                            TextPatternRangeEndpoint.End, visibleRange, TextPatternRangeEndpoint.End) <= 0;

                    if (TryUnionBounds(lineRange.GetBoundingRectangles()) is { } lineBounds)
                    {
                        var intersection = lineBounds.Intersect(selection);
                        if (!intersection.IsEmpty)
                        {
                            // UIA rectangles describe visible portions, whereas
                            // GetText can return the entire expanded line. Do
                            // not send partially visible/selected line strings.
                            if (!wholeLineVisible || !IsTextFullySelected(lineBounds, selection))
                            {
                                needsImageOcr = true;
                                return null;
                            }
                            var lineText = lineRange.GetText(1200)?.Trim() ?? string.Empty;
                            if (lineText.Length > 0)
                            {
                                candidates.Add((lineText, lineBounds));
                            }
                        }
                    }

                    var previousStart = cursor.Clone();
                    if (cursor.Move(TextUnit.Line, 1) <= 0
                        || cursor.CompareEndpoints(
                            TextPatternRangeEndpoint.Start,
                            previousStart,
                            TextPatternRangeEndpoint.Start) <= 0)
                    {
                        break;
                    }
                }
            }

            return BuildDocumentFromBoundedLines(candidates, selection);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or COMException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return null;
        }
    }

    internal static OcrDocument? BuildDocumentFromBoundedLines(
        IEnumerable<(string Text, PixelRect ScreenBounds)> candidates,
        PixelRect screenSelection)
    {
        var lines = new List<OcrLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rawText, bounds) in candidates)
        {
            var text = rawText?.Trim() ?? string.Empty;
            if (text.Length == 0 || text.Length > 1200 || bounds.IsEmpty)
            {
                continue;
            }
            var intersection = bounds.Intersect(screenSelection);
            if (intersection.IsEmpty || !IsTextFullySelected(bounds, screenSelection))
            {
                continue;
            }
            var clipped = bounds.Intersect(screenSelection);
            var localBounds = new PixelRect(
                clipped.X - screenSelection.X,
                clipped.Y - screenSelection.Y,
                clipped.Width,
                clipped.Height);
            var key = $"{localBounds.X}:{localBounds.Y}:{text}";
            if (!seen.Add(key))
            {
                continue;
            }
            lines.Add(new OcrLine(
                text,
                localBounds,
                [new OcrWord(text, localBounds, 1)],
                1));
        }

        if (lines.Sum(line => line.Text.Count(character => !char.IsWhiteSpace(character))) < 3)
        {
            return null;
        }
        var ordered = lines.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).ToArray();
        var combined = string.Join(Environment.NewLine, ordered.Select(line => line.Text));
        return new OcrDocument(OcrQualityEvaluator.DetectLanguage(combined), combined, ordered)
        {
            Confidence = 1,
            ProviderName = "直接读取界面文字"
        };
    }

    internal static bool IsTextFullySelected(PixelRect bounds, PixelRect selection) =>
        !bounds.IsEmpty && bounds.Intersect(selection) == bounds;

    private static PixelRect? TryUnionBounds(System.Windows.Rect[]? rectangles)
    {
        if (rectangles is null || rectangles.Length == 0)
        {
            return null;
        }
        var union = new PixelRect(0, 0, 0, 0);
        foreach (var rectangle in rectangles)
        {
            if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                continue;
            }
            var current = ToPixelRect(rectangle);
            union = union.IsEmpty ? current : Union(union, current);
        }
        return union.IsEmpty ? null : union;
    }

    private static PixelRect Union(PixelRect a, PixelRect b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static PixelRect ToPixelRect(System.Windows.Rect rectangle)
    {
        var left = (int)Math.Floor(rectangle.Left);
        var top = (int)Math.Floor(rectangle.Top);
        var right = (int)Math.Ceiling(rectangle.Right);
        var bottom = (int)Math.Ceiling(rectangle.Bottom);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

}
