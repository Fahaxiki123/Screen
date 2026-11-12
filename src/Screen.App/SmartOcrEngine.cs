using Screen.Core;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Screen.App;

internal sealed class SmartOcrEngine(
    IOcrEngine local,
    TencentCloudOcrEngine cloud,
    JsonSettingsStore settingsStore,
    CredentialService credentials,
    Func<string, string?>? readCredential = null) : IOcrEngine, IDisposable
{
    private readonly Func<string, string?> _readCredential = readCredential ?? credentials.Read;

    public IReadOnlyList<string> AvailableLanguages { get; } = ["auto"];

    public bool IsLanguageAvailable(string languageTag) => true;

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
        var secretId = _readCredential(TranslationProfile.TencentSecretIdCredentialName);
        var secretKey = _readCredential(TranslationProfile.TencentSecretKeyCredentialName);
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
            if (cloudResult is null)
            {
                return localResult is null
                    ? throw new InvalidOperationException("本地 OCR 与云端 OCR 均不可用。")
                    : localResult with { ProviderName = "PP-OCRv6 本地（云端已回退）" };
            }
            return localResult is null
                ? cloudResult
                : OcrQualityEvaluator.Fuse(localResult, cloudResult);
        }

        var localDocument = await TryLocalAsync(local, image, languageTag, purpose, cancellationToken)
            .ConfigureAwait(false);
        if (localDocument is not null
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
        if (enhanced is null)
        {
            return localDocument is null
                ? throw new InvalidOperationException("本地 OCR 与云端 OCR 均不可用。")
                : localDocument with { ProviderName = "PP-OCRv6 本地（云端已回退）" };
        }
        return localDocument is null
            ? enhanced
            : OcrQualityEvaluator.Fuse(localDocument, enhanced);
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

    private static async Task<OcrDocument?> TryLocalAsync(
        IOcrEngine local,
        PixelBuffer image,
        string languageTag,
        OcrRecognitionPurpose purpose,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RecognizeLocalAsync(local, image, languageTag, purpose, cancellationToken)
                .ConfigureAwait(false);
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
            return null;
        }
    }

    private async Task<OcrDocument?> TryCloudAsync(
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
            return await cloud.RecognizeAsync(image, secretId, secretKey, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogWarning($"Cloud OCR failed, falling back to local result if available: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    public void Dispose() => (local as IDisposable)?.Dispose();
}
