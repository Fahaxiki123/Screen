using Screen.Core;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Screen.App;

internal sealed class SmartOcrEngine(
    PaddleOcrEngine local,
    TencentCloudOcrEngine cloud,
    JsonSettingsStore settingsStore,
    CredentialService credentials) : IOcrEngine, IDisposable
{
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
        var secretId = credentials.Read(TranslationProfile.TencentSecretIdCredentialName);
        var secretKey = credentials.Read(TranslationProfile.TencentSecretKeyCredentialName);
        var cloudAvailable = profile.Mode != OcrProviderMode.LocalOnly
                             && !string.IsNullOrWhiteSpace(secretId)
                             && !string.IsNullOrWhiteSpace(secretKey);
        var preferCloud = profile.Mode == OcrProviderMode.CloudFirst
                          || purpose == OcrRecognitionPurpose.Translation && profile.UseCloudForTranslation;

        if (!cloudAvailable)
        {
            return await local.RecognizeAsync(image, languageTag, purpose, cancellationToken)
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

    private static async Task<OcrDocument?> TryLocalAsync(
        PaddleOcrEngine local,
        PixelBuffer image,
        string languageTag,
        OcrRecognitionPurpose purpose,
        CancellationToken cancellationToken)
    {
        try
        {
            return await local.RecognizeAsync(image, languageTag, purpose, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedOcrFailure(exception))
        {
            // Missing model files, native loader failures, or a disposed engine
            // must not take down the whole pipeline when a cloud result exists.
            return null;
        }
    }

    private static bool IsExpectedOcrFailure(Exception exception) => exception is
        FileNotFoundException or
        DllNotFoundException or
        ObjectDisposedException or
        InvalidOperationException or
        JsonException or
        HttpRequestException or
        ArgumentException;

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => local.Dispose();
}
