using Screen.Core;

namespace Screen.App;

internal sealed class SmartOcrEngine(
    IOcrEngine local,
    TencentCloudOcrEngine cloud,
    JsonSettingsStore settingsStore,
    ICredentialStore credentials,
    Func<string, string?>? readCredential = null) : IOcrEngine, IDisposable
{
    private readonly Func<string, string?> _readCredential = readCredential ?? credentials.ReadSecret;

    public IReadOnlyList<string> AvailableLanguages { get; } = ["auto"];

    public bool IsLanguageAvailable(string languageTag) => true;

    public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        local.WarmUpAsync(cancellationToken);

    public Task<OcrDocument> RecognizeAsync(
        PixelBuffer image,
        string languageTag,
        CancellationToken cancellationToken = default) =>
        RecognizeAsync(image, languageTag, OcrRecognitionPurpose.TextExtraction, cancellationToken);

    public async Task<OcrDocument> RecognizeAsync(
        PixelBuffer image,
        string languageTag,
        OcrRecognitionPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        var profile = settings.Ocr ?? new OcrProfile();
        var secretId = _readCredential(OcrProfile.TencentSecretIdCredentialName);
        var secretKey = _readCredential(OcrProfile.TencentSecretKeyCredentialName);
        var cloudAvailable = profile.Mode != OcrProviderMode.LocalOnly
                             && !string.IsNullOrWhiteSpace(secretId)
                             && !string.IsNullOrWhiteSpace(secretKey);
        var preferCloud = profile.Mode == OcrProviderMode.CloudFirst
                          || purpose == OcrRecognitionPurpose.Translation && profile.UseCloudForTranslation;

        if (!cloudAvailable)
        {
            return await RecognizeLocalAsync(local, image, languageTag, purpose, cancellationToken)
                .ConfigureAwait(false);
        }

        if (preferCloud)
        {
            var localTask = TryLocalAsync(local, image, languageTag, purpose, cancellationToken);
            var cloudTask = TryCloudAsync(
                image,
                secretId!,
                secretKey!,
                profile.CloudTimeoutMilliseconds,
                cancellationToken);
            await Task.WhenAll(localTask, cloudTask).ConfigureAwait(false);
            var localResult = await localTask.ConfigureAwait(false);
            var cloudResult = await cloudTask.ConfigureAwait(false);
            return MergeResults(localResult, cloudResult);
        }

        var localAttempt = await TryLocalAsync(local, image, languageTag, purpose, cancellationToken)
            .ConfigureAwait(false);
        if (localAttempt.Document is { } localDocument
            && !OcrQualityEvaluator.NeedsCloudEnhancement(
                localDocument,
                image,
                profile.CloudConfidenceThreshold))
        {
            return localDocument;
        }

        var enhanced = await TryCloudAsync(
                image,
                secretId!,
                secretKey!,
                profile.CloudTimeoutMilliseconds,
                cancellationToken)
            .ConfigureAwait(false);
        return MergeResults(localAttempt, enhanced);
    }

    private static OcrDocument MergeResults(RecognitionAttempt local, RecognitionAttempt cloud)
    {
        if (local.Document is { } localDocument)
        {
            return cloud.Document is { } cloudDocument
                ? OcrQualityEvaluator.Fuse(localDocument, cloudDocument)
                : localDocument with { ProviderName = "PP-OCRv6 本地（云端已回退）" };
        }
        if (cloud.Document is { } onlyCloud)
        {
            return onlyCloud;
        }
        throw new InvalidOperationException(
            $"本地 OCR：{local.Error!.Message}；腾讯云 OCR：{cloud.Error!.Message}",
            new AggregateException(local.Error, cloud.Error));
    }

    private static Task<OcrDocument> RecognizeLocalAsync(
        IOcrEngine local,
        PixelBuffer image,
        string languageTag,
        OcrRecognitionPurpose purpose,
        CancellationToken cancellationToken) =>
        local is PaddleOcrEngine paddle
            ? paddle.RecognizeAsync(image, languageTag, purpose, cancellationToken)
            : local.RecognizeAsync(image, languageTag, cancellationToken);

    private static async Task<RecognitionAttempt> TryLocalAsync(
        IOcrEngine local,
        PixelBuffer image,
        string languageTag,
        OcrRecognitionPurpose purpose,
        CancellationToken cancellationToken)
    {
        try
        {
            return new(await RecognizeLocalAsync(local, image, languageTag, purpose, cancellationToken)
                .ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Missing model files, ONNX runtime / native loader failures, or a
            // disposed engine must not discard a valid cloud OCR result.
            SimpleLogger.LogWarning($"Local OCR failed while cloud fallback is available: {exception.GetType().Name}: {exception.Message}");
            return new(null, exception);
        }
    }

    private async Task<RecognitionAttempt> TryCloudAsync(
        PixelBuffer image,
        string secretId,
        string secretKey,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMilliseconds, 800, 8000)));
        try
        {
            return new(await cloud.RecognizeAsync(image, secretId, secretKey, timeout.Token)
                .ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return new(null, new TimeoutException("识别请求已超时。", exception));
        }
        catch (Exception exception)
        {
            SimpleLogger.LogWarning($"Cloud OCR failed, falling back to local result if available: {exception.GetType().Name}: {exception.Message}");
            return new(null, exception);
        }
    }

    public void Dispose() => (local as IDisposable)?.Dispose();

    private sealed record RecognitionAttempt(OcrDocument? Document, Exception? Error);
}
