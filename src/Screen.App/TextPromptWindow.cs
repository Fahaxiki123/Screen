using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        _text = new TextBox
        {
            Width = 300,
            MinHeight = 44,
            MaxHeight = 180,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = Math.Clamp(style.FontSize, 14, 42),
            FontWeight = weight,
            // The editor text uses the theme for legibility. The caller still
            // renders Value with the untouched AnnotationStyle/Argb.
            Foreground = Token("Brush.TextPrimary"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            CaretBrush = Token("Brush.TextPrimary"),
            SelectionBrush = Token("Brush.Accent"),
            SelectionOpacity = 0.28,
            Text = initialValue ?? string.Empty
        };
        _text.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] =
            Application.Current.FindResource("DarkScrollBarStyle");
        _text.PreviewKeyDown += OnKeyDown;
        // Rounded navy card: the TextBox itself cannot round its corners,
        // so the chrome lives on the wrapping Border.
        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Token("Brush.SurfaceRaised"),
            BorderBrush = Token("Brush.Accent"),
            BorderThickness = new Thickness(1.5),
            Child = _text
        };
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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            (int)Math.Round(_screenPoint.X),
            (int)Math.Round(_screenPoint.Y)));
        var dpiScale = dpi / 96.0;
        _text.FontSize = Math.Clamp(_style.FontSize / dpiScale, 10, 42);
        TextBlock.SetLineHeight(_text, _style.FontSize * 1.22 / dpiScale);
        if (_maxContentWidth is double maxContentWidth)
        {
            _text.Width = Math.Max(48, maxContentWidth / dpiScale + 24);
        }
        var width = (int)Math.Round((_text.Width + 4) * dpiScale);
        var height = (int)Math.Round(Math.Max(48, ActualHeight) * dpi / 96.0);
        NativeMethods.SetWindowPos(
            handle,
            new nint(-1),
            (int)Math.Round(_screenPoint.X - 12 * dpiScale),
            (int)Math.Round(_screenPoint.Y - 9 * dpiScale),
            width,
            height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
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
