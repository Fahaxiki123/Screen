using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Shapes;
using Path = System.IO.Path;
using Microsoft.Win32;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Event handlers and user actions for CaptureOverlayWindow.
/// </summary>
internal sealed partial class CaptureOverlayWindow
{
    private void SelectTool(CaptureTool tool)
    {
        if (!_surface.IsSelectionCommitted)
        {
            return;
        }
        CancelTextOperations(keepInPlace: _inPlaceResult is not null);
        _surface.ClearAnnotationSelection();
        ShowAnnotationTool(tool);
    }

    private void ShowAnnotationTool(CaptureTool tool)
    {
        _surface.Tool = tool;
        _stylePanel.Visibility = Visibility.Visible;
        ConfigureStylePanel(tool);
        UpdateDrawingSelection();
        PositionFloatingPanels();
    }

    private void SyncSelectedStyle()
    {
        if (_applyingSelectedStyle || _surface.SelectedAnnotationStyle is not { } style
            || _surface.SelectedAnnotationKind is not { } kind)
        {
            return;
        }

        var scale = Math.Max(1.0, _frame.DpiX / 96.0);
        _color = style.Argb;
        switch (kind)
        {
            case AnnotationKind.Number:
                _numberSize = style.FontSize / scale;
                break;
            case AnnotationKind.Text:
                _fontSize = style.FontSize / scale;
                _textVariant = style.Variant;
                break;
            case AnnotationKind.Mosaic:
                _mosaicSize = style.Thickness / scale;
                _mosaicIntensity = style.Intensity;
                _mosaicVariant = style.Variant % 100;
                _surface.MosaicMode = style.Variant >= 100 ? MosaicDrawMode.Brush : MosaicDrawMode.Rectangle;
                break;
            default:
                _thickness = style.Thickness / scale;
                break;
        }

        ShowAnnotationTool(kind switch
        {
            AnnotationKind.Rectangle => CaptureTool.Rectangle,
            AnnotationKind.Ellipse => CaptureTool.Ellipse,
            AnnotationKind.Line => CaptureTool.Line,
            AnnotationKind.Arrow => CaptureTool.Arrow,
            AnnotationKind.Pen => CaptureTool.Pen,
            AnnotationKind.Text => CaptureTool.Text,
            AnnotationKind.Number => CaptureTool.Number,
            AnnotationKind.Mosaic => CaptureTool.Mosaic,
            _ => CaptureTool.Select
        });
    }

    private void UpdateStyle(bool applyToSelection = false)
    {
        var pixelScale = Math.Max(1.0, _frame.DpiX / 96.0);
        var thickness = (_surface.Tool == CaptureTool.Mosaic ? _mosaicSize : _thickness) * pixelScale;
        var variant = _surface.Tool switch
        {
            CaptureTool.Mosaic => _mosaicVariant + (_surface.MosaicMode == MosaicDrawMode.Brush ? 100 : 0),
            CaptureTool.Text => _textVariant,
            _ => 0
        };
        var fontSize = (_surface.Tool == CaptureTool.Number ? _numberSize : _fontSize) * pixelScale;
        var style = new AnnotationStyle(_color, thickness, fontSize, variant, _mosaicIntensity);
        _surface.CurrentStyle = style;
        if (!applyToSelection || _surface.SelectedAnnotationStyle is not { } selected)
        {
            return;
        }

        var updated = _surface.SelectedAnnotationKind switch
        {
            AnnotationKind.Number => selected with { Argb = style.Argb, FontSize = style.FontSize },
            AnnotationKind.Text => selected with { Argb = style.Argb, FontSize = style.FontSize, Variant = style.Variant },
            AnnotationKind.Mosaic => selected with { Thickness = style.Thickness, Variant = style.Variant, Intensity = style.Intensity },
            _ => selected with { Argb = style.Argb, Thickness = style.Thickness }
        };
        _applyingSelectedStyle = true;
        try
        {
            _surface.ApplyStyleToSelected(updated);
        }
        finally
        {
            _applyingSelectedStyle = false;
        }
    }

    private void CopyCurrentColor()
    {
        var argb = _surface.HoverColor;
        var text = _surface.ColorRgbMode ? ColorText.ToRgb(argb) : ColorText.ToHex(argb);
        try
        {
            BitmapUtilities.SetClipboardTextWithRetry(text);
            _surface.ColorStatus = $"✓ 已复制 {text}";
        }
        catch (Exception)
        {
            _surface.ColorStatus = "复制失败 · 剪贴板被占用";
        }
        _surface.InvalidateVisual();
    }

    private async void ShowOcr(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        try
        {
            _translationPopup.IsOpen = false;
            CancelTranslation();
            ClearInPlaceTranslation();
            _translationCard.Visibility = Visibility.Collapsed;
            _ocrCard.FinishEditing();
            _ocrCard.ResetPosition();
            _ocrCard.Visibility = Visibility.Visible;
            PositionFloatingPanels();
            // 优先复用卡片原文（保留用户刚手工纠错的内容）；空识别不会被写入缓存，重开会重新识别。
            if (_textCache.TryGetCardSource(requireCloudEnhancement: false, out var cachedSource))
            {
                var linkedImageDoc = !cachedSource.IsEdited
                                     && !cachedSource.IsDirectText
                                     && _textCache.TryGetImageOcr(requireCloudEnhancement: false, out var linkedImage)
                    ? linkedImage.Document
                    : null;
                _ocrCard.ShowCachedResult(
                    cachedSource.Revision,
                    cachedSource.Text,
                    cachedSource.LanguageTag,
                    cachedSource.ProviderName,
                    linkedImageDoc,
                    isDirectText: cachedSource.IsDirectText,
                    isEdited: cachedSource.IsEdited);
                PositionFloatingPanels();
                return;
            }
            if (_textCache.TryGetImageOcr(requireCloudEnhancement: false, out var cachedImage))
            {
                _ocrCard.ShowCachedResult(
                    cachedImage.Revision,
                    OcrTextFormatter.Format(cachedImage.Document),
                    cachedImage.Document.LanguageTag,
                    cachedImage.Document.ProviderName,
                    cachedImage.Document,
                    isDirectText: false);
                PositionFloatingPanels();
                return;
            }
            await _ocrCard.RecognizeAsync();
            PositionFloatingPanels();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"OCR 识别失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleTranslationMenu(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        _ocrCard.Visibility = Visibility.Collapsed;
        _translationPopup.IsOpen = !_translationPopup.IsOpen;
    }

    private async void TranslateWithCurrentLanguage(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        try
        {
            _ocrCard.Visibility = Visibility.Collapsed;
            _translationPopup.IsOpen = false;
            await TranslateSelectionAsync(_settingsStore.Load().Translation.TargetLanguage);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"翻译失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task TranslateFromOcrCardAsync(OcrTranslateRequest request)
    {
        if (_captureClosed || !_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        // 选区或标注在打开 OCR 卡后已变化时，拒绝使用过期原文，重新按最新像素识别。
        if (request.Revision != _textCache.Revision)
        {
            await TranslateSelectionAsync(_settingsStore.Load().Translation.TargetLanguage, displayModeOverride: TranslationDisplayMode.Card);
            return;
        }
        try
        {
            var requireCloud = SelectionTextCache.RequiresCloudTranslationOcr(_settingsStore.Load(), _credentialService);
            var cloudSatisfied = !requireCloud || request.Document?.UsedOnlineService == true;
            if (!request.IsEdited && !request.IsDirectText && request.Document is { } document)
            {
                _textCache.TryStoreImageOcr(request.Revision, document, cloudSatisfied);
            }
            _textCache.TryStoreCardSource(
                request.Revision,
                request.Text,
                request.LanguageTag,
                request.ProviderName,
                cloudSatisfied,
                isEdited: request.IsEdited,
                isDirectText: request.IsDirectText);
            var targetLanguage = _settingsStore.Load().Translation.TargetLanguage;
            await TranslateSelectionAsync(
                targetLanguage,
                displayModeOverride: TranslationDisplayMode.Card,
                explicitSourceText: request.Text,
                explicitSourceLanguage: request.LanguageTag,
                explicitSourceProvider: request.ProviderName);
        }
        catch (Exception ex)
        {
            SimpleLogger.LogWarning(FormatSafeTranslationErrorLog(_settingsStore.Load().Translation.Provider, ex));
            _translationCard.SetError($"翻译请求失败：{ex.Message}");
        }
    }

    private async Task RetranslateCurrentCardAsync(string? targetLanguage = null)
    {
        if (_captureClosed || !_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        var language = targetLanguage ?? _translationCard.TargetLanguage;
        // 仅当卡片原文属于当前选区版本时才复用；若用户已将原文主动删空，绝不偷偷恢复旧缓存提交。
        if (_translationCard.HasSourceLoaded && _translationCard.SourceRevision == _textCache.Revision)
        {
            var currentSource = _translationCard.CurrentSourceText;
            var detectedLanguage = OcrQualityEvaluator.DetectLanguage(currentSource);
            var isEdited = _translationCard.IsSourceEdited;
            if (isEdited)
            {
                _textCache.TryStoreCardSource(_translationCard.SourceRevision, currentSource, detectedLanguage,
                    "已编辑文本", cloudSatisfied: false, isEdited: true);
                _translationCard.AcceptSourceEdit();
            }
            if (string.IsNullOrWhiteSpace(currentSource))
            {
                _translationCard.SetError("原文为空，请输入文字后再重译。");
                PositionFloatingPanels();
                return;
            }
            var provider = isEdited ? "已编辑原文" : "缓存原文";
            await TranslateSelectionAsync(
                language,
                displayModeOverride: TranslationDisplayMode.Card,
                explicitSourceText: currentSource,
                explicitSourceLanguage: detectedLanguage,
                explicitSourceProvider: provider);
            return;
        }
        await TranslateSelectionAsync(language, displayModeOverride: TranslationDisplayMode.Card);
    }

    private async Task TranslateSelectionAsync(
        string targetLanguage,
        TranslationDisplayMode? displayModeOverride = null,
        string? explicitSourceText = null,
        string? explicitSourceLanguage = null,
        string? explicitSourceProvider = null)
    {
        if (_captureClosed || !_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        _ocrCard.CancelRecognition();
        _ocrCard.Visibility = Visibility.Collapsed;
        _surface.ClearAnnotationSelection();
        _surface.Tool = CaptureTool.Select;
        UpdateDrawingSelection();
        var settings = _settingsStore.Load();
        var effectiveDisplayMode = displayModeOverride ?? settings.TranslationDisplayMode;
        _inPlaceMode = effectiveDisplayMode == TranslationDisplayMode.InPlace;
        // 仅取消不释放前一个令牌源，防止在途任务创建链接令牌时抛出 ObjectDisposedException。
        _translationOperation?.Cancel();
        var operation = _translationOperation = new CancellationTokenSource();
        var cancellationToken = operation.Token;
        var capturedRevision = _textCache.Revision;
        ClearInPlaceTranslation();
        if (_inPlaceMode)
        {
            _translationCard.Visibility = Visibility.Collapsed;
            SetInPlaceStatus("正在提取文字…");
        }
        else
        {
            var hasExplicitSource = !string.IsNullOrWhiteSpace(explicitSourceText);
            _translationCard.Begin(targetLanguage, preserveSource: hasExplicitSource);
            if (hasExplicitSource)
            {
                _translationCard.SetSourceText(explicitSourceText!, capturedRevision);
            }
        }
        _translationBusy = true;
        UpdateProcessingIndicator();
        _stylePanel.Visibility = Visibility.Collapsed;
        PositionFloatingPanels();

        _translationCard.SetProvider(FormatProviderDisplayName(settings.Translation.Provider));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_inPlaceMode)
            {
                await TranslateInPlaceAsync(settings, targetLanguage, operation, capturedRevision);
                return;
            }

            string sourceText;
            string sourceLanguage;
            string extractedBy;
            var requireCloud = SelectionTextCache.RequiresCloudTranslationOcr(settings, _credentialService);

            if (!string.IsNullOrWhiteSpace(explicitSourceText))
            {
                sourceText = explicitSourceText;
                sourceLanguage = string.IsNullOrWhiteSpace(explicitSourceLanguage)
                    ? OcrQualityEvaluator.DetectLanguage(sourceText)
                    : explicitSourceLanguage;
                extractedBy = string.IsNullOrWhiteSpace(explicitSourceProvider) ? "缓存原文" : explicitSourceProvider;
            }
            else if (_textCache.TryGetCardSource(requireCloud, out var cachedSource))
            {
                sourceText = cachedSource.Text;
                sourceLanguage = cachedSource.LanguageTag;
                extractedBy = string.IsNullOrWhiteSpace(cachedSource.ProviderName) ? "缓存原文" : cachedSource.ProviderName;
            }
            else
            {
                var directDocument = await TryExtractDirectTextAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectText = directDocument is not null;
                var document = directDocument ?? await OcrCard.RecognizeAutomaticallyAsync(
                    _ocrEngine, _surface.RenderSelection(), purpose: OcrRecognitionPurpose.Translation,
                    cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (capturedRevision != _textCache.Revision)
                {
                    return;
                }
                sourceText = OcrTextFormatter.Format(document);
                sourceLanguage = document.LanguageTag;
                extractedBy = string.IsNullOrWhiteSpace(document.ProviderName) ? "文字识别" : document.ProviderName;
                var cloudSatisfied = !requireCloud || document.UsedOnlineService || !isDirectText;
                _textCache.TryStoreCardSource(
                    capturedRevision,
                    sourceText,
                    sourceLanguage,
                    extractedBy,
                    cloudSatisfied,
                    isEdited: false,
                    isDirectText: isDirectText);
                if (!isDirectText)
                {
                    _textCache.TryStoreImageOcr(capturedRevision, document, cloudSatisfied: true);
                }
            }

            if (capturedRevision != _textCache.Revision)
            {
                return;
            }

            _translationCard.SetSourceLanguage(sourceLanguage);
            _translationCard.SetSourceText(sourceText, capturedRevision);
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                _translationCard.SetStatus("未识别到可翻译文字");
                _translationCard.Complete();
                return;
            }
            _translationCard.SetStatus(
                $"文字提取完成（{extractedBy}，用时 {timer.Elapsed.TotalSeconds:F1} 秒）· 正在翻译…", "正在翻译…");
            var extractionSeconds = timer.Elapsed.TotalSeconds;
            double? firstTokenSeconds = null;
            var progress = new Progress<string>(text =>
            {
                if (operation.IsCancellationRequested || !ReferenceEquals(_translationOperation, operation) || !_translationBusy)
                {
                    return;
                }
                firstTokenSeconds ??= timer.Elapsed.TotalSeconds - extractionSeconds;
                _translationCard.SetText(text);
            });
            var result = await _translationProvider.TranslateAsync(
                sourceText,
                sourceLanguage,
                settings.Translation with { TargetLanguage = targetLanguage },
                TranslationCredentialStore.ReadSaved(settings.Translation, _credentialService),
                progress,
                cancellationToken);
            if (operation.IsCancellationRequested || capturedRevision != _textCache.Revision)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                _translationCard.SetError("翻译服务未返回译文，请重试。");
                PositionFloatingPanels();
                return;
            }
            if (result.UsedFallback)
            {
                SimpleLogger.LogWarning(FormatSafeFallbackLog(settings.Translation.Provider, result.FallbackProvider, result.Failures));
            }
            var translationSeconds = timer.Elapsed.TotalSeconds - extractionSeconds;
            var timing = firstTokenSeconds is { } firstToken
                ? $"提取 {extractionSeconds:F1}s · 翻译 {translationSeconds:F1}s（首字等待 {firstToken:F1}s）· 总用时 {timer.Elapsed.TotalSeconds:F1}s"
                : $"提取 {extractionSeconds:F1}s · 翻译 {translationSeconds:F1}s · 总用时 {timer.Elapsed.TotalSeconds:F1}s";
            _translationCard.SetText(result.Text);
            _translationCard.SetProvider(result.ProviderName);
            _translationCard.SetStatus(result.UsedFallback
                ? $"已使用 {result.ProviderName}（首选服务失败：{result.PrimaryFailure}）· {timing}"
                : $"翻译完成 · {timing}");
            _translationCard.Complete();
            PositionFloatingPanels();
        }
        catch (OperationCanceledException)
        {
            if (!operation.IsCancellationRequested)
            {
                SimpleLogger.LogWarning($"Translation timed out ({settings.Translation.Provider}).");
                if (_inPlaceMode)
                {
                    SetInPlaceStatus("翻译超时，请重试");
                }
                else
                {
                    _translationCard.SetError("翻译超时，请重试");
                }
            }
        }
        catch (Exception exception)
        {
            if (!operation.IsCancellationRequested && ReferenceEquals(_translationOperation, operation))
            {
                SimpleLogger.LogWarning(FormatSafeTranslationErrorLog(settings.Translation.Provider, exception));
                if (_inPlaceMode)
                {
                    SetInPlaceStatus($"翻译失败：{exception.Message}");
                }
                else
                {
                    _translationCard.SetError($"翻译请求失败：{exception.Message}");
                }
                PositionFloatingPanels();
            }
        }
        finally
        {
            if (ReferenceEquals(_translationOperation, operation))
            {
                _translationBusy = false;
                UpdateProcessingIndicator();
            }
        }
    }

    /// <summary>
    /// 日志仅使用各跳的结构化诊断，不读取可能回显截图文本、URL 或凭据的异常消息。
    /// </summary>
    internal static string FormatSafeTranslationErrorLog(TranslationProviderKind provider, Exception exception)
    {
        var attempts = exception is TranslationRouteException route
            ? string.Join(" -> ", route.Failures)
            : TranslationFailure.From(provider, exception).ToString();
        return $"Translation failed ({provider}): {attempts}";
    }

    internal static string FormatSafeFallbackLog(TranslationProviderKind primaryProvider, TranslationProviderKind? fallbackProvider,
        System.Collections.Generic.IReadOnlyList<TranslationFailure>? failures = null)
    {
        var attempts = failures is { Count: > 0 } ? ": " + string.Join(" -> ", failures) : string.Empty;
        return $"Translation fell back ({primaryProvider} -> {fallbackProvider?.ToString() ?? "Unknown"}){attempts}";
    }

    private static string FormatProviderDisplayName(TranslationProviderKind provider) =>
        provider == TranslationProviderKind.OpenAiCompatible ? "AI 自然翻译" : "免费在线翻译";

    private Task<OcrDocument?> TryExtractDirectTextAsync(CancellationToken cancellationToken)
    {
        var selection = _surface.Selection;
        if (selection.IsEmpty)
        {
            return Task.FromResult<OcrDocument?>(null);
        }
        var screenSelection = new PixelRect(
            _frame.ScreenBounds.X + selection.X,
            _frame.ScreenBounds.Y + selection.Y,
            selection.Width,
            selection.Height);
        return DirectTextExtractor.TryExtractAsync(
            _surface.UnmaskedTextWindow,
            screenSelection,
            cancellationToken);
    }

    private void Pin(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        var image = _surface.RenderSelection();
        var region = _surface.Selection;
        _pinManager.Pin(image, new PixelRect(
            _frame.ScreenBounds.X + region.X,
            _frame.ScreenBounds.Y + region.Y,
            region.Width,
            region.Height));
        Close();
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        var settings = _settingsStore.Load();
        var initialDirectory = Directory.Exists(settings.SaveDirectory)
            ? settings.SaveDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            InitialDirectory = initialDirectory,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            BitmapUtilities.SavePng(_surface.RenderSelection(), dialog.FileName);
            settings.SaveDirectory = Path.GetDirectoryName(dialog.FileName) ?? initialDirectory;
            _settingsStore.Update(latest => latest.SaveDirectory = settings.SaveDirectory);
            Close();
        }
        catch (Exception ex)
        {
            SimpleLogger.LogError("Failed to save screenshot", ex);
            MessageBox.Show(this, $"保存截图失败：{ex.Message}", "Flick", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool _finishing;

    private void Finish(object sender, RoutedEventArgs e)
    {
        if (_finishing || !_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        try
        {
            BitmapUtilities.CopyToClipboard(_surface.RenderSelection());
        }
        catch (Exception ex)
        {
            SimpleLogger.LogError("Failed to copy screenshot to clipboard", ex);
            MessageBox.Show(this, ex.Message, "Flick", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _finishing = true;
        _root.IsHitTestVisible = false;
        var region = _surface.Selection;
        var scaleX = _surface.ActualWidth / _frame.Width;
        var scaleY = _surface.ActualHeight / _frame.Height;
        var flash = new Rectangle
        {
            Width = region.Width * scaleX,
            Height = region.Height * scaleY,
            Fill = Brushes.White,
            Opacity = 0,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(flash, region.X * scaleX);
        Canvas.SetTop(flash, region.Y * scaleY);
        Panel.SetZIndex(flash, 19);
        _root.Children.Add(flash);
        var exposure = new DoubleAnimationUsingKeyFrames();
        exposure.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, TimeSpan.FromMilliseconds(85)));
        exposure.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(280)));
        exposure.Completed += (_, _) =>
        {
            if (_captureClosed)
            {
                return;
            }
            Close();
            HintToastWindow.ShowCopied(new NativeMethods.NativePoint(
                _frame.ScreenBounds.X + region.X + region.Width / 2,
                _frame.ScreenBounds.Y + region.Y + region.Height / 2));
        };
        flash.BeginAnimation(OpacityProperty, exposure);
    }

    private void StartLongCapture(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        var selection = _surface.Selection;
        var region = new SelectionRegion(
            new PixelRect(
                _frame.ScreenBounds.X + selection.X,
                _frame.ScreenBounds.Y + selection.Y,
                selection.Width,
                selection.Height),
            _surface.SelectedWindow);
        CancelTextOperations();
        Hide();
        // Hold the reference: the overlay window disposes it on close so an
        // abnormal exit path cannot leave the hook + helper windows behind.
        _scrollCapture = new ScrollCaptureController(
            _captureService,
            region,
            _settingsStore);
        _scrollCapture.Completed += (_, _) => Close();
        // Cancelling the long capture exits capture mode entirely instead of
        // dropping back into the (already dimmed-away) annotation overlay.
        _scrollCapture.Cancelled += (_, _) => Close();
        _ = _scrollCapture.StartAsync();
    }

    private void BeginRecordingFromPanel()
    {
        if (!_recordingMode || !_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        var selection = _surface.Selection;
        var region = new PixelRect(
            _frame.ScreenBounds.X + selection.X,
            _frame.ScreenBounds.Y + selection.Y,
            selection.Width,
            selection.Height);
        RecordingRegionConfirmed?.Invoke(this, new RecordingSelection(
            region,
            _showCursorChoice,
            _showClickEffectsChoice,
            _recordingFormat == RecordingExportFormat.Mp4 && _recordSystemAudioChoice,
            _recordingFormat == RecordingExportFormat.Mp4 && _recordMicrophoneChoice,
            _recordingFormat));
    }

    private void StartRegionRecording(object sender, RoutedEventArgs e)
    {
        if (_recordingMode || !_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        CancelTextOperations();
        _translationPopup.IsOpen = false;
        _surface.ClearAnnotationSelection();
        _surface.Tool = CaptureTool.Select;
        _surface.AllowAnnotationEditing = false;
        _recordingMode = true;
        _entryMode = CaptureEntryMode.Recording;
        _recordingPanel = BuildRecordingPanel();
        _root.Children.Add(_recordingPanel);
        RecordingRegionConfirmed += StartPreparedRegionRecording;
        PositionFloatingPanels();
    }

    private async void StartPreparedRegionRecording(object? sender, RecordingSelection selection)
    {
        RecordingRegionConfirmed -= StartPreparedRegionRecording;
        var host = ((App)Application.Current).Host;
        if (host is null)
        {
            Close();
            return;
        }
        // Persistent border first, then close — the outline must never blink.
        var border = new RecordingBorderWindow(selection.Region);
        border.Show();
        Close();
        await host.StartRecordingWithRegionAsync(selection, border);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_finishing)
        {
            e.Handled = true;
            return;
        }
        if (_recordingMode)
        {
            if (e.Key is Key.Tab or Key.Space
                || (_recordingPanel?.IsKeyboardFocusWithin == true && e.Key is Key.Left or Key.Right))
                return;
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.Enter && _surface.IsSelectionCommitted && _surface.Selection.IsEmpty == false)
            {
                BeginRecordingFromPanel();
            }
            e.Handled = true;
            return;
        }

        if (CaptureKeyboardRouting.PreserveTextInput(
                e.Key,
                Keyboard.FocusedElement as DependencyObject ?? e.OriginalSource as DependencyObject))
        {
            return;
        }

        // Swallow a lone Alt press: letting it reach the default window
        // procedure puts the window into the system menu-modal loop, which
        // captures the mouse and stops hover tracking (magnifier/picker).
        // Alt combinations such as Alt+F4 still reach WPF normally.
        if (e.Key == Key.LeftAlt
            || e.Key == Key.RightAlt
            || (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt)))
        {
            e.Handled = true;
            return;
        }

        if (!_surface.IsSelectionCommitted)
        {
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                CopyCurrentColor();
                e.Handled = true;
                return;
            }
            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && !e.IsRepeat)
            {
                _surface.ColorRgbMode = !_surface.ColorRgbMode;
                _surface.InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if ((e.Key is Key.Delete or Key.Back) && Keyboard.Modifiers == ModifierKeys.None && _surface.DeleteSelectedAnnotation())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Enter || (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            if (_surface.IsSelectionCommitted)
            {
                Finish(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _surface.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _surface.Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Save(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
