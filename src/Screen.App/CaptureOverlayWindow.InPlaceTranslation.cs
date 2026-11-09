using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private readonly Image _inPlaceImage = new() { IsHitTestVisible = false, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly Border _inPlaceStatusBar = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _inPlaceStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private Button _originalToggle = null!;
    private Button _copyTranslation = null!;
    private PixelBuffer? _inPlaceResult;
    private PixelRect _textSelection;
    private string _inPlaceText = string.Empty;
    private bool _inPlaceMode;
    private bool _translationBusy;
    private bool _captureClosed;

    private void InitializeInPlaceTranslation()
    {
        _root.Children.Add(_inPlaceImage);
        Panel.SetZIndex(_inPlaceImage, 1);
        var row = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _originalToggle = CreateInlineButton("查看原文", (_, _) =>
        {
            var showingTranslation = _inPlaceImage.Visibility == Visibility.Visible;
            _inPlaceImage.Visibility = showingTranslation ? Visibility.Collapsed : Visibility.Visible;
            _originalToggle.Content = showingTranslation ? "查看译文" : "查看原文";
        });
        _copyTranslation = CreateInlineButton("复制译文", (_, _) =>
        {
            try
            { BitmapUtilities.SetClipboardTextWithRetry(_inPlaceText); }
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
        _inPlaceStatusBar.Child = row;
        _inPlaceStatusBar.Background = FindResourceBrush("Brush.ChromeGradient");
        _inPlaceStatusBar.BorderBrush = FindResourceBrush("Brush.OverlayBorder");
        _inPlaceStatusBar.BorderThickness = new Thickness(1);
        _inPlaceStatusBar.CornerRadius = new CornerRadius(9);
        _inPlaceStatusBar.Padding = new Thickness(5);
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

    private void CancelTextOperations()
    {
        CancelTranslation();
        _ocrCard.CancelRecognition();
        _ocrCard.Visibility = Visibility.Collapsed;
        _translationCard.Visibility = Visibility.Collapsed;
        ClearInPlaceTranslation();
        if (!_captureClosed)
            PositionFloatingPanels();
    }

    private void UpdateProcessingIndicator() =>
        _surface.SetProcessing(!_captureClosed && (_translationBusy || _ocrCard.IsRecognizing));

    private void ClearInPlaceTranslation()
    {
        _inPlaceImage.Source = null;
        _inPlaceImage.Visibility = Visibility.Collapsed;
        _inPlaceResult = null;
        _inPlaceText = string.Empty;
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
        _inPlaceStatusBar.Visibility = Visibility.Visible;
        PositionFloatingPanels();
    }

    private void PositionInPlaceTranslation()
    {
        var selection = _surface.Selection;
        if (selection.IsEmpty || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var sx = ActualWidth / _frame.Width;
        var sy = ActualHeight / _frame.Height;
        _inPlaceImage.Width = selection.Width * sx;
        _inPlaceImage.Height = selection.Height * sy;
        Canvas.SetLeft(_inPlaceImage, selection.X * sx);
        Canvas.SetTop(_inPlaceImage, selection.Y * sy);
        if (_inPlaceStatusBar.Visibility != Visibility.Visible)
            return;
        _inPlaceStatusBar.Width = Math.Min(Math.Max(400, selection.Width * sx), Math.Max(1, ActualWidth - 16));
        _inPlaceStatusBar.Measure(new Size(_inPlaceStatusBar.Width, double.PositiveInfinity));
        var height = _inPlaceStatusBar.DesiredSize.Height;
        var toolbar = ToolbarBounds();
        var top = selection.Y * sy - height - 10;
        if (top < 8 || new Rect(selection.X * sx, top, _inPlaceStatusBar.Width, height).IntersectsWith(toolbar))
        {
            top = toolbar.Bottom + 6;
            if (top + height > ActualHeight - 8)
                top = Math.Max(8, toolbar.Top - height - 6);
        }
        Canvas.SetLeft(_inPlaceStatusBar, Math.Clamp(selection.X * sx, 8, Math.Max(8, ActualWidth - _inPlaceStatusBar.Width - 8)));
        Canvas.SetTop(_inPlaceStatusBar, top);
    }

    private PixelBuffer RenderCurrentSelection() =>
        _inPlaceResult is not null && _inPlaceImage.Visibility == Visibility.Visible
            ? _inPlaceResult
            : _surface.RenderSelection();

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
        var credentials = new TranslationCredentials(
            _credentialService.Read(settings.Translation.CredentialName),
            _credentialService.Read(TranslationProfile.TencentSecretIdCredentialName),
            _credentialService.Read(TranslationProfile.TencentSecretKeyCredentialName),
            _credentialService.Read(settings.Translation.RacingCredentialName));
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
        _inPlaceResult = InPlaceTranslationRenderer.Render(source, translated);
        _inPlaceImage.Source = _inPlaceResult.ToBitmapSource();
        _inPlaceImage.Visibility = Visibility.Visible;
        _inPlaceText = string.Join(Environment.NewLine + Environment.NewLine, translated.Select(region => region.Text));
        _originalToggle.Visibility = _copyTranslation.Visibility = Visibility.Visible;
        SetInPlaceStatus(usedFallback ? "原位翻译完成 · 已使用备用服务" : "原位翻译完成");
    }
}
