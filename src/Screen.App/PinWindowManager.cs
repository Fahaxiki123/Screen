using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

internal sealed class PinWindowManager : IPinWindowManager
{
    private readonly List<PinWindow> _windows = [];

    public int Count => _windows.Count;
    public bool HasClickThroughWindows => _windows.Any(window => window.IsClickThrough);
    public event EventHandler? ClickThroughEnabled;

    public void Pin(PixelBuffer image, PixelRect sourceBounds)
    {
        var window = new PinWindow(image, sourceBounds);
        window.Closed += (_, _) => _windows.Remove(window);
        window.ClickThroughChanged += enabled =>
        {
            if (enabled)
            {
                ClickThroughEnabled?.Invoke(this, EventArgs.Empty);
            }
        };
        _windows.Add(window);
        window.Show();
    }

    public void DisableClickThroughForAll()
    {
        foreach (var window in _windows.ToArray())
        {
            window.SetClickThrough(false);
        }
    }

    public void CloseAll()
    {
        foreach (var window in _windows.ToArray())
        {
            window.Close();
        }
    }
}

internal sealed class PinWindow : Window
{
    private const double OuterMarginDip = 2;
    private const double ContentPaddingDip = 9;
    private const double TotalInsetDip = OuterMarginDip + ContentPaddingDip;
    private readonly PixelRect _sourceBounds;
    private readonly Rectangle _outline;
    private readonly Button _closeButton;
    private MenuItem? _clickThroughMenuItem;
    private NativeMethods.NativePoint _dragStart;
    private NativeMethods.NativeRect _windowStart;
    private bool _dragging;
    private bool _clickThrough;
    private bool _outlineExplicit;
    private double _zoom = 1;
    private double _dpi = 96;

    public PinWindow(PixelBuffer buffer, PixelRect sourceBounds)
    {
        _sourceBounds = sourceBounds;
        Title = "Screen 贴图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;

        var image = new Image
        {
            Source = buffer.ToBitmapSource(),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var closeIcon = new LucideIconElement
        {
            Kind = LucideIconKind.X,
            Width = 14,
            Height = 14,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeIcon.SetBinding(
            LucideIconElement.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
        _closeButton = new Button
        {
            Content = closeIcon,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "关闭贴图",
            Visibility = Visibility.Collapsed
        };
        _closeButton.SetResourceReference(StyleProperty, "PinCloseButtonStyle");
        _closeButton.Click += (_, _) => Close();

        var imageFrame = new Border
        {
            Margin = new Thickness(OuterMarginDip),
            Padding = new Thickness(ContentPaddingDip),
            Background = SampleEdgeBrush(buffer),
            CornerRadius = new CornerRadius(11),
            ClipToBounds = true,
            Child = image
        };
        _outline = new Rectangle
        {
            Stroke = (Brush)Application.Current.FindResource("Brush.Accent"),
            StrokeThickness = 1.5,
            RadiusX = 12,
            RadiusY = 12,
            Margin = new Thickness(0.75),
            SnapsToDevicePixels = false,
            IsHitTestVisible = false,
            Opacity = 0
        };
        var root = new Grid
        {
            Background = Brushes.Transparent,
            ContextMenu = BuildContextMenu(buffer)
        };
        root.Children.Add(imageFrame);
        root.Children.Add(_outline);
        root.Children.Add(_closeButton);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += (_, _) => EndDrag();
        MouseDoubleClick += OnMouseDoubleClick;
        MouseWheel += OnMouseWheel;
        // At rest a pin is pure content: chrome (close button + outline) only
        // appears while hovered, unless the user pinned an explicit outline.
        MouseEnter += (_, _) =>
        {
            _closeButton.Visibility = Visibility.Visible;
            if (!_outlineExplicit)
            {
                _outline.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
            }
        };
        MouseLeave += (_, _) =>
        {
            _closeButton.Visibility = Visibility.Collapsed;
            if (!_outlineExplicit)
            {
                _outline.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)));
            }
        };
    }

    public bool IsClickThrough => _clickThrough;
    public event Action<bool>? ClickThroughChanged;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            _sourceBounds.X + _sourceBounds.Width / 2,
            _sourceBounds.Y + _sourceBounds.Height / 2));
        var inset = (int)Math.Ceiling(TotalInsetDip * _dpi / 96.0);
        Width = _sourceBounds.Width * 96.0 / _dpi + TotalInsetDip * 2;
        Height = _sourceBounds.Height * 96.0 / _dpi + TotalInsetDip * 2;
        NativeMethods.SetWindowPos(
            handle,
            new nint(-1),
            _sourceBounds.X - inset,
            _sourceBounds.Y - inset,
            _sourceBounds.Width + inset * 2,
            _sourceBounds.Height + inset * 2,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private ContextMenu BuildContextMenu(PixelBuffer buffer)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(BackgroundProperty, "Brush.Surface");
        menu.SetResourceReference(ForegroundProperty, "Brush.TextPrimary");
        menu.SetResourceReference(BorderBrushProperty, "Brush.Border");
        var opacity = new MenuItem { Header = "透明度" };
        foreach (var value in new[] { 100, 80, 60, 40, 20 })
        {
            var item = new MenuItem { Header = $"{value}%" };
            item.Click += (_, _) => Opacity = value / 100.0;
            opacity.Items.Add(item);
        }

        var outline = new MenuItem { Header = "描边" };
        AddOutlineItem(outline, "无描边", Brushes.Transparent, 0);
        AddOutlineItem(outline, "浅色描边", new SolidColorBrush(Color.FromArgb(220, 235, 238, 242)), 1);
        AddOutlineItem(outline, "深色描边", new SolidColorBrush(Color.FromArgb(220, 43, 48, 55)), 1);
        AddOutlineItem(outline, "蓝色描边", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), 2);

        var through = new MenuItem { Header = "鼠标穿透（从托盘恢复）", IsCheckable = true };
        _clickThroughMenuItem = through;
        through.Click += (_, _) => SetClickThrough(through.IsChecked);
        var save = new MenuItem { Header = "保存为 PNG" };
        save.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存贴图",
                Filter = "PNG 图片 (*.png)|*.png",
                DefaultExt = ".png",
                AddExtension = true,
                FileName = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };
            if (dialog.ShowDialog(this) == true)
            {
                BitmapUtilities.SavePng(buffer, dialog.FileName);
            }
        };
        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();
        menu.Items.Add(opacity);
        menu.Items.Add(outline);
        menu.Items.Add(through);
        menu.Items.Add(save);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        return menu;
    }

    private void AddOutlineItem(MenuItem parent, string text, Brush brush, double thickness)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) =>
        {
            // An explicitly chosen outline stays visible even when not hovered.
            _outlineExplicit = thickness > 0;
            _outline.BeginAnimation(UIElement.OpacityProperty, null);
            _outline.Opacity = thickness > 0 ? 1 : 0;
            _outline.Stroke = brush;
            _outline.StrokeThickness = Math.Max(1, thickness);
        };
        parent.Items.Add(item);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !NativeMethods.TryGetPhysicalCursorPosition(out _dragStart) || !NativeMethods.GetWindowRect(handle, out _windowStart))
        {
            return;
        }
        _dragging = true;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !NativeMethods.TryGetPhysicalCursorPosition(out var cursor))
        {
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            handle,
            new nint(-1),
            _windowStart.Left + cursor.X - _dragStart.X,
            _windowStart.Top + cursor.Y - _dragStart.Y,
            _windowStart.Right - _windowStart.Left,
            _windowStart.Bottom - _windowStart.Top,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void EndDrag()
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && FindAncestor<Button>(e.OriginalSource as DependencyObject) is null)
        {
            Close();
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 0.9), 0.2, 5.0);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            return;
        }
        NativeMethods.SetWindowPos(
            handle,
            new nint(-1),
            rect.Left,
            rect.Top,
            Math.Max(40, (int)Math.Round(_sourceBounds.Width * _zoom))
            + (int)Math.Ceiling(TotalInsetDip * 2 * _dpi / 96.0),
            Math.Max(40, (int)Math.Round(_sourceBounds.Height * _zoom))
            + (int)Math.Ceiling(TotalInsetDip * 2 * _dpi / 96.0),
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        e.Handled = true;
    }

    private static Brush SampleEdgeBrush(PixelBuffer buffer)
    {
        var red = new int[256];
        var green = new int[256];
        var blue = new int[256];
        var samples = 0;
        var stepX = Math.Max(1, buffer.Width / 512);
        var stepY = Math.Max(1, buffer.Height / 512);
        void Add(int x, int y)
        {
            var offset = y * buffer.Stride + x * 4;
            blue[buffer.Pixels[offset]]++;
            green[buffer.Pixels[offset + 1]]++;
            red[buffer.Pixels[offset + 2]]++;
            samples++;
        }
        for (var x = 0; x < buffer.Width; x += stepX)
        {
            Add(x, 0);
            if (buffer.Height > 1)
                Add(x, buffer.Height - 1);
        }
        for (var y = stepY; y < buffer.Height - 1; y += stepY)
        {
            Add(0, y);
            if (buffer.Width > 1)
                Add(buffer.Width - 1, y);
        }
        byte Median(int[] histogram)
        {
            var target = Math.Max(1, (samples + 1) / 2);
            var count = 0;
            for (var value = 0; value < histogram.Length; value++)
            {
                count += histogram[value];
                if (count >= target)
                {
                    return (byte)value;
                }
            }
            return 0;
        }
        return new SolidColorBrush(Color.FromRgb(Median(red), Median(green), Median(blue)));
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    public void SetClickThrough(bool enabled)
    {
        if (_clickThrough == enabled)
        {
            if (_clickThroughMenuItem is not null)
            {
                _clickThroughMenuItem.IsChecked = enabled;
            }
            return;
        }
        _clickThrough = enabled;
        if (_clickThroughMenuItem is not null)
        {
            _clickThroughMenuItem.IsChecked = enabled;
        }
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        style = enabled ? style | NativeMethods.WsExTransparent : style & ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)style);
        NativeMethods.SetWindowPos(
            handle,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove
            | NativeMethods.SwpNoSize
            | NativeMethods.SwpNoZOrder
            | NativeMethods.SwpNoActivate
            | NativeMethods.SwpFrameChanged);
        ClickThroughChanged?.Invoke(enabled);
    }
}
