using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
        CancelTextOperations();
        _surface.Tool = tool;
        _stylePanel.Visibility = Visibility.Visible;
        ConfigureStylePanel(tool);
        foreach (var pair in _toolButtons)
        {
            var isSelected = pair.Key == tool;
            pair.Value.Background = isSelected
                ? (Brush)Application.Current.FindResource("Brush.AccentSubtleDark")
                : Brushes.Transparent;
            pair.Value.Foreground = isSelected
                ? (Brush)Application.Current.FindResource("Brush.AccentOnDark")
                : (Brush)Application.Current.FindResource("Brush.OverlayIcon");
            pair.Value.BorderBrush = isSelected
                ? (Brush)Application.Current.FindResource("Brush.AccentSubtleDarkBorder")
                : Brushes.Transparent;
            pair.Value.BorderThickness = isSelected
                ? new Thickness(1)
                : new Thickness(0);
        }
        PositionFloatingPanels();
    }

    private void UpdateStyle()
    {
        var pixelScale = Math.Max(1.0, _frame.DpiX / 96.0);
        var thickness = (_surface.Tool == CaptureTool.Mosaic ? _mosaicSize : _thickness) * pixelScale;
        var variant = _surface.Tool switch
        {
            CaptureTool.Mosaic => _mosaicVariant + (_surface.MosaicMode == MosaicDrawMode.Brush ? 100 : 0),
            CaptureTool.Text => _textVariant,
            _ => 0
        };
        _surface.CurrentStyle = new AnnotationStyle(_color, thickness, _fontSize * pixelScale, variant, _mosaicIntensity);
    }

    private void CopyCurrentColor()
    {
        var argb = _surface.HoverColor;
        var text = _surface.ColorRgbMode ? ColorText.ToRgb(argb) : ColorText.ToHex(argb);
        try
        {
            BitmapUtilities.SetClipboardTextWithRetry(text);
            _surface.ColorStatus = "已复制 ✓";
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
            _ocrCard.ResetPosition();
            _ocrCard.Visibility = Visibility.Visible;
            PositionFloatingPanels();
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

    private async Task TranslateSelectionAsync(string targetLanguage)
    {
        if (_captureClosed || !_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
            return;
        _ocrCard.CancelRecognition();
        _ocrCard.Visibility = Visibility.Collapsed;
        _surface.Tool = CaptureTool.Select;
        foreach (var button in _toolButtons.Values)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = FindResourceBrush("Brush.OverlayIcon");
        }
        var settings = _settingsStore.Load();
        _inPlaceMode = settings.TranslationDisplayMode == TranslationDisplayMode.InPlace;
        // Cancel but do NOT dispose the previous source: the in-flight task may
        // still be reading that token, and the providers link it via
        // CreateLinkedTokenSource (which registers on it) — a disposed source
        // turns the expected cancellation into ObjectDisposedException. See
        // OcrCard.RecognizeAsync for the same rationale.
        _translationOperation?.Cancel();
        var operation = _translationOperation = new CancellationTokenSource();
        var cancellationToken = operation.Token;
        ClearInPlaceTranslation();
        if (_inPlaceMode)
        {
            _translationCard.Visibility = Visibility.Collapsed;
            SetInPlaceStatus("正在提取文字…");
        }
        else
        {
            _translationCard.Begin(targetLanguage);
        }
        _translationBusy = true;
        UpdateProcessingIndicator();
        _stylePanel.Visibility = Visibility.Collapsed;
        PositionFloatingPanels();

        _translationCard.SetProvider(settings.Translation.Provider switch
        {
            TranslationProviderKind.Tencent => "腾讯机器翻译",
            TranslationProviderKind.MyMemory => "MyMemory",
            _ => "AI 自然翻译"
        });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_inPlaceMode)
            {
                await TranslateInPlaceAsync(settings, targetLanguage, operation);
                return;
            }
            var document = await TryExtractDirectTextAsync(cancellationToken)
                           ?? await OcrCard.RecognizeAutomaticallyAsync(
                               _ocrEngine,
                               _surface.RenderSelection(),
                               purpose: OcrRecognitionPurpose.Translation,
                               cancellationToken: cancellationToken);
            if (operation.IsCancellationRequested)
            {
                // A newer translation has already taken over the card.
                return;
            }
            var sourceText = OcrTextFormatter.Format(document);
            _translationCard.SetSourceLanguage(document.LanguageTag);
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                _translationCard.SetStatus("未识别到可翻译文字");
                _translationCard.Complete();
                return;
            }
            var extractedBy = string.IsNullOrWhiteSpace(document.ProviderName)
                ? "文字识别"
                : document.ProviderName;
            _translationCard.SetStatus(
                $"文字提取完成（{extractedBy}，用时 {timer.Elapsed.TotalSeconds:F1} 秒）· 正在翻译…");
            var extractionSeconds = timer.Elapsed.TotalSeconds;
            double? firstTokenSeconds = null;
            var progress = new Progress<string>(text =>
            {
                // Progress posts are queued to the dispatcher and can arrive
                // after a newer translation has begun; a stale run must never
                // overwrite the new card content.
                if (operation.IsCancellationRequested || !ReferenceEquals(_translationOperation, operation) || !_translationBusy)
                {
                    return;
                }
                firstTokenSeconds ??= timer.Elapsed.TotalSeconds - extractionSeconds;
                _translationCard.SetText(text);
            });
            var result = await _translationProvider.TranslateAsync(
                sourceText,
                document.LanguageTag,
                settings.Translation with { TargetLanguage = targetLanguage },
                new TranslationCredentials(
                    _credentialService.Read(settings.Translation.CredentialName),
                    _credentialService.Read(TranslationProfile.TencentSecretIdCredentialName),
                    _credentialService.Read(TranslationProfile.TencentSecretKeyCredentialName),
                    _credentialService.Read(settings.Translation.RacingCredentialName)),
                progress,
                cancellationToken);
            if (operation.IsCancellationRequested)
            {
                return;
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
                if (_inPlaceMode)
                    SetInPlaceStatus("翻译超时，请重试");
                else
                    _translationCard.SetError("翻译超时，请重试");
            }
        }
        catch (Exception exception)
        {
            // A stale run faulting after being superseded must not overwrite
            // the newer run's card with an error.
            if (!operation.IsCancellationRequested && ReferenceEquals(_translationOperation, operation))
            {
                if (_inPlaceMode)
                    SetInPlaceStatus($"翻译失败：{exception.Message}");
                else
                    _translationCard.SetError($"翻译请求失败：{exception.Message}");
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
            _surface.SelectedWindow,
            screenSelection,
            cancellationToken);
    }

    private void Pin(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        var image = RenderCurrentSelection();
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
        BitmapUtilities.SavePng(RenderCurrentSelection(), dialog.FileName);
        settings.SaveDirectory = Path.GetDirectoryName(dialog.FileName) ?? initialDirectory;
        _settingsStore.Update(latest => latest.SaveDirectory = settings.SaveDirectory);
        Close();
    }

    private void Finish(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        BitmapUtilities.CopyToClipboard(RenderCurrentSelection());
        Close();
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
            // The panel's 鼠标设置 switch owns both the cursor and the click
            // ripple effect — otherwise the effect stays bound to a hidden
            // settings flag and looks broken.
            _showCursorChoice,
            _recordSystemAudioChoice,
            _recordMicrophoneChoice));
    }

    private async void StartRegionRecording(object sender, RoutedEventArgs e)
    {
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            return;
        }
        var selection = _surface.Selection;
        var region = new PixelRect(
            _frame.ScreenBounds.X + selection.X,
            _frame.ScreenBounds.Y + selection.Y,
            selection.Width,
            selection.Height);
        var host = ((App)Application.Current).Host;
        if (host is null)
        {
            Close();
            return;
        }
        // Persistent border first, then close — the outline must never blink.
        var border = new RecordingBorderWindow(region);
        border.Show();
        Close();
        await host.StartRecordingWithRegionAsync(region, border);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingMode)
        {
            // Recording selection only understands Esc (cancel) and Enter
            // (confirm); everything else — including the copy/finish keys —
            // stays swallowed so it cannot trigger screenshot actions.
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
