namespace Screen.Core;

public static class OcrQualityEvaluator
{
    public static bool NeedsCloudEnhancement(
        OcrDocument document,
        PixelBuffer image,
        double confidenceThreshold)
    {
        var text = document.Text ?? string.Empty;
        var visible = text.Count(character => !char.IsWhiteSpace(character));
        if (visible < 3 || document.Lines.Count == 0)
        {
            return true;
        }

        var confidence = NormalizeConfidence(document.Confidence);
        if (confidence <= 0)
        {
            confidence = document.Lines.Count == 0
                ? 0
                : document.Lines.Average(line => NormalizeConfidence(line.Confidence));
        }
        if (confidence > 0 && confidence < Math.Clamp(confidenceThreshold, 0.5, 0.99))
        {
            return true;
        }

        // Whitespace (including the newlines between lines) is part of any
        // multi-line document; only count real C0 control characters and the
        // replacement character as corruption.
        var replacementCharacters = text.Count(character =>
            character == '\uFFFD'
            || (char.IsControl(character) && !char.IsWhiteSpace(character)));
        var usefulCharacters = text.Count(character => IsUsefulCharacter(character) && !char.IsWhiteSpace(character));
        if (replacementCharacters > 0 || usefulCharacters < visible * 0.72)
        {
            return true;
        }

        // A single weak line is common with a permissive local engine; escalate
        // only when a meaningful share of the lines is genuinely uncertain,
        // otherwise Smart mode would call the paid cloud service on almost
        // every image.
        var veryLowLines = document.Lines.Count(line =>
            line.Confidence > 0 && NormalizeConfidence(line.Confidence) < 0.68);
        if (veryLowLines * 5 >= document.Lines.Count)
        {
            return true;
        }

        return IsLowInformationImage(image) && confidence < 0.94;
    }

    public static OcrDocument Fuse(OcrDocument local, OcrDocument cloud)
    {
        if (cloud.Lines.Count == 0 || string.IsNullOrWhiteSpace(cloud.Text))
        {
            return local;
        }
        if (local.Lines.Count == 0 || string.IsNullOrWhiteSpace(local.Text))
        {
            return cloud;
        }

        var merged = new List<OcrLine>();
        var usedLocal = new HashSet<int>();
        foreach (var cloudLine in cloud.Lines)
        {
            var match = FindBestMatch(cloudLine, local.Lines, usedLocal);
            if (match < 0)
            {
                merged.Add(cloudLine);
                continue;
            }

            usedLocal.Add(match);
            var localLine = local.Lines[match];
            merged.Add(ChooseLine(localLine, cloudLine));
        }

        for (var index = 0; index < local.Lines.Count; index++)
        {
            if (!usedLocal.Contains(index) && NormalizeConfidence(local.Lines[index].Confidence) >= 0.72)
            {
                merged.Add(local.Lines[index]);
            }
        }

        var ordered = merged
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToArray();
        var text = string.Join(Environment.NewLine, ordered.Select(line => line.Text));
        var confidence = ordered.Length == 0
            ? 0
            : ordered.Average(line => NormalizeConfidence(line.Confidence));
        return new OcrDocument(DetectLanguage(text), text, ordered)
        {
            Confidence = confidence,
            ProviderName = "腾讯云增强 + PP-OCRv6",
            UsedOnlineService = true
        };
    }

    public static string DetectLanguage(string text)
    {
        var han = text.Count(character => character is >= '\u3400' and <= '\u9FFF');
        var kana = text.Count(character => character is >= '\u3040' and <= '\u30FF');
        var hangul = text.Count(character => character is >= '\uAC00' and <= '\uD7AF');
        if (kana > Math.Max(2, han / 5))
            return "ja-JP";
        if (hangul > 2)
            return "ko-KR";
        if (han > 0)
            return "zh-CN";
        return "en-US";
    }

    private static OcrLine ChooseLine(OcrLine local, OcrLine cloud)
    {
        var localConfidence = NormalizeConfidence(local.Confidence);
        var cloudConfidence = NormalizeConfidence(cloud.Confidence);
        var localVisible = local.Text.Count(character => !char.IsWhiteSpace(character));
        var cloudVisible = cloud.Text.Count(character => !char.IsWhiteSpace(character));

        if (cloudVisible >= Math.Max(1, (int)Math.Round(localVisible * 0.82))
            && cloudConfidence + 0.04 >= localConfidence)
        {
            return cloud;
        }
        return local;
    }

    private static int FindBestMatch(
        OcrLine target,
        IReadOnlyList<OcrLine> candidates,
        IReadOnlySet<int> excluded)
    {
        var best = -1;
        var bestScore = 0.0;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (excluded.Contains(index))
            {
                continue;
            }
            var score = OverlapScore(target.Bounds, candidates[index].Bounds);
            if (score > bestScore)
            {
                best = index;
                bestScore = score;
            }
        }
        return bestScore >= 0.28 ? best : -1;
    }

    private static double OverlapScore(PixelRect first, PixelRect second)
    {
        var intersection = first.Intersect(second);
        if (intersection.IsEmpty)
        {
            return 0;
        }
        var intersectionArea = intersection.Width * (double)intersection.Height;
        var smallerArea = Math.Min(
            first.Width * (double)first.Height,
            second.Width * (double)second.Height);
        return smallerArea <= 0 ? 0 : intersectionArea / smallerArea;
    }

    private static bool IsLowInformationImage(PixelBuffer image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            return true;
        }
        var step = Math.Max(1, Math.Max(image.Width, image.Height) / 500);
        long total = 0;
        long totalSquared = 0;
        var count = 0;
        for (var y = 0; y < image.Height; y += step)
        {
            for (var x = 0; x < image.Width; x += step)
            {
                var offset = y * image.Stride + x * 4;
                var luminance = (image.Pixels[offset] * 29
                                 + image.Pixels[offset + 1] * 150
                                 + image.Pixels[offset + 2] * 77) >> 8;
                total += luminance;
                totalSquared += luminance * luminance;
                count++;
            }
        }
        if (count == 0)
        {
            return true;
        }
        var mean = total / (double)count;
        var variance = totalSquared / (double)count - mean * mean;
        return variance < 180;
    }

    private static double NormalizeConfidence(double confidence) => confidence > 1
        ? Math.Clamp(confidence / 100, 0, 1)
        : Math.Clamp(confidence, 0, 1);

    private static bool IsUsefulCharacter(char character) =>
        char.IsLetterOrDigit(character)
        || char.IsWhiteSpace(character)
        || char.IsPunctuation(character)
        || character is >= '\u3400' and <= '\u9FFF'
        || character is >= '\u3040' and <= '\u30FF'
        || character is >= '\uAC00' and <= '\uD7AF';
}
