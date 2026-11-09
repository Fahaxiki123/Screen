using System.Text;
using System.Text.RegularExpressions;

namespace Screen.Core;

public static partial class OcrTextFormatter
{
    [GeneratedRegex(@"^(?:[•·●○▪▫\-–—]|(?:\d+|[A-Za-z])[.)、．])\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ListItemPattern();

    [GeneratedRegex(@"^(?:\d+(?:\.\d+)*|[一二三四五六七八九十]+)[.、．]\s*\S.{0,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    public static string Format(OcrDocument document)
    {
        if (document.Lines.Count == 0)
        {
            return NormalizeRaw(document.Text);
        }

        var lines = document.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .Select(line => (Line: line, Text: NormalizeLine(line.Text)))
            .Where(item => item.Text.Length > 0)
            .ToArray();
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var heights = lines.Select(item => Math.Max(1, item.Line.Bounds.Height)).Order().ToArray();
        var medianHeight = heights[heights.Length / 2];
        var leftEdge = lines.Min(item => item.Line.Bounds.X);
        var rightEdge = lines.Max(item => item.Line.Bounds.Right);
        var builder = new StringBuilder(lines.Sum(item => item.Text.Length + 1));
        for (var index = 0; index < lines.Length; index++)
        {
            var current = lines[index];
            if (index > 0)
            {
                var previous = lines[index - 1];
                var verticalGap = current.Line.Bounds.Y - previous.Line.Bounds.Bottom;
                var overlapsSameRow = current.Line.Bounds.Y < previous.Line.Bounds.Bottom - medianHeight * 0.35
                                      && current.Line.Bounds.X >= previous.Line.Bounds.Right;
                if (overlapsSameRow)
                {
                    builder.Append(' ');
                }
                else if (ShouldJoinWrappedLine(previous, current, rightEdge, medianHeight, verticalGap))
                {
                    if (NeedsWordSpace(previous.Text[^1], current.Text[0]))
                    {
                        builder.Append(' ');
                    }
                }
                else
                {
                    builder.AppendLine();
                    if (verticalGap >= medianHeight * 0.7)
                    {
                        builder.AppendLine();
                    }
                    AppendIndent(builder, current.Line.Bounds.X - leftEdge, medianHeight);
                }
            }
            else
            {
                AppendIndent(builder, current.Line.Bounds.X - leftEdge, medianHeight);
            }
            builder.Append(current.Text);
        }
        return builder.ToString().Trim();
    }

    public static string NormalizeRaw(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        return string.Join(
            Environment.NewLine,
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(NormalizeLine)
                .Where(line => line.Length > 0));
    }

    public static string NormalizeLine(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(trimmed.Length);
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
                continue;
            }

            while (index + 1 < trimmed.Length && char.IsWhiteSpace(trimmed[index + 1]))
            {
                index++;
            }
            var previous = builder.Length == 0 ? '\0' : builder[^1];
            var next = index + 1 < trimmed.Length ? trimmed[index + 1] : '\0';
            if (previous != '\0' && next != '\0' && NeedsWordSpace(previous, next))
            {
                builder.Append(' ');
            }
        }
        return builder.ToString();
    }

    private static bool NeedsWordSpace(char previous, char next)
    {
        if (IsCjk(previous) && IsCjk(next))
        {
            return false;
        }
        if (IsClosingPunctuation(next) || IsOpeningPunctuation(previous))
        {
            return false;
        }
        return true;
    }

    private static bool EndsParagraph(string text) => text[^1] is
        '。' or '！' or '？' or '；' or '!' or '?' or ';';

    private static bool ShouldJoinWrappedLine(
        (OcrLine Line, string Text) previous,
        (OcrLine Line, string Text) current,
        int rightEdge,
        int medianHeight,
        int verticalGap) =>
        verticalGap <= medianHeight * 0.55
        && previous.Line.Bounds.Right >= rightEdge - medianHeight * 2
        && current.Line.Bounds.X <= previous.Line.Bounds.X + medianHeight * 1.5
        && !EndsParagraph(previous.Text)
        && !ListItemPattern().IsMatch(current.Text)
        && !HeadingPattern().IsMatch(previous.Text)
        && !HeadingPattern().IsMatch(current.Text);

    private static void AppendIndent(StringBuilder builder, int offset, int medianHeight)
    {
        var levels = Math.Clamp((int)Math.Round(offset / Math.Max(1.0, medianHeight * 1.35)), 0, 6);
        builder.Append(' ', levels * 2);
    }

    private static bool IsCjk(char character) => character is
        >= '\u3400' and <= '\u9FFF'
        or >= '\u3040' and <= '\u30FF'
        or >= '\uAC00' and <= '\uD7AF';

    private static bool IsClosingPunctuation(char character) => character is
        ',' or '.' or ':' or ';' or '!' or '?' or ')' or ']' or '}'
        or '，' or '。' or '：' or '；' or '！' or '？' or '）' or '】' or '》' or '、';

    private static bool IsOpeningPunctuation(char character) => character is
        '(' or '[' or '{' or '（' or '【' or '《';
}
