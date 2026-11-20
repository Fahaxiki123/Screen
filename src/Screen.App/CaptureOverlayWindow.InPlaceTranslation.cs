using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private readonly GlassPanel _inPlaceStatusBar = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _inPlaceStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private Button _originalToggle = null!;
    private Button _copyTranslation = null!;
    private PixelBuffer? _inPlaceResult;
    private AnnotationItem[] _inPlaceMosaics = [];
    private PixelRect _textSelection;
    private string _inPlaceText = string.Empty;
    private bool _inPlaceMode;
    private bool _translationBusy;
    private bool _captureClosed;

    private void InitializeInPlaceTranslation()
    {
        var row = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _originalToggle = CreateInlineButton("查看原文", (_, _) =>
        {
            var showingTranslation = _surface.IsShowingTranslation;
            _surface.SetTranslatedSelection(showingTranslation ? null : _inPlaceResult);
            _originalToggle.Content = showingTranslation ? "查看译文" : "查看原文";
        });
        _copyTranslation = CreateInlineButton("复制译文", (_, _) =>
        {
            try
            {
                BitmapUtilities.SetClipboardTextWithRetry(_inPlaceText);
                HintToastWindow.Success("已拷贝", SelectionCenter());
            }
            catch (Exception exception) { SetInPlaceStatus($"复制失败：{exception.Message}"); }
        });
        actions.Children.Add(_originalToggle);
        actions.Children.Add(_copyTranslation);
        actions.Children.Add(CreateInlineButton("关闭", (_, _) =>
        {
            CancelTranslation();
            ClearInPlaceTranslation();
            PositionFloatingPanels();
        }));
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        _inPlaceStatus.Foreground = FindResourceBrush("Brush.OverlayText");
        _inPlaceStatus.FontSize = 12;
        _inPlaceStatus.Margin = new Thickness(6, 0, 12, 0);
        row.Children.Add(_inPlaceStatus);
        _inPlaceStatusBar.Content = row;
        _inPlaceStatusBar.SetResourceReference(GlassPanel.CornerRadiusProperty, "Radius.Bar");
        _inPlaceStatusBar.Padding = new Thickness(6, 4, 6, 4);
        _root.Children.Add(_inPlaceStatusBar);
        Panel.SetZIndex(_inPlaceStatusBar, 22);
        ClearInPlaceTranslation();
    }

    private static Button CreateInlineButton(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), FontSize = 12, MinHeight = 28 };
        button.SetResourceReference(StyleProperty, "TextCardActionStyle");
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
        _surface.ShowOcrLines(null);
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
        if (_inPlaceStatusBar.Visibility == Visibility.Visible)
            _surface.ShowOcrLines(null);
        _inPlaceStatusBar.Visibility = Visibility.Collapsed;
        if (_originalToggle is not null)
        {
            _originalToggle.Visibility = _copyTranslation.Visibility = Visibility.Collapsed;
            _originalToggle.Content = "查看原文";
        }
    }

    private void SetInPlaceStatus(string status)
    {
        _inPlaceStatus.Text = status;
        _inPlaceStatus.ToolTip = status;
        if (_inPlaceStatusBar.Visibility != Visibility.Visible)
        {
            _inPlaceStatusBar.Visibility = Visibility.Visible;
            Motion.PopIn(_inPlaceStatusBar);
        }
        PositionFloatingPanels();
    }

    private void PositionInPlaceTranslation(Rect viewport, Rect selection)
    {
        if (_inPlaceStatusBar.Visibility != Visibility.Visible)
            return;
        _inPlaceStatusBar.Width = Math.Min(Math.Max(400, selection.Width), Math.Max(1, viewport.Width - 16));
        _inPlaceStatusBar.Measure(new Size(_inPlaceStatusBar.Width, double.PositiveInfinity));
        var height = _inPlaceStatusBar.DesiredSize.Height;
        var toolbar = ToolbarBounds();
        var left = FitX(viewport, selection.X, _inPlaceStatusBar.Width);
        var top = selection.Y - height - 10;
        if (top < viewport.Top + 8 || new Rect(left, top, _inPlaceStatusBar.Width, height).IntersectsWith(toolbar))
        {
            top = toolbar.Bottom + 6;
            if (top + height > viewport.Bottom - 8)
                top = toolbar.Top - height - 6;
        }
        Canvas.SetLeft(_inPlaceStatusBar, left);
        Canvas.SetTop(_inPlaceStatusBar, FitY(viewport, top, height));
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
        _surface.ShowOcrLines(document);
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
        _surface.ShowOcrLines(null);
        _surface.SetTranslatedSelection(_inPlaceResult);
        _inPlaceText = string.Join(Environment.NewLine + Environment.NewLine, translated.Select(region => region.Text));
        _originalToggle.Visibility = _copyTranslation.Visibility = Visibility.Visible;
        SetInPlaceStatus(usedFallback ? "原位翻译完成 · 已使用备用服务" : "原位翻译完成");
    }
}
