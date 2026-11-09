using System.Text;
using System.Text.RegularExpressions;

namespace Screen.Core;

/// <summary>
/// Keeps values that must stay byte-for-byte stable away from machine translation.
/// Generic translation engines often turn "$0.435" into "0美元.435" or lose a
/// decimal in "4.6x".  The values are replaced temporarily and restored after the
/// surrounding sentence has been translated.
/// </summary>
internal static partial class TranslationTextProtector
{
    private static readonly Regex ProtectedValuePattern = new(
        string.Join('|',
            @"(?i:https?)://[^\s<>\[\]{}]+",
            @"(?i:www\.)[^\s<>\[\]{}]+",
            @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
            @"(?<![\p{L}\p{N}_])@[A-Za-z0-9_]{1,30}",
            @"(?<![\p{L}\p{N}_])#[\p{L}\p{N}_]{1,100}",
            @"(?<![\p{L}\p{N}])(?:US|CA|AU|HK|NZ|SG)?[$＄]\s*\d[\d,]*(?:\.\d+)?(?:\s*[KMBT])?",
            @"(?<![\p{L}\p{N}])[€£¥￥₩₹]\s*\d[\d,]*(?:\.\d+)?(?:\s*[KMBT])?",
            @"(?<![\p{L}\p{N}])\d[\d,]*(?:\.\d+)?\s*(?:USD|EUR|GBP|CNY|RMB|JPY|KRW)(?![\p{L}\p{N}])",
            @"(?<![\p{L}\p{N}])\d+(?:\.\d+)?\s*(?:%|x|×)(?![\p{L}\p{N}])",
            @"\bV\d+(?:\.\d+)*(?:[-._][A-Z0-9]+)*\b",
            @"\b\d+(?:\.\d+)+(?:[-._][A-Z0-9]+)*\b",
            @"\b[A-Z][A-Z0-9]*[a-z][A-Za-z0-9]*[A-Z][A-Za-z0-9]*\b"),
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SuspectedDollarPattern = new(
        @"(?<![\p{L}\p{N}_])S(?=\s*\d+(?:\.\d+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PriceContextPattern = new(
        @"\b(price|pricing|cost|input|output|peak|token|million|billion|usd|dollar)s?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string NormalizeLikelyOcrArtifacts(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var suspectedDollars = SuspectedDollarPattern.Matches(text);
        var hasPriceEvidence = text.Contains('$')
                               || text.Contains('＄')
                               || suspectedDollars.Count >= 2
                               || PriceContextPattern.IsMatch(text);
        return hasPriceEvidence
            ? SuspectedDollarPattern.Replace(text, "$").Replace('＄', '$')
            : text.Replace('＄', '$');
    }

    internal static ProtectedTranslationText Protect(string text)
    {
        var values = new List<ProtectedTranslationValue>();
        var protectedText = ProtectedValuePattern.Replace(text, match =>
        {
            var marker = $"QZXKEEP{ToLetters(values.Count)}WZXQ";
            values.Add(new ProtectedTranslationValue(marker, match.Value));
            return marker;
        });
        return new ProtectedTranslationText(protectedText, values);
    }

    private static string ToLetters(int value)
    {
        var builder = new StringBuilder();
        do
        {
            builder.Insert(0, (char)('A' + value % 26));
            value = value / 26 - 1;
        }
        while (value >= 0);
        return builder.ToString();
    }
}

internal sealed record ProtectedTranslationValue(string Marker, string Original);

internal sealed class ProtectedTranslationText(
    string text,
    IReadOnlyList<ProtectedTranslationValue> values)
{
    private static readonly Regex AlteredMarkerPattern = new(
        @"Q\s*Z\s*X\s*K\s*E\s*E\s*P+[A-Z\s]{0,16}?W\s*Z\s*X\s*Q",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal string Text { get; } = text;

    internal string Restore(string translatedText)
    {
        var restored = translatedText;
        // The last-resort fallback below matches any marker-SHAPED token — an
        // intact marker of a later value matches it too.  Reserve every
        // value's intact marker (exact or whitespace-spaced) for its own
        // iteration, so a dropped or mutated marker can never steal a later
        // value's marker and displace its replacement.
        var reserved = values.Select(value => new Regex(
            "^" + string.Join(@"\s*", value.Marker.Select(character => Regex.Escape(character.ToString()))) + "$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)).ToArray();
        foreach (var value in values)
        {
            var exactIndex = restored.IndexOf(value.Marker, StringComparison.OrdinalIgnoreCase);
            if (exactIndex >= 0)
            {
                restored = restored.Remove(exactIndex, value.Marker.Length).Insert(exactIndex, value.Original);
                continue;
            }

            // Some engines insert spaces around an unknown identifier.  Accept
            // whitespace between marker characters while keeping the match very
            // specific, so ordinary translated text cannot be replaced.
            var looseMarker = string.Join(@"\s*", value.Marker.Select(character => Regex.Escape(character.ToString())));
            var looseMatch = Regex.Match(
                restored,
                looseMarker,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (looseMatch.Success)
            {
                restored = restored.Remove(looseMatch.Index, looseMatch.Length).Insert(looseMatch.Index, value.Original);
                continue;
            }

            // Translation services occasionally duplicate or mutate one of the
            // repeated P characters (for example QZXKEEPPCWZXQ).  Replace the
            // next marker-shaped token that is not reserved for another value;
            // markers whose original was dropped by the engine stay dropped —
            // misplacing them onto another value's slot is worse.
            foreach (Match candidate in AlteredMarkerPattern.Matches(restored))
            {
                if (reserved.Any(pattern => pattern.IsMatch(candidate.Value)))
                {
                    continue;
                }
                restored = restored.Remove(candidate.Index, candidate.Length).Insert(candidate.Index, value.Original);
                break;
            }
        }
        return AlteredMarkerPattern.Replace(restored, string.Empty);
    }
}
