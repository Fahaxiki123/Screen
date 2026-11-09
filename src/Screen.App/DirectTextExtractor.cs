using System.Runtime.InteropServices;
using System.Windows.Automation;
using Screen.Core;

namespace Screen.App;

internal static class DirectTextExtractor
{
    public static async Task<OcrDocument?> TryExtractAsync(
        WindowTarget? window,
        PixelRect screenSelection,
        CancellationToken cancellationToken)
    {
        if (window is null || window.Handle == 0)
        {
            return null;
        }
        try
        {
            // Cancel the underlying UIA work when the time budget expires instead
            // of leaving a background task running against a possibly hung window.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(550));
            return await Task.Run(
                    () => ExtractCore(window, screenSelection, timeout.Token),
                    timeout.Token)
                .WaitAsync(TimeSpan.FromMilliseconds(550), cancellationToken)
                .ConfigureAwait(false);
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

        var textPatternResult = TryReadDocumentText(root, screenSelection);
        if (textPatternResult is not null)
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
            if (intersection.IsEmpty || IntersectionRatio(bounds, intersection) < 0.45)
            {
                continue;
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

    private static OcrDocument? TryReadDocumentText(AutomationElement root, PixelRect selection)
    {
        if (!root.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
            || patternObject is not TextPattern pattern)
        {
            return null;
        }
        var text = pattern.DocumentRange.GetText(-1).Trim();
        if (text.Count(character => !char.IsWhiteSpace(character)) < 3)
        {
            return null;
        }
        var rootBounds = ToPixelRect(root.Current.BoundingRectangle);
        if (rootBounds.Intersect(selection).IsEmpty)
        {
            return null;
        }
        var localBounds = new PixelRect(
            rootBounds.X - selection.X,
            rootBounds.Y - selection.Y,
            rootBounds.Width,
            rootBounds.Height);
        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lineHeight = Math.Max(1, localBounds.Height / Math.Max(1, rawLines.Length));
        var lines = rawLines.Select((line, index) =>
        {
            var bounds = new PixelRect(localBounds.X, localBounds.Y + index * lineHeight, localBounds.Width, lineHeight);
            return new OcrLine(line, bounds, [new OcrWord(line, bounds, 1)], 1);
        }).ToArray();
        return new OcrDocument(OcrQualityEvaluator.DetectLanguage(text), text, lines)
        {
            Confidence = 1,
            ProviderName = "直接读取界面文字"
        };
    }

    private static PixelRect ToPixelRect(System.Windows.Rect rectangle)
    {
        var left = (int)Math.Floor(rectangle.Left);
        var top = (int)Math.Floor(rectangle.Top);
        var right = (int)Math.Ceiling(rectangle.Right);
        var bottom = (int)Math.Ceiling(rectangle.Bottom);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static double IntersectionRatio(PixelRect bounds, PixelRect intersection)
    {
        var area = bounds.Width * (double)bounds.Height;
        return area <= 0 ? 0 : intersection.Width * (double)intersection.Height / area;
    }
}
