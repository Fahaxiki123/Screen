using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private readonly SelectionTextCache _textCache = new();
    private readonly SquircleGlassBorder _inPlaceStatusBar = new()
    {
        CornerRadiusRadius = 10.5,
        Padding = new Thickness(4),
        Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _inPlaceStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _inPlaceCopyFeedback = new() { Interval = TimeSpan.FromSeconds(1) };
    private Button _originalToggle = null!;
    private Button _cardSwitch = null!;
    private Button _copyTranslation = null!;
    private PixelBuffer? _inPlaceResult;
    private CachedInPlaceTranslation? _activeInPlaceSnapshot;
    private AnnotationItem[] _inPlaceMosaics = [];
    private PixelRect _textSelection;
    private string _inPlaceText = string.Empty;
    private string? _inPlaceCompletionDetail;
    private bool _inPlaceMode;
    private bool _translationBusy;
    private bool _captureClosed;

    private void InitializeInPlaceTranslation()
    {
        var row = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _originalToggle = CreateInlineButton("查看原文", (_, _) =>
        {
            bool showOriginal = _surface.IsShowingTranslation;
            _surface.SetTranslatedSelection(showOriginal ? null : _inPlaceResult, animateComparison: true);
            _originalToggle.Content = showOriginal ? "查看译文" : "查看原文";
            SetInPlaceStatus(showOriginal ? "正在对照原文" : "原位覆盖完成", _inPlaceCompletionDetail);
        });
        _cardSwitch = CreateInlineButton("卡片", (_, _) => SwitchInPlaceToCard());
        _cardSwitch.ToolTip = "在浮动卡片中查看当前译文（不重新请求）";
        _copyTranslation = CreateInlineButton("复制译文", (_, _) =>
        {
            if (_activeInPlaceSnapshot is not { } snapshot
                || snapshot.Revision != _textCache.Revision
                || string.IsNullOrWhiteSpace(_inPlaceText))
            {
                return;
            }
            try
            {
                BitmapUtilities.SetClipboardTextWithRetry(_inPlaceText);
                _copyTranslation.Content = "✓ 已复制";
                PositionFloatingPanels();
                _inPlaceCopyFeedback.Stop();
                _inPlaceCopyFeedback.Start();
            }
            catch (Exception exception) { SetInPlaceStatus($"复制失败：{exception.Message}"); }
        });
        _copyTranslation.SetResourceReference(StyleProperty, "CardPrimaryButtonStyle");
        _inPlaceCopyFeedback.Tick += (_, _) =>
        {
            _inPlaceCopyFeedback.Stop();
            _copyTranslation.Content = "复制译文";
            PositionFloatingPanels();
        };
        Closed += (_, _) => _inPlaceCopyFeedback.Stop();
        actions.Children.Add(_originalToggle);
        actions.Children.Add(_cardSwitch);
        actions.Children.Add(_copyTranslation);
        var close = CreateInlineButton("关闭", (_, _) =>
        {
            CancelTranslation();
            ClearInPlaceTranslation();
            PositionFloatingPanels();
        });
        close.Content = AppleVectorIcons.Create("close", 14);
        close.ToolTip = "关闭原位翻译";
        AutomationProperties.SetName(close, "关闭原位翻译");
        close.Padding = new Thickness(0);
        close.Width = 28;
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(9, 0, 2, 0) };
        dot.SetResourceReference(Shape.FillProperty, "Brush.Success");
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        _inPlaceStatus.Foreground = FindResourceBrush("Brush.OverlayText");
        _inPlaceStatus.FontSize = 12;
        _inPlaceStatus.Margin = new Thickness(6, 0, 12, 0);
        row.Children.Add(_inPlaceStatus);
        _inPlaceStatusBar.Child = row;
        _root.Children.Add(_inPlaceStatusBar);
        Panel.SetZIndex(_inPlaceStatusBar, 22);
        ClearInPlaceTranslation();
    }

    private void SwitchInPlaceToCard()
    {
        if (_activeInPlaceSnapshot is not { } snapshot || snapshot.Revision != _textCache.Revision)
        {
            return;
        }
        _inPlaceMode = false;
        ClearInPlaceTranslation();
        _translationCard.ShowCachedTranslation(
            snapshot.TargetLanguage,
            snapshot.SourceLanguage,
            snapshot.SourceText,
            snapshot.CombinedText,
            snapshot.ProviderName,
            snapshot.CompletionDetail,
            snapshot.Revision);
        PositionFloatingPanels();
    }

    private void OnOcrCardRecognitionCompleted(OcrCompletedEventArgs args)
    {
        var requireCloud = SelectionTextCache.RequiresCloudTranslationOcr(_settingsStore.Load(), _credentialService);
        var cloudSatisfied = !requireCloud || args.Document.UsedOnlineService;
        _textCache.TryStoreCardSource(
            args.Revision,
            args.FormattedText,
            args.Document.LanguageTag,
            args.Document.ProviderName,
            cloudSatisfied,
            isEdited: false,
            isDirectText: args.IsDirectText);
        if (!args.IsDirectText)
        {
            _textCache.TryStoreImageOcr(args.Revision, args.Document, cloudSatisfied);
        }
    }

    private static Button CreateInlineButton(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), FontSize = 12, MinHeight = 28 };
        button.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        button.Click += action;
        return button;
    }

    private void OnCaptureSelectionChanged()
    {
        if (_textSelection != _surface.Selection)
        {
            _textCache.Invalidate();
            CancelTextOperations();
            _textSelection = _surface.Selection;
        }
        PositionFloatingPanels();
    }

    private void CancelTranslation()
    {
        _translationOperation?.Cancel();
        _translationBusy = false;
        UpdateProcessingIndicator();
    }

    private void CancelTextOperations(bool keepInPlace = false)
    {
        CancelTranslation();
        _ocrCard.CancelRecognition();
        _ocrCard.Visibility = Visibility.Collapsed;
        _translationCard.Visibility = Visibility.Collapsed;
        if (!keepInPlace)
        {
            ClearInPlaceTranslation();
        }
        if (!_captureClosed)
        {
            PositionFloatingPanels();
        }
    }

    private void OnCaptureAnnotationsChanged()
    {
        // 任何标注增改、画笔遮挡、打码、撤销或重做都会改变选区像素，立即递增版本号并作废旧文本缓存。
        _textCache.Invalidate();
        var keepInPlace = _inPlaceResult is not null && _surface.GetMosaicAnnotations().SequenceEqual(_inPlaceMosaics);
        if (keepInPlace)
        {
            // 仅保留非马赛克标注下的视觉底图供导出；旧快照原文/译文已过期，必须立刻失效并隐藏切卡/复制出口。
            _activeInPlaceSnapshot = null;
            _inPlaceText = string.Empty;
            if (_cardSwitch is not null && _copyTranslation is not null)
            {
                _cardSwitch.Visibility = _copyTranslation.Visibility = Visibility.Collapsed;
            }
        }
        CancelTextOperations(keepInPlace: keepInPlace);
    }

    private void UpdateProcessingIndicator() =>
        _surface.SetProcessing(!_captureClosed && (_translationBusy || _ocrCard.IsRecognizing));

    private void ClearInPlaceTranslation()
    {
        _surface.SetTranslatedSelection(null);
        _inPlaceResult = null;
        _activeInPlaceSnapshot = null;
        _inPlaceMosaics = [];
        _inPlaceText = string.Empty;
        _inPlaceCompletionDetail = null;
        _inPlaceCopyFeedback.Stop();
        _inPlaceStatusBar.Visibility = Visibility.Collapsed;
        if (_originalToggle is not null)
        {
            _originalToggle.Visibility = _cardSwitch.Visibility = _copyTranslation.Visibility = Visibility.Collapsed;
            _originalToggle.Content = "查看原文";
            _copyTranslation.Content = "复制译文";
        }
    }

    private void SetInPlaceStatus(string status, string? detail = null)
    {
        _inPlaceStatus.Text = status;
        _inPlaceStatus.ToolTip = detail ?? status;
        bool entering = _inPlaceStatusBar.Visibility != Visibility.Visible;
        _inPlaceStatusBar.Visibility = Visibility.Visible;
        PositionFloatingPanels();
        if (entering)
        {
            GlassMotion.Enter(_inPlaceStatusBar, -10, 0.96, 280);
        }
    }

    private void ApplyInPlaceSnapshot(CachedInPlaceTranslation snapshot)
    {
        _inPlaceResult = snapshot.RenderedImage;
        _activeInPlaceSnapshot = snapshot;
        _inPlaceMosaics = _surface.GetMosaicAnnotations();
        _surface.SetTranslatedSelection(_inPlaceResult);
        _inPlaceText = snapshot.CombinedText;
        _originalToggle.Visibility = _cardSwitch.Visibility = _copyTranslation.Visibility = Visibility.Visible;
        _inPlaceCompletionDetail = snapshot.CompletionDetail;
        SetInPlaceStatus("原位覆盖完成", _inPlaceCompletionDetail);
    }

    private async Task TranslateInPlaceAsync(
        AppSettings settings,
        string targetLanguage,
        CancellationTokenSource operation,
        int capturedRevision)
    {
        var token = operation.Token;
        if (_textCache.TryGetInPlaceTranslation(targetLanguage, out var cachedSnapshot))
        {
            ApplyInPlaceSnapshot(cachedSnapshot);
            return;
        }

        var source = _surface.RenderSelection();
        var requireCloud = SelectionTextCache.RequiresCloudTranslationOcr(settings, _credentialService);
        OcrDocument document;
        if (_textCache.TryGetImageOcr(requireCloud, out var cachedOcr))
        {
            document = cachedOcr.Document;
        }
        else
        {
            // 原位覆盖必须锚定可靠像素坐标，始终走图像 OCR，不复用 UIA 文本。
            document = await OcrCard.RecognizeAutomaticallyAsync(
                _ocrEngine, source, purpose: OcrRecognitionPurpose.Translation, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (capturedRevision != _textCache.Revision)
            {
                return;
            }
            _textCache.TryStoreImageOcr(capturedRevision, document, cloudSatisfied: true);
            var formatted = OcrTextFormatter.Format(document);
            _textCache.TryStoreCardSource(capturedRevision, formatted, document.LanguageTag, document.ProviderName, cloudSatisfied: true);
        }

        var regions = InPlaceTranslationLayout.CreateRegions(document, source.Width, source.Height);
        if (regions.Count == 0)
        {
            SetInPlaceStatus("未识别到可翻译文字 · 可调整选区后重试");
            return;
        }
        SetInPlaceStatus($"正在翻译 · 0/{regions.Count}");
        var credentials = TranslationCredentialStore.ReadSaved(settings.Translation, _credentialService);
        var profile = settings.Translation with { TargetLanguage = targetLanguage };

        TranslatedRegion[] translated;
        bool usedFallback;
        string providerName;

        if (_translationProvider is IBatchTranslationProvider batchProvider)
        {
            var progress = new Progress<int>(done =>
            {
                if (!operation.IsCancellationRequested && ReferenceEquals(_translationOperation, operation))
                {
                    SetInPlaceStatus($"正在翻译 · {Math.Min(done, regions.Count)}/{regions.Count}");
                }
            });
            var batchResult = await batchProvider.TranslateBatchAsync(
                regions.Select(region => region.Text).ToArray(),
                document.LanguageTag,
                profile,
                credentials,
                progress,
                token);
            token.ThrowIfCancellationRequested();
            if (batchResult.Texts.Count != regions.Count || batchResult.Texts.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("翻译服务返回了不完整内容，请重试。");
            }
            translated = regions.Select((region, index) => new TranslatedRegion(region, batchResult.Texts[index])).ToArray();
            usedFallback = batchResult.UsedFallback;
            providerName = string.IsNullOrWhiteSpace(batchResult.ProviderName)
                ? FormatProviderDisplayName(profile.Provider)
                : batchResult.ProviderName;
            if (usedFallback)
            {
                SimpleLogger.LogWarning(FormatSafeFallbackLog(profile.Provider, batchResult.FallbackProvider, batchResult.Failures));
            }
        }
        else
        {
            using var batch = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var slots = new SemaphoreSlim(3);
            var completed = 0;
            usedFallback = false;
            providerName = FormatProviderDisplayName(profile.Provider);
            translated = await Task.WhenAll(regions.Select(async region =>
            {
                await slots.WaitAsync(batch.Token);
                try
                {
                    var result = await _translationProvider.TranslateAsync(
                        region.Text, document.LanguageTag, profile, credentials, cancellationToken: batch.Token);
                    batch.Token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        throw new InvalidOperationException("翻译服务返回了空内容，请重试。");
                    }
                    usedFallback |= result.UsedFallback;
                    if (!string.IsNullOrWhiteSpace(result.ProviderName))
                    {
                        providerName = result.ProviderName;
                    }
                    SetInPlaceStatus($"正在翻译 · {++completed}/{regions.Count}");
                    return new TranslatedRegion(region, result.Text);
                }
                catch
                {
                    batch.Cancel();
                    throw;
                }
                finally
                {
                    slots.Release();
                }
            }));
        }

        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_translationOperation, operation) || capturedRevision != _textCache.Revision)
        {
            return;
        }

        var rendered = InPlaceTranslationRenderer.Render(_surface.RenderOriginalSelection(), translated);
        var combinedText = string.Join(Environment.NewLine + Environment.NewLine, translated.Select(region => region.Text));
        var combinedSource = string.Join(Environment.NewLine + Environment.NewLine, regions.Select(region => region.Text));
        var detail = usedFallback ? $"原位覆盖完成 · 已使用备用服务（{providerName}）" : $"原位覆盖完成 · {providerName}";
        var snapshot = new CachedInPlaceTranslation(
            capturedRevision,
            targetLanguage,
            document.LanguageTag,
            combinedSource,
            combinedText,
            providerName,
            rendered,
            usedFallback,
            detail);
        _textCache.TryStoreInPlaceTranslation(capturedRevision, snapshot);
        ApplyInPlaceSnapshot(snapshot);
    }
}
