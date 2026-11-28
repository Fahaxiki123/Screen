namespace Screen.Core;

public interface IScreenCaptureService : IDisposable
{
    Task<CaptureFrame> CaptureAtAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops any cached capture session.  Desktop Duplication allows only one
    /// session per output, so the screen recorder must take it over; the
    /// service recreates its session lazily on the next capture.
    /// </summary>
    void ReleaseSessions() { }

    /// <summary>
    /// Pre-loads native capture assemblies and graphics driver dependencies so
    /// the first user-triggered screenshot does not pay a multi-second cold start.
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public interface IOcrEngine
{
    IReadOnlyList<string> AvailableLanguages { get; }
    bool IsLanguageAvailable(string languageTag);
    Task<OcrDocument> RecognizeAsync(PixelBuffer image, string languageTag, CancellationToken cancellationToken = default);

    Task<OcrDocument> RecognizeAsync(
        PixelBuffer image,
        string languageTag,
        OcrRecognitionPurpose purpose,
        CancellationToken cancellationToken = default) =>
        RecognizeAsync(image, languageTag, cancellationToken);

    /// <summary>
    /// Pre-initializes the underlying OCR engine so the upcoming recognition request
    /// does not wait on model loading.
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText = null,
        CancellationToken cancellationToken = default);
}

public interface IBatchTranslationProvider
{
    Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<int>? completedCount = null,
        CancellationToken cancellationToken = default);
}

public interface IScrollStitcher
{
    StitchResult Stitch(IReadOnlyList<PixelBuffer> frames, CancellationToken cancellationToken = default);
}

public interface IPinWindowManager
{
    int Count { get; }
    void Pin(PixelBuffer image, PixelRect sourceBounds);
    void DisableClickThroughForAll();
    void CloseAll();
}
