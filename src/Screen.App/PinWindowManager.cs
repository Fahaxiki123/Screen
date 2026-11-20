using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

internal sealed class PinWindowManager(
    Func<(PinOutlineColor Color, double Thickness)> getDefaults,
    Action<PinOutlineColor?, double?> saveChange) : IPinWindowManager
{
    private readonly List<PinWindow> _windows = [];

    public int Count => _windows.Count;
    public bool HasClickThroughWindows => _windows.Any(window => window.IsClickThrough);
    public event EventHandler? ClickThroughEnabled;

    public void Pin(PixelBuffer image, PixelRect sourceBounds)
    {
        var (color, thickness) = getDefaults();
        var window = new PinWindow(image, sourceBounds, color, thickness, saveChange);
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
    private const int MinimumZoomedLongSide = 40;
    private const double CornerRadiusDip = 12;
    private readonly PixelRect _sourceBounds;
    private readonly System.Windows.Shapes.Path _outline;
    private readonly Button _closeButton;
    private MenuItem? _clickThroughMenuItem;
    private NativeMethods.NativePoint _dragStart;
    private NativeMethods.NativeRect _windowStart;
    private bool _dragging;
    private bool _clickThrough;
    private double _zoom = 1;

    public PinWindow(PixelBuffer buffer, PixelRect sourceBounds)
        : this(buffer, sourceBounds, PinOutlineColor.Subtle, AppSettings.DefaultPinOutlineWidth)
    {
    }

    public PinWindow(PixelBuffer buffer, PixelRect sourceBounds, PinOutlineColor color, double thickness,
        Action<PinOutlineColor?, double?>? saveChange = null)
    {
        _sourceBounds = sourceBounds;
        Title = "Screen 贴图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        SetResourceReference(BackgroundProperty, "Brush.Transparent");
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
        _closeButton.SetResourceReference(StyleProperty, "Pin.Close");
        _closeButton.Click += (_, _) => Close();

        var silhouette = new RectangleGeometry { RadiusX = CornerRadiusDip, RadiusY = CornerRadiusDip };
        _outline = new System.Windows.Shapes.Path
        {
            Data = silhouette,
            // Only the inner half of a stroke on the clip boundary is visible.
            StrokeThickness = 2 * thickness,
            IsHitTestVisible = false
        };
        SetOutlineColor(color);
        // The outline follows the image size; it must not contribute to layout.
        var outlineLayer = new Canvas { IsHitTestVisible = false };
        outlineLayer.Children.Add(_outline);
        var root = new Grid
        {
            Clip = silhouette,
            ContextMenu = BuildContextMenu(buffer, color, thickness, saveChange)
        };
        root.SizeChanged += (_, e) => silhouette.Rect = new Rect(e.NewSize);
        root.SetResourceReference(Grid.BackgroundProperty, "Brush.Transparent");
        root.Children.Add(image);
        root.Children.Add(outlineLayer);
        root.Children.Add(_closeButton);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += (_, _) => EndDrag();
        MouseDoubleClick += OnMouseDoubleClick;
        MouseWheel += OnMouseWheel;
        MouseEnter += (_, _) => _closeButton.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => _closeButton.Visibility = Visibility.Collapsed;
    }

    public bool IsClickThrough => _clickThrough;
    public event Action<bool>? ClickThroughChanged;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            _sourceBounds.X + _sourceBounds.Width / 2,
            _sourceBounds.Y + _sourceBounds.Height / 2));
        Width = _sourceBounds.Width * 96.0 / dpi;
        Height = _sourceBounds.Height * 96.0 / dpi;
        NativeMethods.SetWindowPos(
            handle,
            new nint(-1),
            _sourceBounds.X,
            _sourceBounds.Y,
            _sourceBounds.Width,
            _sourceBounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private ContextMenu BuildContextMenu(PixelBuffer buffer, PinOutlineColor initialColor, double initialThickness,
        Action<PinOutlineColor?, double?>? saveChange)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(StyleProperty, "GlassMenuStyle");
        var opacity = new MenuItem { Header = "透明度" };
        foreach (var value in new[] { 100, 80, 60, 40, 20 })
        {
            var item = new MenuItem { Header = $"{value}%" };
            item.Click += (_, _) => Opacity = value / 100.0;
            opacity.Items.Add(item);
        }

        var colors = new MenuItem { Header = "描边颜色" };
        foreach (var (color, label) in new[]
        {
            (PinOutlineColor.Subtle, "淡描边"),
            (PinOutlineColor.None, "无描边"),
            (PinOutlineColor.Light, "浅色描边"),
            (PinOutlineColor.Dark, "深色描边"),
            (PinOutlineColor.Blue, "蓝色描边")
        })
        {
            AddChoice(colors, label, color == initialColor, () =>
            {
                SetOutlineColor(color);
                saveChange?.Invoke(color, null);
            });
        }

        var thicknesses = new MenuItem { Header = "描边粗细" };
        foreach (var thickness in AppSettings.PinOutlineWidths)
        {
            AddChoice(thicknesses, FormattableString.Invariant($"{thickness:0.#} px"), thickness == initialThickness, () =>
            {
                _outline.StrokeThickness = 2 * thickness;
                saveChange?.Invoke(null, thickness);
            });
        }

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
                try
                {
                    BitmapUtilities.SavePng(buffer, dialog.FileName);
                }
                catch (Exception ex)
                {
                    SimpleLogger.LogError("Failed to save pinned image", ex);
                    MessageBox.Show(this, $"保存贴图失败：{ex.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        };
        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();
        menu.Items.Add(opacity);
        menu.Items.Add(colors);
        menu.Items.Add(thicknesses);
        menu.Items.Add(through);
        menu.Items.Add(save);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        return menu;
    }

    private void SetOutlineColor(PinOutlineColor color) =>
        _outline.SetResourceReference(Shape.StrokeProperty, color switch
        {
            PinOutlineColor.None => "Brush.Transparent",
            PinOutlineColor.Light => "Pin.OutlineLight",
            PinOutlineColor.Dark => "Pin.OutlineDark",
            PinOutlineColor.Blue => "Brush.Accent",
            _ => "Pin.OutlineSubtle"
        });

    private static void AddChoice(MenuItem parent, string label, bool selected, Action apply)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected };
        item.Click += (_, _) =>
        {
            foreach (MenuItem choice in parent.Items)
                choice.IsChecked = choice == item;
            apply();
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
        var scale = Math.Max(_zoom, MinimumZoomedLongSide / (double)Math.Max(_sourceBounds.Width, _sourceBounds.Height));
        NativeMethods.SetWindowPos(
            handle,
            new nint(-1),
            rect.Left,
            rect.Top,
            Math.Max(1, (int)Math.Round(_sourceBounds.Width * scale)),
            Math.Max(1, (int)Math.Round(_sourceBounds.Height * scale)),
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        e.Handled = true;
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
            _clickThroughMenuItem?.IsChecked = enabled;
            return;
        }
        _clickThrough = enabled;
        _clickThroughMenuItem?.IsChecked = enabled;
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
