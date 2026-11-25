using System.Text;

namespace Screen.Core;

/// <summary>
/// 统一为非流式翻译请求施加单次超时上限，避免共享 HttpClient 的长超时阻塞回退链路。
/// </summary>
internal static class TranslationHttp
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    internal static Task<(int Status, string Reason, string Body)> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string timeoutMessage,
        CancellationToken cancellationToken) =>
        SendAsync(httpClient, request, timeoutMessage, DefaultRequestTimeout, cancellationToken);

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
/// 各翻译服务商的目标与源语言代码映射。
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

    public static string ToTencent(string language) => language switch
    {
        "简体中文" => "zh",
        "繁體中文" => "zh-TW",
        "English" => "en",
        "日本語" => "ja",
        "한국어" => "ko",
        "Français" => "fr",
        "Español" => "es",
        "Português" => "pt",
        "Deutsch" => "de",
        _ => "zh"
    };

    public static string ToMyMemoryTarget(string language) => language switch
    {
        "简体中文" => "zh-CN",
        "繁體中文" => "zh-TW",
        "English" => "en",
        "日本語" => "ja",
        "한국어" => "ko",
        "Français" => "fr",
        "Español" => "es",
        "Português" => "pt",
        "Deutsch" => "de",
        _ => "zh-CN"
    };

    public static string ToMyMemorySource(string sourceLanguage, string text, string? targetCode = null)
    {
        // 多语种原位回退时，整图 sourceLanguage 可能与当前单段文本语种不一致（如整图偏英文但单段为日/韩文）；
        // 优先依据当前文本自身特征识别，避免日/韩文段落被误按 en 翻译而原样残留。
        var textDetected = DetectDistinctScriptCode(text);
        var detected = textDetected
                       ?? ParseMyMemoryCode(sourceLanguage)
                       ?? ParseMyMemoryCode(OcrQualityEvaluator.DetectLanguage(text))
                       ?? "en";
        // 避免中英混排时源语种与目标语种相同导致 MyMemory 直接报 "PLEASE SELECT TWO DISTINCT LANGUAGES"。
        if (!string.IsNullOrWhiteSpace(targetCode)
            && IsSameLanguageFamily(detected, targetCode))
        {
            var fallbackByText = ParseMyMemoryCode(OcrQualityEvaluator.DetectLanguage(text));
            if (fallbackByText is not null && !IsSameLanguageFamily(fallbackByText, targetCode))
            {
                return fallbackByText;
            }
            return targetCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "en" : "zh-CN";
        }
        return detected;
    }

    private static string? DetectDistinctScriptCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var han = 0;
        var kana = 0;
        var hangul = 0;
        var latin = 0;
        foreach (var character in text)
        {
            if (character is >= '\u3400' and <= '\u9FFF')
            {
                han++;
            }
            else if (character is >= '\u3040' and <= '\u30FF')
            {
                kana++;
            }
            else if (character is >= '\uAC00' and <= '\uD7AF')
            {
                hangul++;
            }
            else if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                latin++;
            }
        }
        if (kana > Math.Max(1, han / 5) && (kana + han) * 3 >= latin)
        {
            return "ja";
        }
        if (hangul > 1 && hangul * 3 >= latin)
        {
            return "ko";
        }
        if (han > 0 && han * 3 >= latin)
        {
            return "zh-CN";
        }
        return null;
    }

    private static string? ParseMyMemoryCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }
        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }
        if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "ja";
        }
        if (language.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return "ko";
        }
        if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }
        if (language.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            return "fr";
        }
        if (language.StartsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            return "es";
        }
        if (language.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return "pt";
        }
        if (language.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            return "de";
        }
        return null;
    }

    private static bool IsSameLanguageFamily(string source, string target)
    {
        var sourcePrimary = source.Split('-')[0];
        var targetPrimary = target.Split('-')[0];
        return string.Equals(sourcePrimary, targetPrimary, StringComparison.OrdinalIgnoreCase);
    }
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
