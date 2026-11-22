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
    private readonly SquircleGlassBorder _inPlaceStatusBar = new()
    {
        CornerRadiusRadius = 10.5,
        Padding = new Thickness(4),
        Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _inPlaceStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _inPlaceCopyFeedback = new() { Interval = TimeSpan.FromSeconds(1) };
    private Button _originalToggle = null!;
    private Button _copyTranslation = null!;
    private PixelBuffer? _inPlaceResult;
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
        _copyTranslation = CreateInlineButton("复制译文", (_, _) =>
        {
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
            PositionFloatingPanels();
    }

    private void OnCaptureAnnotationsChanged() => CancelTextOperations(keepInPlace:
        _inPlaceResult is not null && _surface.GetMosaicAnnotations().SequenceEqual(_inPlaceMosaics));

    private void UpdateProcessingIndicator() =>
        _surface.SetProcessing(!_captureClosed && (_translationBusy || _ocrCard.IsRecognizing));

    private void ClearInPlaceTranslation()
    {
        _surface.SetTranslatedSelection(null);
        _inPlaceResult = null;
        _inPlaceMosaics = [];
        _inPlaceText = string.Empty;
        _inPlaceCompletionDetail = null;
        _inPlaceCopyFeedback.Stop();
        _inPlaceStatusBar.Visibility = Visibility.Collapsed;
        if (_originalToggle is not null)
        {
            _originalToggle.Visibility = _copyTranslation.Visibility = Visibility.Collapsed;
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
            GlassMotion.Enter(_inPlaceStatusBar, -10, 0.96, 280);
    }

    private async Task TranslateInPlaceAsync(AppSettings settings, string targetLanguage, CancellationTokenSource operation)
    {
        var token = operation.Token;
        var source = _surface.RenderSelection();
        // UI Automation document ranges can have synthesized line rectangles.
        // Use image OCR here so every replacement is anchored to visible pixels.
        var document = await OcrCard.RecognizeAutomaticallyAsync(
            _ocrEngine, source, purpose: OcrRecognitionPurpose.Translation, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        var regions = InPlaceTranslationLayout.CreateRegions(document, source.Width, source.Height);
        if (regions.Count == 0)
        {
            SetInPlaceStatus("未识别到可翻译文字 · 可调整选区后重试");
            return;
        }
        SetInPlaceStatus($"正在翻译 · 0/{regions.Count}");
        var credentials = TranslationCredentialStore.ReadSaved(settings.Translation, _credentialService);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var slots = new SemaphoreSlim(3);
        var completed = 0;
        var usedFallback = false;
        var translated = await Task.WhenAll(regions.Select(async region =>
        {
            await slots.WaitAsync(batch.Token);
            try
            {
                var result = await _translationProvider.TranslateAsync(
                    region.Text, document.LanguageTag,
                    settings.Translation with { TargetLanguage = targetLanguage }, credentials,
                    cancellationToken: batch.Token);
                batch.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(result.Text))
                    throw new InvalidOperationException("翻译服务返回了空内容，请重试。");
                usedFallback |= result.UsedFallback;
                SetInPlaceStatus($"正在翻译 · {++completed}/{regions.Count}");
                return new TranslatedRegion(region, result.Text);
            }
            catch
            {
                batch.Cancel();
                throw;
            }
            finally { slots.Release(); }
        }));
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_translationOperation, operation))
            return;
        // Replace the selection only once the complete batch succeeds. Failure
        // leaves the original readable and never shows an incomplete translation.
        // Keep editable annotations out of the translated base. They are drawn
        // over both the original and translated versions by the capture surface.
        _inPlaceResult = InPlaceTranslationRenderer.Render(_surface.RenderOriginalSelection(), translated);
        _inPlaceMosaics = _surface.GetMosaicAnnotations();
        _surface.SetTranslatedSelection(_inPlaceResult);
        _inPlaceText = string.Join(Environment.NewLine + Environment.NewLine, translated.Select(region => region.Text));
        _originalToggle.Visibility = _copyTranslation.Visibility = Visibility.Visible;
        _inPlaceCompletionDetail = usedFallback ? "原位覆盖完成 · 已使用备用服务" : null;
        SetInPlaceStatus("原位覆盖完成", _inPlaceCompletionDetail);
    }
}
