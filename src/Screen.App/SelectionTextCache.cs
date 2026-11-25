using Screen.Core;

namespace Screen.App;

/// <summary>
/// 仅在单次截图覆盖层生命周期内存活的选区级版本化缓存。
/// 选区移动、增改标注、打码或撤销重做都会立即递增版本号并作废旧缓存与在途结果，防止遮挡隐私泄露。
/// </summary>
internal sealed class SelectionTextCache
{
    private CachedCardSource? _cardSource;
    private CachedImageOcr? _imageOcr;
    private CachedInPlaceTranslation? _inPlaceTranslation;

    public int Revision { get; private set; }

    /// <summary>
    /// 选区像素发生变化时调用：递增版本号并清空全部缓存槽。
    /// </summary>
    public int Invalidate()
    {
        Revision++;
        _cardSource = null;
        _imageOcr = null;
        _inPlaceTranslation = null;
        return Revision;
    }

    public bool TryStoreCardSource(
        int capturedRevision,
        string text,
        string languageTag,
        string providerName,
        bool cloudSatisfied,
        bool isEdited = false,
        bool isDirectText = false)
    {
        // A deliberate edit (including clearing the card) wins over subsequent image OCR for this revision.
        if (capturedRevision != Revision || (!isEdited && (_cardSource?.IsEdited == true || string.IsNullOrWhiteSpace(text))))
        {
            return false;
        }
        _cardSource = new CachedCardSource(
            capturedRevision,
            text,
            languageTag,
            providerName,
            cloudSatisfied,
            isEdited,
            isDirectText);
        return true;
    }

    public bool TryGetCardSource(bool requireCloudEnhancement, out CachedCardSource source)
    {
        if (_cardSource is { } cached
            && cached.Revision == Revision
            && (cached.IsEdited || !requireCloudEnhancement || cached.CloudSatisfied))
        {
            source = cached;
            return true;
        }
        source = null!;
        return false;
    }

    public bool TryStoreImageOcr(int capturedRevision, OcrDocument document, bool cloudSatisfied)
    {
        if (capturedRevision != Revision || document is null)
        {
            return false;
        }
        // 首次识别为空时不缓存空结果，允许用户重开 OCR 卡片或翻译时重新识别。
        if (document.Lines.Count == 0 && string.IsNullOrWhiteSpace(document.Text))
        {
            return false;
        }
        _imageOcr = new CachedImageOcr(capturedRevision, document, cloudSatisfied);
        return true;
    }

    public bool TryGetImageOcr(bool requireCloudEnhancement, out CachedImageOcr ocr)
    {
        if (_imageOcr is { } cached
            && cached.Revision == Revision
            && (!requireCloudEnhancement || cached.CloudSatisfied))
        {
            ocr = cached;
            return true;
        }
        ocr = null!;
        return false;
    }

    public bool TryStoreInPlaceTranslation(int capturedRevision, CachedInPlaceTranslation snapshot)
    {
        if (capturedRevision != Revision)
        {
            return false;
        }
        _inPlaceTranslation = snapshot;
        return true;
    }

    public bool TryGetInPlaceTranslation(string targetLanguage, out CachedInPlaceTranslation snapshot)
    {
        if (_inPlaceTranslation is { } cached
            && cached.Revision == Revision
            && string.Equals(cached.TargetLanguage, targetLanguage, StringComparison.Ordinal))
        {
            snapshot = cached;
            return true;
        }
        snapshot = null!;
        return false;
    }

    /// <summary>
    /// 判断当前配置在截图翻译场景下是否需要云端增强 OCR，避免本地提取缓存绕过用户开启的云增强策略。
    /// </summary>
    public static bool RequiresCloudTranslationOcr(AppSettings settings, ICredentialStore credentials)
    {
        var ocr = settings.Ocr ?? new OcrProfile();
        if (ocr.Mode == OcrProviderMode.LocalOnly)
        {
            return false;
        }
        var preferCloud = ocr.Mode == OcrProviderMode.CloudFirst || ocr.UseCloudForTranslation;
        if (!preferCloud)
        {
            return false;
        }
        var secretId = credentials.ReadCredential(TranslationProfile.TencentSecretIdCredentialName)?.Secret;
        var secretKey = credentials.ReadCredential(TranslationProfile.TencentSecretKeyCredentialName)?.Secret;
        return !string.IsNullOrWhiteSpace(secretId) && !string.IsNullOrWhiteSpace(secretKey);
    }
}

internal sealed record CachedCardSource(
    int Revision,
    string Text,
    string LanguageTag,
    string ProviderName,
    bool CloudSatisfied,
    bool IsEdited = false,
    bool IsDirectText = false);

internal sealed record CachedImageOcr(
    int Revision,
    OcrDocument Document,
    bool CloudSatisfied);

internal sealed record CachedInPlaceTranslation(
    int Revision,
    string TargetLanguage,
    string SourceLanguage,
    string SourceText,
    string CombinedText,
    string ProviderName,
    PixelBuffer RenderedImage,
    bool UsedFallback,
    string? CompletionDetail);
