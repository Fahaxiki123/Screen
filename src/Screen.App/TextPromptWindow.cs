using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

internal sealed class TextPromptWindow : Window
{
    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    private readonly TextBox _text;
    private readonly PixelPoint _screenPoint;
    private readonly AnnotationStyle _style;
    private readonly double? _maxContentWidth;
    private bool _completing;
    private bool _composing;
    private static readonly Thickness EditorPadding = new(5, 3, 5, 3);

    public TextPromptWindow(
        AnnotationStyle style,
        PixelPoint screenPoint,
        string? initialValue = null,
        double? maxContentWidth = null)
    {
        _style = style;
        _screenPoint = screenPoint;
        _maxContentWidth = maxContentWidth;
        Title = "输入文字";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        var weight = style.Variant switch
        {
            AnnotationVariants.TextMedium => FontWeights.SemiBold,
            AnnotationVariants.TextBold => FontWeights.Bold,
            _ => FontWeights.Normal
        };
        var ink = new SolidColorBrush(Color.FromArgb(
            (byte)(style.Argb >> 24), (byte)(style.Argb >> 16), (byte)(style.Argb >> 8), (byte)style.Argb));
        ink.Freeze();
        _text = new TextBox
        {
            Width = 276 + EditorPadding.Left + EditorPadding.Right,
            MaxHeight = 180,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = style.FontSize,
            FontWeight = weight,
            Foreground = ink,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = EditorPadding,
            FocusVisualStyle = null,
            CaretBrush = ink,
            SelectionBrush = Token("Brush.Accent"),
            SelectionOpacity = 0.28,
            Text = initialValue ?? string.Empty
        };
        _text.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, _) =>
            {
                _composing = true;
                // Let the native IME candidate list receive mouse clicks.
                if (Mouse.Captured == _text)
                    Mouse.Capture(null);
            }), true);
        _text.AddHandler(TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((_, _) =>
            {
                if (_composing)
                {
                    _composing = false;
                    RestoreCaptureAfterInput();
                }
            }), true);
        // TextBox releases capture after selecting text with the mouse.
        _text.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, _) => RestoreCaptureAfterInput()), true);
        _text.ContextMenuClosing += (_, _) => RestoreCaptureAfterInput();
        _text.PreviewKeyDown += OnKeyDown;
        var editor = new Grid();
        editor.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x29, 0, 0, 0)), RadiusX = 6, RadiusY = 6 });
        editor.Children.Add(_text);
        editor.Children.Add(new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xC7, 255, 255, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            RadiusX = 6,
            RadiusY = 6,
            IsHitTestVisible = false
        });
        Content = editor;
        AddHandler(
            Mouse.PreviewMouseDownOutsideCapturedElementEvent,
            new MouseButtonEventHandler(OnMouseDownOutsideCapturedElement),
            true);
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            _text.Focus();
            Keyboard.Focus(_text);
            if (!string.IsNullOrEmpty(initialValue))
            {
                _text.SelectAll();
            }
            Mouse.Capture(_text, CaptureMode.SubTree);
        };
        Closed += (_, _) =>
        {
            if (Mouse.Captured == _text || IsDescendantOfTextBox(Mouse.Captured as DependencyObject))
            {
                Mouse.Capture(null);
            }
        };
    }

    public string Value => _text.Text.Trim();

    private void RestoreCaptureAfterInput()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsActive && !_completing && !_composing && _text.IsKeyboardFocusWithin && Mouse.Captured is null)
                Mouse.Capture(_text, CaptureMode.SubTree);
        }));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            (int)Math.Round(_screenPoint.X),
            (int)Math.Round(_screenPoint.Y)));
        var dpiScale = dpi / 96.0;
        _text.FontSize = _style.FontSize / dpiScale;
        TextBlock.SetLineHeight(_text, _style.FontSize * 1.22 / dpiScale);
        if (_maxContentWidth is double maxContentWidth)
        {
            _text.Width = Math.Max(1, maxContentWidth / dpiScale) + EditorPadding.Left + EditorPadding.Right;
        }
        NativeMethods.SetWindowPos(
            handle,
            new nint(-1),
            (int)Math.Round(_screenPoint.X - EditorPadding.Left * dpiScale),
            (int)Math.Round(_screenPoint.Y - EditorPadding.Top * dpiScale),
            0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_composing || e.Key == Key.ImeProcessed)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            Complete(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Complete(!string.IsNullOrWhiteSpace(_text.Text));
            e.Handled = true;
        }
    }

    private void OnMouseDownOutsideCapturedElement(object sender, MouseButtonEventArgs e)
    {
        if (_composing)
        {
            return;
        }
        Complete(!string.IsNullOrWhiteSpace(_text.Text));
        e.Handled = true;
    }

    private void Complete(bool accepted)
    {
        if (_completing)
        {
            return;
        }
        _completing = true;
        if (Mouse.Captured == _text || IsDescendantOfTextBox(Mouse.Captured as DependencyObject))
        {
            Mouse.Capture(null);
        }
        DialogResult = accepted;
    }

    private bool IsDescendantOfTextBox(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, _text))
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
}
