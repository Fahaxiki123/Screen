using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

internal sealed class OcrCard : Grid, IDisposable
{
    private readonly IOcrEngine _ocr;
    private readonly Func<PixelBuffer> _imageProvider;
    private readonly Func<CancellationToken, Task<OcrDocument?>>? _directTextProvider;
    private readonly TextBox _text;
    private readonly Border _textBorder;
    private readonly Border _progress;
    private readonly TextBlock _status;
    private readonly Button _editButton;
    private readonly Button _copyButton;
    private readonly Button _installButton;
    private readonly Polygon _pointer;
    private readonly GlassPanel _card;
    private CancellationTokenSource? _operation;
    private bool _editing;
    private bool _dragging;
    private Point _dragOrigin;
    private double _dragLeft;
    private double _dragTop;

    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    public OcrCard(
        IOcrEngine ocr,
        Func<PixelBuffer> imageProvider,
        Func<CancellationToken, Task<OcrDocument?>>? directTextProvider = null)
    {
        _ocr = ocr;
        _imageProvider = imageProvider;
        _directTextProvider = directTextProvider;
        Width = 320;

        _text = new TextBox { IsReadOnly = true, MinHeight = 70, MaxHeight = 280 };
        _text.SetResourceReference(StyleProperty, "TextCardBodyStyle");
        _text.Resources[typeof(ScrollBar)] = Application.Current.FindResource("DarkScrollBarStyle");
        _textBorder = new Border { Child = _text, BorderThickness = new Thickness(1), Margin = new Thickness(0, 7, 0, 0) };
        _textBorder.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");

        var close = new Button
        {
            Content = new LucideIconElement { Kind = LucideIconKind.X, Width = 12, Height = 12, StrokeThickness = 2, Stroke = Token("Brush.LabelSecondary") },
            ToolTip = "关闭"
        };
        close.SetResourceReference(StyleProperty, "TextCardCloseStyle");
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭文字卡片");
        close.Click += (_, _) => Visibility = Visibility.Collapsed;

        var title = new TextBlock
        {
            Text = "提取文字",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("Brush.Label"),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeAll,
            Background = Token("Brush.Transparent")
        };
        title.MouseLeftButtonDown += BeginDrag;
        title.MouseMove += Drag;
        title.MouseLeftButtonUp += EndDrag;

        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);
        _progress = new Border
        {
            Height = 2,
            Background = Token("Brush.Accent"),
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };

        _status = new TextBlock();
        _status.SetResourceReference(StyleProperty, "TextCardFootnoteStyle");
        _status.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = _status });
        _installButton = CardAction("安装语言包");
        _installButton.Visibility = Visibility.Collapsed;
        _installButton.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });

        _editButton = CardAction("编辑");
        _editButton.Click += (_, _) => SetEditing(!_editing);
        _copyButton = CardAction("拷贝");
        _copyButton.Click += (_, _) => CopyText();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_installButton);
        actions.Children.Add(_editButton);
        actions.Children.Add(_copyButton);
        var footer = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        footer.Children.Add(_status);
        var footerBorder = new Border { Child = footer };
        footerBorder.SetResourceReference(StyleProperty, "TextCardFooterStyle");

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(_progress);
        content.Children.Add(_textBorder);
        content.Children.Add(footerBorder);
        _card = new GlassPanel { Content = content, Padding = new Thickness(12, 10, 12, 10) };
        _card.SetResourceReference(GlassPanel.CornerRadiusProperty, "Radius.Card");
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Motion.PopIn(this, horizontal: true);
        };
        _pointer = new Polygon
        {
            Points = new PointCollection { new(0, 0), new(18, 0), new(9, 10) },
            Fill = Token("Brush.Surface"),
            Stroke = Token("Brush.Border"),
            StrokeThickness = 1,
            Width = 18,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1)
        };
        Children.Add(_card);
        Children.Add(_pointer);
    }

    public string CurrentText => _text.Text;
    public bool IsRecognizing { get; private set; }
    public event EventHandler? RecognitionStateChanged;
    internal event Action<OcrDocument>? DocumentRecognized;
    public bool IsUserPositioned { get; private set; }

    public void SetAvailableSize(double availableWidth, double availableHeight)
    {
        Width = Math.Max(1, Math.Min(Math.Clamp(availableWidth * 0.28, 300, 370), availableWidth - 16));
        var bodyHeight = Math.Clamp(availableHeight - 115, 40, 280);
        _text.MinHeight = Math.Min(70, bodyHeight);
        _text.MaxHeight = bodyHeight;
    }

    public void ResetPosition() => IsUserPositioned = false;

    public void SetPointer(CalloutPointerSide side, double offset)
    {
        _pointer.Visibility = side == CalloutPointerSide.None ? Visibility.Collapsed : Visibility.Visible;
        _card.Margin = side switch
        {
            CalloutPointerSide.Top => new Thickness(0, 10, 0, 0),
            CalloutPointerSide.Right => new Thickness(0, 0, 10, 0),
            CalloutPointerSide.Bottom => new Thickness(0, 0, 0, 10),
            CalloutPointerSide.Left => new Thickness(10, 0, 0, 0),
            _ => new Thickness(0)
        };
        var horizontal = side is CalloutPointerSide.Top or CalloutPointerSide.Bottom;
        _pointer.Width = horizontal ? 18 : 10;
        _pointer.Height = horizontal ? 10 : 18;
        _pointer.HorizontalAlignment = side switch
        {
            CalloutPointerSide.Left => HorizontalAlignment.Left,
            CalloutPointerSide.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };
        _pointer.VerticalAlignment = side switch
        {
            CalloutPointerSide.Top => VerticalAlignment.Top,
            CalloutPointerSide.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Top
        };
        _pointer.Margin = horizontal
            ? new Thickness(Math.Max(12, offset - 9), side == CalloutPointerSide.Top ? 1 : 0, 0, side == CalloutPointerSide.Bottom ? 1 : 0)
            : new Thickness(side == CalloutPointerSide.Left ? 1 : 0, Math.Max(12, offset - 9), side == CalloutPointerSide.Right ? 1 : 0, 0);
        _pointer.Points = side switch
        {
            CalloutPointerSide.Top => new PointCollection { new(0, 10), new(18, 10), new(9, 0) },
            CalloutPointerSide.Right => new PointCollection { new(0, 0), new(0, 18), new(10, 9) },
            CalloutPointerSide.Bottom => new PointCollection { new(0, 0), new(18, 0), new(9, 10) },
            CalloutPointerSide.Left => new PointCollection { new(10, 0), new(10, 18), new(0, 9) },
            _ => _pointer.Points
        };
    }

    public async Task RecognizeAsync()
    {
        _operation?.Cancel();
        // Do not dispose the previous source here: an in-flight task may still
        // be reading its token, and disposing can surface ObjectDisposedException
        // instead of the expected OperationCanceledException. The GC finalizer
        // releases the source after the task drops its reference.
        var operation = _operation = new CancellationTokenSource();
        SetEditing(false);
        var supportedLanguages = GetAutomaticLanguages(_ocr.AvailableLanguages);
        if (supportedLanguages.Count == 0)
        {
            ShowProgress(false);
            SetRecognizing(false);
            _text.Text = "缺少可用的中、英或日文 OCR 语言包。安装后，再次点按提取文字。";
            _status.Text = "需要语言包";
            _installButton.Visibility = Visibility.Visible;
            _editButton.Visibility = Visibility.Collapsed;
            _copyButton.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            SetRecognizing(true);
            ShowProgress(true);
            _status.Text = "正在识别…";
            _installButton.Visibility = Visibility.Collapsed;
            _editButton.Visibility = Visibility.Collapsed;
            _copyButton.Visibility = Visibility.Collapsed;
            _text.Text = "正在提取选区文字…";
            var document = _directTextProvider is null
                ? null
                : await _directTextProvider(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            document ??= await RecognizeAutomaticallyAsync(
                _ocr,
                _imageProvider(),
                supportedLanguages,
                OcrRecognitionPurpose.TextExtraction,
                operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _text.Text = OcrTextFormatter.Format(document);
            _status.Text = _text.Text.Length == 0
                ? "未识别到文字"
                : $"{LanguageName(document.LanguageTag)} · {ProviderName(document)}";
            _editButton.Visibility = Visibility.Visible;
            _copyButton.Visibility = Visibility.Visible;
            DocumentRecognized?.Invoke(document);
        }
        catch (OperationCanceledException)
        {
            if (!operation.IsCancellationRequested)
                _status.Text = "识别超时，请重试";
        }
        catch (Exception exception)
        {
            if (!operation.IsCancellationRequested)
            {
                _text.Clear();
                _status.Text = $"识别失败：{exception.Message}";
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
                ? "英语"
                : "文字";

    private static string ProviderName(OcrDocument document) =>
        string.IsNullOrWhiteSpace(document.ProviderName)
            ? (document.UsedOnlineService ? "云端识别" : "本机识别")
            : document.ProviderName == "PP-OCRv6 本地" ? "本机识别" : document.ProviderName;

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (Parent is not Canvas canvas)
        {
            return;
        }
        _dragging = true;
        _dragOrigin = e.GetPosition(canvas);
        _dragLeft = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        _dragTop = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        IsUserPositioned = true;
        SetPointer(CalloutPointerSide.None, 0);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Drag(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || Parent is not Canvas canvas)
        {
            return;
        }
        var position = e.GetPosition(canvas);
        var left = Math.Clamp(_dragLeft + position.X - _dragOrigin.X, 8, Math.Max(8, canvas.ActualWidth - ActualWidth - 8));
        var top = Math.Clamp(_dragTop + position.Y - _dragOrigin.Y, 8, Math.Max(8, canvas.ActualHeight - ActualHeight - 8));
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SetEditing(bool editing)
    {
        _editing = editing;
        _text.IsReadOnly = !editing;
        _textBorder.SetResourceReference(Border.BorderBrushProperty, editing ? "Brush.Accent" : "Brush.Transparent");
        _editButton.Content = editing ? "完成编辑" : "编辑";
        _copyButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
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
            HintToastWindow.Success("已拷贝");
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
    }

    private static Button CardAction(string label)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(StyleProperty, "TextCardActionStyle");
        return button;
    }
    public void Dispose()
    {
        // Cancel without disposing: an in-flight recognition may still be
        // linking this token (CreateLinkedTokenSource registers on it), and
        // disposing surfaces ObjectDisposedException instead of cancellation.
        // The GC reclaims the source once the task drops its reference.
        CancelRecognition();
    }
}
