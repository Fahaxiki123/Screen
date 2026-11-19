using System.Text;

namespace Screen.Core;

internal enum TranslationChunkBoundary
{
    Start,
    Paragraph,
    Sentence,
    Word,
    Fragment,
    InsideWord
}

internal sealed record TranslationChunk(string Text, string SeparatorBefore, TranslationChunkBoundary Boundary);

/// <summary>
/// Keeps source separators outside requests, so providers trimming each result
/// cannot erase paragraphs or glue translated words together. Protection tokens
/// are created for the whole document before this layer sees it.
/// </summary>
internal static class TranslationChunkLayout
{
    public static IReadOnlyList<TranslationChunk> ByCharacters(string text, int maximum) =>
        Create(text, line => TranslationTextChunker.SplitByCharacters(line, maximum),
            value => value.Length <= maximum);

    public static IReadOnlyList<TranslationChunk> ByUtf8Bytes(string text, int maximum) =>
        Create(text, line => TranslationTextChunker.SplitByUtf8Bytes(line, maximum),
            value => Encoding.UTF8.GetByteCount(value) <= maximum);

    private static IReadOnlyList<TranslationChunk> Create(
        string text,
        Func<string, IReadOnlyList<string>> split,
        Func<string, bool> fits)
    {
        var chunks = new List<TranslationChunk>();
        var separator = new StringBuilder();
        foreach (var raw in split(text))
        {
            var leading = raw.Length - raw.TrimStart().Length;
            separator.Append(raw.AsSpan(0, leading));
            var content = raw.Trim();
            if (content.Length == 0)
                continue;
            if (!fits(content))
                throw new InvalidOperationException("受保护的文字片段超过翻译接口的单次长度限制。");

            var before = separator.ToString();
            var boundary = BoundaryBefore(chunks.LastOrDefault()?.Text, content, before);
            chunks.Add(new TranslationChunk(content, before, boundary));
            separator.Clear();
            separator.Append(raw.AsSpan(leading + content.Length));
        }
        return chunks;
    }

    private static TranslationChunkBoundary BoundaryBefore(string? previous, string current, string separator)
    {
        if (previous is null)
            return TranslationChunkBoundary.Start;
        if (separator.Contains('\n') || separator.Contains('\r'))
            return TranslationChunkBoundary.Paragraph;
        if (previous[^1] is '.' or '!' or '?' or '。' or '！' or '？')
            return TranslationChunkBoundary.Sentence;
        if (separator.Length > 0)
            return TranslationChunkBoundary.Word;
        return IsWordCharacter(previous[^1]) && IsWordCharacter(current[0])
            ? TranslationChunkBoundary.InsideWord
            : TranslationChunkBoundary.Fragment;
    }

    public static void Append(
        StringBuilder output,
        TranslationChunk chunk,
        string translated,
        string targetLanguage,
        Func<string, string>? restore = null)
    {
        var content = translated.Trim();
        if (content.Length == 0)
            throw new InvalidOperationException("翻译服务返回了空片段，译文不完整。");

        if (output.Length == 0 || chunk.Boundary == TranslationChunkBoundary.Paragraph)
        {
            // Preserve explicit paragraph/line separators, including blank lines
            // and indentation. A provider's own outer whitespace is discarded.
            output.Append(chunk.SeparatorBefore);
        }
        else if (chunk.Boundary != TranslationChunkBoundary.InsideWord)
        {
            var previous = restore?.Invoke(output.ToString()) ?? output.ToString();
            var next = restore?.Invoke(content) ?? content;
            if (NeedsSpace(previous, next, targetLanguage))
                output.Append(' ');
        }
        output.Append(content);
    }

    private static bool NeedsSpace(string previous, string next, string targetLanguage)
    {
        if (previous.Length == 0 || next.Length == 0)
            return false;
        var last = previous[^1];
        var first = next[0];
        if (char.IsWhiteSpace(last) || char.IsWhiteSpace(first)
            || last is '(' or '[' or '{' or '（' or '【' or '《'
            || first is ',' or '.' or ':' or ';' or '!' or '?' or ')' or ']' or '}'
                or '，' or '。' or '：' or '；' or '！' or '？' or '）' or '】' or '》' or '、'
                or '\'' or '’')
            return false;

        var target = TranslationLanguageCodes.ToMyMemoryTarget(targetLanguage);
        if (target is "zh-CN" or "zh-TW" or "ja")
        {
            // Chinese/Japanese do not need spaces between translated clauses,
            // but retained Latin product names still need word boundaries.
            return IsWordCharacter(last) && IsWordCharacter(first);
        }
        return true;
    }

    private static bool IsWordCharacter(char value) =>
        char.IsLetterOrDigit(value)
        && value is not (>= '\u2e80' and <= '\u9fff')
        && value is not (>= '\uf900' and <= '\ufaff')
        && !char.IsSurrogate(value);
}
