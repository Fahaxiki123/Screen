using System.Runtime.InteropServices;
using System.IO;
using RapidOcrNet;
using Screen.Core;
using SkiaSharp;

namespace Screen.App;

internal sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RapidOcr? _ocr;
    private int _disposed;

    public IReadOnlyList<string> AvailableLanguages { get; } = ["auto"];

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool IsLanguageAvailable(string languageTag) => !IsDisposed;

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
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            // Dispose() turned away a waiter by disposing the gate; callers
            // expect cancellation, not a disposal fault.
            throw new OperationCanceledException("本地 OCR 引擎已释放。");
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // CreateEngine loads ~30 MB of ONNX models; it must run on the thread
            // pool, otherwise the first OCR call freezes the UI thread.
            return await Task.Run(() =>
                {
                    _ocr ??= CreateEngine();
                    return RecognizeCore(_ocr, image, cancellationToken);
                }, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose() already tore the gate down after a bounded wait.
            }
        }
    }

    private static RapidOcr CreateEngine()
    {
        var engine = new RapidOcr();
        try
        {
            var modelRoot = Path.Combine(AppContext.BaseDirectory, "models");
            var modelSet = RapidOcrModelSet.PPOCRv6Small with
            {
                DetModelPath = RequireModel(
                    Path.Combine(modelRoot, "v6", "PP-OCRv6_det_small.onnx")),
                ClsModelPath = RequireModel(
                    Path.Combine(modelRoot, "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")),
                RecModelPath = RequireModel(
                    Path.Combine(modelRoot, "v6", "PP-OCRv6_rec_small.onnx")),
                KeysPath = RequireModel(
                    Path.Combine(modelRoot, "v6", "ppocrv6_small_dict.txt"))
            };
            engine.InitModels(modelSet);
            return engine;
        }
        catch
        {
            try
            {
                engine.Dispose();
            }
            catch
            {
                // RapidOcrNet 3.0 may throw while disposing a partially initialized engine.
            }
            throw;
        }
    }

    private static string RequireModel(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "OCR 模型文件缺失，请使用最新正式版本覆盖安装。",
                path);
        }
        return path;
    }

    private static OcrDocument RecognizeCore(
        RapidOcr engine,
        PixelBuffer image,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = ToBitmap(image);
        var result = engine.Detect(bitmap, RapidOcrOptions.PPOCRv6 with
        {
            ReturnWordBox = true,
            TextScore = 0.30f
        });
        cancellationToken.ThrowIfCancellationRequested();

        var lines = result.TextBlocks
            .Select(block =>
            {
                var bounds = ToBounds(block.BoxPoints);
                var confidence = block.CharScores is { Length: > 0 }
                    ? block.CharScores.Average(score => (double)score)
                    : Math.Clamp(block.BoxScore, 0, 1);
                var words = block.WordResults is { Length: > 0 }
                    ? block.WordResults.Select(word => new OcrWord(
                        word.Text,
                        ToBounds(word.BoxPoints),
                        Math.Clamp(word.Score, 0, 1))).ToArray()
                    : [new OcrWord(block.Text, bounds, confidence)];
                return new OcrLine(block.Text.Trim(), bounds, words, confidence);
            })
            .Where(line => line.Text.Length > 0)
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToArray();
        var text = string.Join(Environment.NewLine, lines.Select(line => line.Text));
        return new OcrDocument(OcrQualityEvaluator.DetectLanguage(text), text, lines)
        {
            Confidence = lines.Length == 0 ? 0 : lines.Average(line => line.Confidence),
            ProviderName = "PP-OCRv6 本地"
        };
    }

    private static SKBitmap ToBitmap(PixelBuffer image)
    {
        var bitmap = new SKBitmap(new SKImageInfo(
            image.Width,
            image.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        var bytesPerRow = checked(image.Width * 4);
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(
                image.Pixels,
                y * image.Stride,
                bitmap.GetAddress(0, y),
                bytesPerRow);
        }
        return bitmap;
    }

    private static PixelRect ToBounds(IReadOnlyList<SKPointI> points)
    {
        if (points.Count == 0)
        {
            return new PixelRect(0, 0, 1, 1);
        }
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        // A running RecognizeAsync holds the gate across the native Detect call;
        // disposing RapidOcr under it would be use-after-free. Wait (bounded, so
        // a wedged inference cannot block shutdown forever) before releasing.
        // The wait is short because Dispose runs on the UI thread at exit — a
        // long one makes quitting look hung; on timeout we take the leak path
        // below, which is safe because the process is terminating anyway.
        var idle = _gate.Wait(TimeSpan.FromSeconds(1.5));
        try
        {
            if (idle)
            {
                _ocr?.Dispose();
            }
            // If the wait timed out, deliberately leak the native engine — the
            // process is shutting down and disposing it mid-inference can crash.
        }
        finally
        {
            _gate.Dispose();
        }
    }
}
