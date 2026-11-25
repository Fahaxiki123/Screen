using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Screen.Core;

namespace Screen.App;

internal sealed record OcrCompletedEventArgs(
    int Revision,
    OcrDocument Document,
    string FormattedText,
    bool IsDirectText);

internal sealed record OcrTranslateRequest(
    int Revision,
    string Text,
    string LanguageTag,
    string ProviderName,
    bool IsEdited,
    bool IsDirectText,
    OcrDocument? Document);

internal sealed class OcrCard : GlassResultCard, IDisposable
{
    private readonly IOcrEngine _ocr;
    private readonly Func<PixelBuffer> _imageProvider;
    private readonly Func<CancellationToken, Task<OcrDocument?>>? _directTextProvider;
    private readonly Func<int>? _revisionProvider;
    private readonly TextBox _text;
    private readonly Grid _editorChrome;
    private readonly Border _progress;
    private readonly TextBlock _status;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _lineCount;
    private readonly Button _editButton;
    private readonly Button _translateButton;
    private readonly Button _copyButton;
    private CancellationTokenSource? _operation;
    private bool _editing;
    private OcrDocument? _lastDocument;
    private string _originalFormattedText = string.Empty;
    private bool _isEditedSource;
    private bool _lastWasDirectText;
    private int _lastRevision;

    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    public OcrCard(
        IOcrEngine ocr,
        Func<PixelBuffer> imageProvider,
        Func<CancellationToken, Task<OcrDocument?>>? directTextProvider = null,
        Func<int>? revisionProvider = null)
    {
        _ocr = ocr;
        _imageProvider = imageProvider;
        _directTextProvider = directTextProvider;
        _revisionProvider = revisionProvider;
        Width = 440;

        _lineCount = new TextBlock { FontSize = 11, Foreground = Token("Brush.TextSecondary"), Text = "0 行" };
        Typography.SetNumeralAlignment(_lineCount, FontNumeralAlignment.Tabular);
        var countBadge = new SquircleGlassBorder
        {
            CornerRadiusRadius = 5,
            Padding = new Thickness(6, 2, 6, 2),
            ShadowLevel = 0,
            ShowRim = false,
            GlassBackground = Token("Brush.SurfaceSunken"),
            Child = _lineCount,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var dragHeader = new DockPanel { Cursor = Cursors.SizeAll, Background = Brushes.Transparent, LastChildFill = false };
        dragHeader.Children.Add(new TextBlock
        {
            Text = "提取文字",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Token("Brush.TextPrimary")
        });
        dragHeader.Children.Add(countBadge);
        var close = new Button { Content = AppleVectorIcons.Create("close", 16), ToolTip = "关闭", Width = 28, Height = 28 };
        close.SetResourceReference(StyleProperty, "CardIconButtonStyle");
        AutomationProperties.SetName(close, "关闭提取文字");
        close.Click += (_, _) => Visibility = Visibility.Collapsed;
        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(dragHeader);

        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 100,
            MaxHeight = 320,
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = 15,
            Foreground = Token("Brush.TextPrimary"),
            CaretBrush = Token("Brush.Accent"),
            Padding = new Thickness(10, 8, 10, 8),
            SelectionBrush = Token("Brush.Accent"),
            SelectionOpacity = 0.25
        };
        TextBlock.SetLineHeight(_text, 24);
        _text.Resources[typeof(ScrollBar)] = Application.Current.FindResource("DarkScrollBarStyle");
        AutomationProperties.SetName(_text, "识别文字");
        var editorSurface = new SquircleGlassBorder
        {
            CornerRadiusRadius = 8,
            ShowRim = false,
            ShadowLevel = 0,
            GlassBackground = Token("Brush.SurfaceSunken")
        };
        var outline = new System.Windows.Shapes.Path { Stroke = Token("Brush.Accent"), StrokeThickness = 1, IsHitTestVisible = false };
        _editorChrome = new Grid { Opacity = 0, IsHitTestVisible = false, Children = { editorSurface, outline } };
        _editorChrome.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 1 && e.NewSize.Height > 1)
            {
                outline.Data = SquircleGeometry.Create(e.NewSize.Width - 1, e.NewSize.Height - 1, 7.5);
                outline.RenderTransform = new TranslateTransform(0.5, 0.5);
            }
        };
        var body = new Grid { Margin = new Thickness(0, 8, 0, 8), Children = { _editorChrome, _text } };
        _progress = new Border
        {
            Height = 2,
            Background = Token("Brush.Accent"),
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        _statusDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Token("Brush.Success"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        _status = new TextBlock
        {
            FontSize = 11,
            Foreground = Token("Brush.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var status = new DockPanel();
        status.Children.Add(_statusDot);
        status.Children.Add(_status);
        _editButton = CreateCardButton("编辑", false);
        _editButton.Click += (_, _) =>
        {
            if (_editing)
                FinishEditing();
            else
                SetEditing(true);
        };
        _translateButton = CreateCardButton("翻译", false);
        _translateButton.ToolTip = "直接翻译当前文字（保留已编辑内容）";
        _translateButton.Click += (_, _) => RequestTranslation();
        _copyButton = CreateCardButton("复制", true);
        _copyButton.MinWidth = 74;
        _copyButton.Click += (_, _) => CopyText();
        _text.TextChanged += (_, _) =>
        {
            if (_editing)
            {
                UpdateTextActions();
            }
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { _editButton, _translateButton, _copyButton } };
        var footer = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        footer.Children.Add(status);

        var content = new StackPanel { Children = { header, _progress, body, footer } };
        AttachCard(new SquircleGlassBorder
        {
            CornerRadiusRadius = 13,
            GlassBackground = Token("Brush.AuroraCard"),
            Padding = new Thickness(16),
            Child = content
        }, dragHeader);
    }

    public string CurrentText => _text.Text;
    public bool IsRecognizing { get; private set; }
    public event EventHandler? RecognitionStateChanged;
    public event EventHandler<OcrCompletedEventArgs>? RecognitionCompleted;
    public event EventHandler<OcrTranslateRequest>? TranslateRequested;
    public event Action<int, string, bool>? EditCommitted;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == VisibilityProperty && (Visibility)e.NewValue != Visibility.Visible)
        {
            FinishEditing();
        }
    }

    public void FinishEditing()
    {
        if (!_editing)
        {
            return;
        }
        SetEditing(false);
        var text = _text.Text;
        if (text == _originalFormattedText || (_revisionProvider is not null && _revisionProvider() != _lastRevision))
        {
            return;
        }
        _originalFormattedText = text;
        _isEditedSource = true;
        if (EditCommitted is { } commit)
        {
            commit(_lastRevision, text, _lastWasDirectText);
            var status = string.IsNullOrWhiteSpace(text) ? "原文已清空" : "已保存到当前选区";
            SetStatus(status, "此次编辑仅保留在本次截图的当前选区。", "Brush.Success");
        }
    }

    public void SetAvailableSize(double availableWidth, double availableHeight)
    {
        Width = Math.Min(Math.Clamp(availableWidth * 0.34, 370, 470), Math.Max(1, availableWidth - 16));
        var bodyHeight = Math.Clamp(availableHeight - 128, 48, 340);
        _text.MinHeight = Math.Min(100, bodyHeight);
        _text.MaxHeight = bodyHeight;
    }

    private void SetStatus(string text, string detail, string brush)
    {
        _status.Text = text;
        _status.ToolTip = detail;
        _statusDot.Fill = Token(brush);
    }

    private void UpdateTextActions()
    {
        RestoreCopyLabel();
        var text = _text.Text;
        int count = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
        _lineCount.Text = $"{count} 行";
        bool hasText = !string.IsNullOrWhiteSpace(text);
        _copyButton.IsEnabled = hasText;
        _translateButton.IsEnabled = hasText;
    }

    /// <summary>
    /// 直接复用同一选区版本的已识别结果，避免连续打开 OCR 卡片时重复识别。
    /// </summary>
    public void ShowCachedResult(
        int revision,
        string formattedText,
        string languageTag,
        string providerName,
        OcrDocument? imageDocument,
        bool isDirectText,
        bool isEdited = false)
    {
        _operation?.Cancel();
        ShowProgress(false);
        SetRecognizing(false);
        SetEditing(false);
        RestoreCopyLabel();
        _lastRevision = revision;
        _lastDocument = imageDocument;
        _lastWasDirectText = isDirectText;
        _isEditedSource = isEdited;
        _originalFormattedText = formattedText ?? string.Empty;
        _text.Text = _originalFormattedText;
        UpdateTextActions();
        var displayProvider = string.IsNullOrWhiteSpace(providerName)
            ? $"自动识别{LanguageName(languageTag)}"
            : providerName;
        var status = isEdited && string.IsNullOrWhiteSpace(_text.Text) ? "原文已清空"
            : _text.Text.Length == 0 ? "未识别到文字" : $"{displayProvider} · 已复用";
        SetStatus(status,
            $"{displayProvider} · {languageTag} · 已复用当前选区识别结果", "Brush.Success");
        _editButton.Visibility = _translateButton.Visibility = _copyButton.Visibility = Visibility.Visible;
    }

    public async Task RecognizeAsync()
    {
        _operation?.Cancel();
        var operation = _operation = new CancellationTokenSource();
        var capturedRevision = _revisionProvider?.Invoke() ?? 0;
        SetEditing(false);
        RestoreCopyLabel();
        _lastDocument = null;
        _originalFormattedText = string.Empty;
        _isEditedSource = false;
        _lastWasDirectText = false;
        _lastRevision = capturedRevision;
        _lineCount.Text = "0 行";
        _editButton.Visibility = _translateButton.Visibility = _copyButton.Visibility = Visibility.Collapsed;

        try
        {
            SetRecognizing(true);
            ShowProgress(true);
            SetStatus("正在识别…", "正在提取选区文字", "Brush.Accent");
            _text.Text = "正在提取选区文字…";
            var timer = Stopwatch.StartNew();
            var directDocument = _directTextProvider is null ? null : await _directTextProvider(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var isDirectText = directDocument is not null;
            var document = directDocument ?? await RecognizeAutomaticallyAsync(
                _ocr, _imageProvider(), purpose: OcrRecognitionPurpose.TextExtraction, cancellationToken: operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_revisionProvider is not null && _revisionProvider() != capturedRevision)
            {
                return;
            }
            var formatted = OcrTextFormatter.Format(document);
            _lastDocument = document;
            _lastWasDirectText = isDirectText;
            _originalFormattedText = formatted;
            _text.Text = formatted;
            UpdateTextActions();
            string provider = ProviderName(document);
            string duration = $"{timer.Elapsed.TotalSeconds:0.0} 秒";
            SetStatus(_text.Text.Length == 0 ? "未识别到文字" : $"{provider} · {duration}",
                $"{provider} · {document.LanguageTag} · 原始识别 {document.Lines.Count} 行 · {duration}", "Brush.Success");
            _editButton.Visibility = _translateButton.Visibility = _copyButton.Visibility = Visibility.Visible;
            RecognitionCompleted?.Invoke(this, new OcrCompletedEventArgs(capturedRevision, document, formatted, isDirectText));
            GlassMotion.Enter(_text, 3, 1, 200);
        }
        catch (OperationCanceledException)
        {
            if (!operation.IsCancellationRequested)
            {
                _text.Clear();
                SetStatus("识别超时", "识别超时，请重新提取文字。", "Brush.Warning");
            }
        }
        catch (Exception exception)
        {
            if (!operation.IsCancellationRequested)
            {
                _text.Clear();
                SetStatus("识别失败", exception.Message, "Brush.Danger");
            }
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                ShowProgress(false);
                SetRecognizing(false);
            }
        }
    }

    private void RequestTranslation()
    {
        var text = _text.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        FinishEditing();
        var isEdited = _isEditedSource;
        var languageTag = isEdited || _lastDocument is null
            ? OcrQualityEvaluator.DetectLanguage(text)
            : _lastDocument.LanguageTag;
        var provider = isEdited
            ? "已编辑文本"
            : _lastDocument is null ? "文字识别" : ProviderName(_lastDocument);
        TranslateRequested?.Invoke(this, new OcrTranslateRequest(
            _lastRevision,
            text,
            languageTag,
            provider,
            isEdited,
            _lastWasDirectText,
            isEdited ? null : _lastDocument));
    }

    private void SetRecognizing(bool value)
    {
        if (IsRecognizing == value)
        {
            return;
        }
        IsRecognizing = value;
        RecognitionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CancelRecognition()
    {
        _operation?.Cancel();
        ShowProgress(false);
        SetRecognizing(false);
    }

    private void ShowProgress(bool visible)
    {
        if (visible)
        {
            _progress.Visibility = Visibility.Visible;
            _progress.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(900))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }
        else
        {
            _progress.BeginAnimation(OpacityProperty, null);
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    internal static Task<OcrDocument> RecognizeAutomaticallyAsync(
        IOcrEngine ocr,
        PixelBuffer image,
        IReadOnlyList<string>? languages = null,
        OcrRecognitionPurpose purpose = OcrRecognitionPurpose.TextExtraction,
        CancellationToken cancellationToken = default)
    {
        var language = languages?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                       ?? ocr.AvailableLanguages.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                       ?? "auto";
        return ocr.RecognizeAsync(image, language, purpose, cancellationToken);
    }

    private static string LanguageName(string languageTag) => languageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? "中文"
        : languageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            ? "日文"
            : languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "英文"
                : "文字";

    private static string ProviderName(OcrDocument document) =>
        string.IsNullOrWhiteSpace(document.ProviderName)
            ? $"自动识别{LanguageName(document.LanguageTag)}"
            : document.ProviderName;

    private void SetEditing(bool editing)
    {
        _editing = editing;
        _text.IsReadOnly = !editing;
        _editButton.Content = editing ? "完成编辑" : "编辑";
        _editorChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(editing ? 1 : 0, TimeSpan.FromMilliseconds(180)));
        if (editing)
        {
            _text.Focus();
            _text.CaretIndex = _text.Text.Length;
        }
    }

    private void CopyText()
    {
        if (string.IsNullOrWhiteSpace(_text.Text))
        {
            return;
        }
        try
        {
            BitmapUtilities.SetClipboardTextWithRetry(_text.Text);
            ShowCopied(_copyButton, "复制");
        }
        catch (Exception exception)
        {
            SetStatus("复制失败", exception.Message, "Brush.Danger");
        }
    }

    private static Button CreateCardButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Height = 30,
            MinWidth = 44,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(9, 0, 9, 0)
        };
        if (!primary)
        {
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
        }
        button.SetResourceReference(StyleProperty, primary ? "CardPrimaryButtonStyle" : "CardSecondaryButtonStyle");
        return button;
    }

    public void Dispose() => CancelRecognition();
}
