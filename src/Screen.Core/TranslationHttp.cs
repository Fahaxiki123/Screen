using System.Text;

namespace Screen.Core;

/// <summary>
/// 限制非流式翻译请求的等待时间，并区分超时和用户取消。
/// </summary>
internal static class TranslationHttp
{
    internal static async Task<(int Status, string Reason, string Body)> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string timeoutMessage,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await httpClient.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, response.ReasonPhrase ?? string.Empty, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }
}

/// <summary>
/// 免费在线翻译的目标语言代码映射。
/// </summary>
internal static class TranslationLanguageCodes
{
    public static string ToEdge(string language) => language switch
    {
        "简体中文" => "zh-Hans",
        "繁體中文" => "zh-Hant",
        "English" => "en",
        "日本語" => "ja",
        "한국어" => "ko",
        "Français" => "fr",
        "Español" => "es",
        "Português" => "pt",
        "Deutsch" => "de",
        _ => "zh-Hans"
    };
}

/// <summary>
/// 按字符数或 UTF-8 字节数安全分块，不拆断代理对与占位保护标记。
/// </summary>
internal static class TranslationTextChunker
{
    public static IReadOnlyList<string> SplitByCharacters(string text, int maximumCharacters) =>
        Split(text, rune => rune.Utf16SequenceLength, maximumCharacters);

    public static IReadOnlyList<string> SplitByUtf8Bytes(string text, int maximumBytes) =>
        Split(text, rune => rune.Utf8SequenceLength, maximumBytes);

    private static IReadOnlyList<string> Split(string text, Func<Rune, int> measure, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        if (text.Length == 0)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            var lastBreak = -1;
            var size = 0;
            while (end < text.Length)
            {
                var rune = Rune.GetRuneAt(text, end);
                var runeSize = measure(rune);
                if (size + runeSize > maximum)
                {
                    if (end == start)
                    {
                        throw new ArgumentOutOfRangeException(nameof(maximum), "The limit must fit a complete Unicode character.");
                    }
                    break;
                }
                var next = end + rune.Utf16SequenceLength;
                if (rune.Value is '\n' or '\r' or '。' or '！' or '？' or '.' or '!' or '?' or ' ')
                {
                    lastBreak = next;
                }
                size += runeSize;
                end = next;
            }
            if (end == text.Length)
            {
                chunks.Add(text[start..]);
                break;
            }
            var splitAt = lastBreak > start ? lastBreak : end;
            splitAt = KeepProtectionMarkerWhole(text, start, splitAt);
            chunks.Add(text[start..splitAt]);
            start = splitAt;
        }
        return chunks;
    }

    private static int KeepProtectionMarkerWhole(string text, int chunkStart, int splitAt)
    {
        const string markerStartText = "QZXKEEP";
        const string markerEndText = "WZXQ";
        var markerStart = -1;
        var candidate = text.IndexOf(markerStartText, chunkStart, StringComparison.Ordinal);
        while (candidate >= chunkStart && candidate < splitAt)
        {
            markerStart = candidate;
            candidate = text.IndexOf(
                markerStartText,
                candidate + markerStartText.Length,
                StringComparison.Ordinal);
        }
        if (markerStart < 0)
        {
            return splitAt;
        }
        var markerEnd = text.IndexOf(markerEndText, markerStart + markerStartText.Length, StringComparison.Ordinal);
        if (markerEnd < 0 || markerEnd + markerEndText.Length <= splitAt)
        {
            return splitAt;
        }
        return markerStart > chunkStart ? markerStart : markerEnd + markerEndText.Length;
    }
}
