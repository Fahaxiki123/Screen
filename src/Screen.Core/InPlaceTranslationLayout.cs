using System.Text;

namespace Screen.Core;

public sealed record TranslationRegion(string Text, PixelRect Bounds, double LineHeight);

public sealed record TranslatedRegion(TranslationRegion Source, string Text);

public static class InPlaceTranslationLayout
{
    public static IReadOnlyList<TranslationRegion> CreateRegions(OcrDocument document, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        var lines = document.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => line with { Text = line.Text.Trim(), Bounds = Clip(line.Bounds, width, height) })
            .Where(line => !line.Bounds.IsEmpty)
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToArray();
        if (lines.Length == 0)
        {
            var text = OcrTextFormatter.NormalizeRaw(document.Text);
            if (text.Length == 0)
            {
                text = OcrTextFormatter.NormalizeRaw(string.Join('\n', document.Lines.Select(line => line.Text)));
            }
            return text.Length == 0 ? [] : [new TranslationRegion(text, new PixelRect(0, 0, width, height), Math.Min(24, height))];
        }

        var paragraphs = new List<List<OcrLine>>();
        foreach (var line in lines)
        {
            // Search all active columns instead of joining adjacent entries in OCR reading order.
            var paragraph = paragraphs
                .Where(group => CanJoin(group[^1], line))
                .OrderBy(group => line.Bounds.Y - group[^1].Bounds.Bottom)
                .ThenBy(group => Math.Abs(line.Bounds.X - group[^1].Bounds.X))
                .FirstOrDefault();
            if (paragraph is null)
            {
                paragraphs.Add([line]);
            }
            else
            {
                paragraph.Add(line);
            }
        }

        return paragraphs.Select(group =>
        {
            var left = group.Min(line => line.Bounds.X);
            var top = group.Min(line => line.Bounds.Y);
            var right = group.Max(line => line.Bounds.Right);
            var bottom = group.Max(line => line.Bounds.Bottom);
            var heights = group.Select(line => line.Bounds.Height).Order().ToArray();
            return new TranslationRegion(JoinLines(group), new PixelRect(left, top, right - left, bottom - top), heights[heights.Length / 2]);
        }).OrderBy(region => region.Bounds.Y).ThenBy(region => region.Bounds.X).ToArray();
    }

    private static bool CanJoin(OcrLine previous, OcrLine current)
    {
        var a = previous.Bounds;
        var b = current.Bounds;
        var lineHeight = Math.Min(a.Height, b.Height);
        var gap = b.Y - a.Bottom;
        if (gap < -lineHeight * 0.15 || gap > lineHeight * 0.65
            || Math.Max(a.Height, b.Height) > lineHeight * 1.35
            || Math.Abs(a.X - b.X) > lineHeight * 0.7)
        {
            return false;
        }

        // Short labels, menu rows, headings, and separate list items retain their own positions.
        if (previous.Text.Length < 12 || a.Width < lineHeight * 7
            || previous.Text[^1] is ':' or '：'
            || IsListItem(previous.Text) || IsListItem(current.Text))
        {
            return false;
        }
        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        return overlap >= Math.Min(a.Width, b.Width) * 0.8;
    }

    private static bool IsListItem(string text)
    {
        if (text[0] is '•' or '●' or '○' or '▪' or '▫' or '-' or '–' or '—')
        {
            return true;
        }
        var index = 0;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            index++;
        }
        return index > 0 && index < text.Length && text[index] is '.' or ')' or '、' or '．';
    }

    private static string JoinLines(List<OcrLine> lines)
    {
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            if (result.Length > 0 && !IsCjk(result[^1]) && !IsCjk(line.Text[0]))
            {
                result.Append(' ');
            }
            result.Append(line.Text);
        }
        return result.ToString();
    }

    private static bool IsCjk(char value) => value is >= '\u2e80' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    private static PixelRect Clip(PixelRect bounds, int width, int height)
    {
        if (bounds.IsEmpty)
        {
            return default;
        }
        // OCR providers are external inputs; avoid overflowing PixelRect.Right/Bottom on malformed bounds.
        var left = Math.Clamp((long)bounds.X, 0, width);
        var top = Math.Clamp((long)bounds.Y, 0, height);
        var right = Math.Clamp((long)bounds.X + bounds.Width, 0, width);
        var bottom = Math.Clamp((long)bounds.Y + bounds.Height, 0, height);
        return right <= left || bottom <= top ? default : new PixelRect((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
}
