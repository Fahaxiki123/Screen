using System;
using System.Collections.Generic;
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

internal sealed class OcrCard : GlassResultCard, IDisposable
{
    private readonly IOcrEngine _ocr;
    private readonly Func<PixelBuffer> _imageProvider;
    private readonly Func<CancellationToken, Task<OcrDocument?>>? _directTextProvider;
    private readonly TextBox _text;
    private readonly Grid _editorChrome;
    private readonly Border _progress;
    private readonly TextBlock _status;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _lineCount;
    private readonly Button _editButton;
    private readonly Button _copyButton;
    private readonly Button _installButton;
    private CancellationTokenSource? _operation;
    private bool _editing;

    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    public OcrCard(IOcrEngine ocr, Func<PixelBuffer> imageProvider, Func<CancellationToken, Task<OcrDocument?>>? directTextProvider = null)
    {
        _ocr = ocr;
        _imageProvider = imageProvider;
        _directTextProvider = directTextProvider;
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
        // Fade the edit surface under the live text so the caret and IME remain in the same control.
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
        _installButton = CreateCardButton("安装语言包", true);
        _installButton.Visibility = Visibility.Collapsed;
        _installButton.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });
        _editButton = CreateCardButton("编辑", false);
        _editButton.Click += (_, _) => SetEditing(!_editing);
        _copyButton = CreateCardButton("复制", true);
        _copyButton.MinWidth = 74;
        _copyButton.Click += (_, _) => CopyText();
        _text.TextChanged += (_, _) =>
        {
            if (_editing)
                UpdateTextActions();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { _installButton, _editButton, _copyButton } };
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
        _copyButton.IsEnabled = !string.IsNullOrWhiteSpace(text);
    }

    public async Task RecognizeAsync()
    {
        _operation?.Cancel();
        var operation = _operation = new CancellationTokenSource();
        SetEditing(false);
        RestoreCopyLabel();
        _lineCount.Text = "0 行";
        _editButton.Visibility = _copyButton.Visibility = Visibility.Collapsed;
        var supportedLanguages = GetAutomaticLanguages(_ocr.AvailableLanguages);
        if (supportedLanguages.Count == 0)
        {
            ShowProgress(false);
            SetRecognizing(false);
            _text.Clear();
            SetStatus("缺少语言包", "缺少可用的中、英或日文 OCR 语言包", "Brush.Warning");
            _installButton.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            SetRecognizing(true);
            ShowProgress(true);
            SetStatus("正在识别…", "正在提取选区文字", "Brush.Accent");
            _installButton.Visibility = Visibility.Collapsed;
            _text.Text = "正在提取选区文字…";
            var timer = Stopwatch.StartNew();
            var document = _directTextProvider is null ? null : await _directTextProvider(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            document ??= await RecognizeAutomaticallyAsync(
                _ocr, _imageProvider(), supportedLanguages, OcrRecognitionPurpose.TextExtraction, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _text.Text = OcrTextFormatter.Format(document);
            UpdateTextActions();
            string provider = ProviderName(document);
            string duration = $"{timer.Elapsed.TotalSeconds:0.0} 秒";
            SetStatus(_text.Text.Length == 0 ? "未识别到文字" : $"{provider} · {duration}",
                $"{provider} · {document.LanguageTag} · 原始识别 {document.Lines.Count} 行 · {duration}", "Brush.Success");
            _editButton.Visibility = _copyButton.Visibility = Visibility.Visible;
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

    private void SetRecognizing(bool value)
    {
        if (IsRecognizing == value)
            return;
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

    internal static async Task<OcrDocument> RecognizeAutomaticallyAsync(
        IOcrEngine ocr,
        PixelBuffer image,
        IReadOnlyList<string>? languages = null,
        OcrRecognitionPurpose purpose = OcrRecognitionPurpose.TextExtraction,
        CancellationToken cancellationToken = default)
    {
        var candidates = languages ?? GetAutomaticLanguages(ocr.AvailableLanguages);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("Windows 未安装可用的中、英或日文 OCR 语言包。");
        }

        var tasks = candidates.Select(async language =>
        {
            try
            {
                return (Document: (OcrDocument?)await ocr.RecognizeAsync(image, language, purpose, cancellationToken),
                    Error: (Exception?)null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (Document: (OcrDocument?)null, Error: exception);
            }
        });
        var attempts = await Task.WhenAll(tasks);
        var bestDocument = attempts.Select(attempt => attempt.Document)
            .OfType<OcrDocument>()
            .MaxBy(RecognitionScore);
        if (bestDocument is not null)
        {
            return bestDocument;
        }

        var error = attempts.Select(attempt => attempt.Error).FirstOrDefault(error => error is not null);
        throw new InvalidOperationException(error?.Message ?? "无法创建可用的 OCR 引擎。", error);
    }

    private static IReadOnlyList<string> GetAutomaticLanguages(IEnumerable<string> availableLanguages)
    {
        var automatic = availableLanguages.FirstOrDefault(language =>
            language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || language.Equals("multi", StringComparison.OrdinalIgnoreCase));
        if (automatic is not null)
        {
            return [automatic];
        }
        var supported = new[] { "zh", "en", "ja" };
        return supported
            .Select(prefix => availableLanguages.FirstOrDefault(language =>
                language.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || language.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)))
            .Where(language => language is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int RecognitionScore(OcrDocument document)
    {
        var text = document.Text ?? string.Empty;
        var visible = text.Count(character => !char.IsWhiteSpace(character));
        var latin = text.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        var han = text.Count(character => character is >= '\u3400' and <= '\u9FFF');
        var kana = text.Count(character => character is >= '\u3040' and <= '\u30FF');
        var score = visible * 3 + document.Lines.Count * 8 + document.Lines.Sum(line => line.Words.Count) * 2;
        if (document.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            score += han * 3 - kana * 2;
        }
        else if (document.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            score += han + kana * 8;
        }
        else if (document.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            score += latin * 3;
        }
        return score;
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
            return;
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
