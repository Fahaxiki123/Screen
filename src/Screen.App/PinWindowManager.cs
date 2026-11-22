using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;
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
    private const int MinimumZoomedLongSide = 40;
    private readonly PixelRect _sourceBounds;
    private readonly WpfPath _outline;
    private readonly WpfPath _chosenOutline = new() { IsHitTestVisible = false };
    private readonly Grid _surfaceHost;
    private readonly Grid _artwork;
    private PinCompositionWindow? _compositor;
    private bool _compositionUnavailable;
    private bool _closing;
    private double _imageOpacity = 1;
    private int _refreshQueued;
    private Decorator? _chromePresenter;
    private readonly ScaleTransform _hoverScale = new();
    private readonly TranslateTransform _hoverOffset = new();
    private readonly TextBlock _zoomText;
    private readonly SquircleGlassBorder _zoomBadge;
    private readonly Brush _hoverBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0, 122, 255));
    private int _insetX, _insetY;
    private uint _imageDpi;
    private readonly Button _closeButton;
    private MenuItem? _clickThroughMenuItem;
    private NativeMethods.NativePoint _dragStart;
    private NativeMethods.NativePoint _dragImageOrigin;
    private bool _dragging;
    private bool _clickThrough;
    private double _zoom = 1;

    public PinWindow(PixelBuffer buffer, PixelRect sourceBounds)
    {
        _sourceBounds = sourceBounds;
        Title = "Flick 贴图";
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

        _closeButton = CloseButton();
        _closeButton.Click += (_, _) => Close();
        _closeButton.MouseEnter += (_, _) => SyncCloseVisual();
        _closeButton.MouseLeave += (_, _) => SyncCloseVisual();

        _outline = new WpfPath { StrokeThickness = 1.8, Stroke = _hoverBrush, IsHitTestVisible = false, Opacity = 0 };
        var shadow = new WpfPath
        {
            IsHitTestVisible = false,
            Fill = Brushes.White,
            Opacity = 0.30,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 3,
                Direction = 270,
                Color = Color.FromRgb(15, 23, 42)
            }
        };
        var shadowLayer = new Canvas { Children = { shadow }, IsHitTestVisible = false };
        var presenter = new ContentPresenter();
        presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding(nameof(Content)) { Source = this });
        var imageLayer = new Grid { Children = { presenter } };
        Panel.SetZIndex(_outline, 1);
        var outlineLayer = new Canvas { Children = { _outline, _chosenOutline }, IsHitTestVisible = false };
        _zoomText = new TextBlock
        {
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        Typography.SetNumeralAlignment(_zoomText, FontNumeralAlignment.Tabular);
        _zoomBadge = new SquircleGlassBorder
        {
            Child = _zoomText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadiusRadius = 6.5,
            Padding = new Thickness(10, 4, 10, 4),
            ShowRim = false,
            ShadowLevel = 0,
            GlassBackground = new SolidColorBrush(Color.FromArgb(190, 15, 23, 42)),
            IsHitTestVisible = false,
            Opacity = 0
        };
        var transform = new TransformGroup { Children = { _hoverScale, _hoverOffset } };
        _artwork = new Grid
        {
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transform,
            Children = { shadowLayer, imageLayer, outlineLayer, _zoomBadge }
        };
        // Layered HWNDs pass zero-alpha pixels through. Keep the original contour
        // hittable even where the artwork lifts away from the pointer.
        var hitSurface = new WpfPath { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        _surfaceHost = new Grid
        {
            ContextMenu = BuildContextMenu(buffer),
            Children = { hitSurface, _artwork, _closeButton }
        };
        _surfaceHost.SizeChanged += (_, e) =>
        {
            var contour = SquircleGeometry.Create(e.NewSize.Width, e.NewSize.Height, 10);
            hitSurface.Data = imageLayer.Clip = shadow.Data = _outline.Data = _chosenOutline.Data = contour;
        };
        Content = image;
        Template = new ControlTemplate(typeof(Window))
        {
            VisualTree = new FrameworkElementFactory(typeof(Decorator), "PART_PinChrome")
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => UpdateCompositionSurface();
        LocationChanged += (_, _) => UpdateCompositionSurface();
        Closed += (_, _) =>
        {
            _closing = true;
            RenderCapability.TierChanged -= CompositionEnvironmentChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            ReleaseCompositionSurface();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(KeepPhysicalImageSize));
        PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => _dragging = false;
        MouseDoubleClick += OnMouseDoubleClick;
        MouseWheel += OnMouseWheel;
        _surfaceHost.MouseEnter += (_, _) => Hover(true);
        _surfaceHost.MouseLeave += (_, _) => Hover(false);
    }

    private static Button CloseButton()
    {
        var button = new Button
        {
            Content = AppleVectorIcons.Create("close", 14),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "关闭贴图",
            Opacity = 0,
            IsHitTestVisible = false
        };
        button.SetResourceReference(StyleProperty, "PinCloseButtonStyle");
        return button;
    }

    private void SyncCloseVisual()
    {
        if (_compositor is not { } renderer)
            return;
        renderer.CloseVisual.SetResourceReference(BackgroundProperty, _closeButton.IsMouseOver ? "Brush.Danger" : "Brush.SurfaceRaised");
        renderer.CloseVisual.SetResourceReference(ForegroundProperty, _closeButton.IsMouseOver ? "Brush.OnAccent" : "Brush.TextPrimary");
    }

    private void UpdateCompositionSurface()
    {
        if (!IsLoaded || _closing || _compositionUnavailable)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var bounds))
            return;
        long pixels = (long)(bounds.Right - bounds.Left - 2 * _insetX) * (bounds.Bottom - bounds.Top - 2 * _insetY);
        if (pixels < 1_200_000 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || SystemParameters.HighContrast || System.Windows.Forms.SystemInformation.TerminalServerSession || RenderCapability.Tier < (2 << 16))
        {
            ReleaseCompositionSurface();
            return;
        }
        try
        {
            if (_compositor is null)
            {
                _compositor = new PinCompositionWindow(this, CloseButton());
                _compositor.Closed += RendererClosed;
            }
            if (!_compositor.Fit(bounds, _surfaceHost.Margin, _imageOpacity))
                throw new InvalidOperationException("The DWM thumbnail could not be positioned.");
            if (!_compositor.IsVisible)
            {
                _surfaceHost.Children.Remove(_artwork);
                _compositor.Layers.Children.Add(_artwork);
                _compositor.Layers.Children.Add(_compositor.CloseVisual);
                _compositor.CloseVisual.Opacity = _closeButton.Opacity;
                _closeButton.BeginAnimation(OpacityProperty, null);
                _closeButton.Opacity = 0;
                // The underlying hit plane is only one alpha unit. Dimming the
                // host as well could round those pixels down to click-through.
                Opacity = 1;
                SyncCloseVisual();
                _compositor.Show();
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or ArgumentException or UnauthorizedAccessException)
        {
            _compositionUnavailable = true;
            ReleaseCompositionSurface();
            SimpleLogger.LogWarning($"Pin uses the layered rendering path: {ex.Message}");
        }
    }

    private void ReleaseCompositionSurface(bool closeRenderer = true)
    {
        if (_compositor is not { } renderer)
            return;
        _compositor = null;
        renderer.Closed -= RendererClosed;
        renderer.Layers.Children.Clear();
        if (closeRenderer)
            renderer.Close();
        if (!_surfaceHost.Children.Contains(_artwork))
            _surfaceHost.Children.Insert(1, _artwork);
        _closeButton.Opacity = _closeButton.IsHitTestVisible ? 1 : 0;
        Opacity = _imageOpacity;
    }

    private void RendererClosed(object? sender, EventArgs e)
    {
        _compositionUnavailable = true;
        ReleaseCompositionSurface(closeRenderer: false);
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e) => CompositionEnvironmentChanged(sender, e);

    private void CompositionEnvironmentChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0 || Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_closing)
                return;
            ReleaseCompositionSurface();
            _compositionUnavailable = false;
            UpdateCompositionSurface();
        }));
    }

    public override void OnApplyTemplate()
    {
        if (_chromePresenter is not null)
            _chromePresenter.Child = null;
        base.OnApplyTemplate();
        _chromePresenter = (Decorator)GetTemplateChild("PART_PinChrome");
        _chromePresenter.Child = _surfaceHost;
    }

    private void Hover(bool hovered)
    {
        hovered &= !_clickThrough;
        _closeButton.IsHitTestVisible = hovered;
        EaseTo(_compositor?.CloseVisual ?? _closeButton, UIElement.OpacityProperty, hovered ? 1 : 0);
        EaseTo(_outline, UIElement.OpacityProperty, hovered ? 1 : 0);
        EaseTo(_hoverScale, ScaleTransform.ScaleXProperty, hovered ? 1.015 : 1);
        EaseTo(_hoverScale, ScaleTransform.ScaleYProperty, hovered ? 1.015 : 1);
        EaseTo(_hoverOffset, TranslateTransform.YProperty, hovered ? -2 : 0);
    }

    private static void EaseTo(IAnimatable element, DependencyProperty property, double target) =>
        element.BeginAnimation(property, new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

    public bool IsClickThrough => _clickThrough;
    public event Action<bool>? ClickThroughChanged;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle).AddHook(OnDisplayMessage);
        RenderCapability.TierChanged += CompositionEnvironmentChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            _sourceBounds.X + _sourceBounds.Width / 2,
            _sourceBounds.Y + _sourceBounds.Height / 2));
        SetImageBounds(handle, _sourceBounds.X, _sourceBounds.Y, _sourceBounds.Width, _sourceBounds.Height, dpi);
    }

    private nint OnDisplayMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Settings/high contrast, display topology, desktop compositor restarted.
        if (message is 0x001A or 0x007E or 0x031E)
            CompositionEnvironmentChanged(this, EventArgs.Empty);
        return 0;
    }

    private void SetImageBounds(nint handle, int x, int y, int width, int height, uint dpi)
    {
        // Give the shadow and the 1.5% hover lift their own HWND space. Keep the
        // image at its original physical coordinates and size inside that space.
        double dip = 96.0 / Math.Max(1u, dpi);
        _imageDpi = dpi;
        _insetX = (int)Math.Ceiling(12 / dip + width * 0.0075);
        _insetY = (int)Math.Ceiling(14 / dip + height * 0.0075);
        _surfaceHost.Margin = new Thickness(_insetX * dip, _insetY * dip, _insetX * dip, _insetY * dip);
        int outerWidth = width + 2 * _insetX, outerHeight = height + 2 * _insetY;
        Width = outerWidth * dip;
        Height = outerHeight * dip;
        NativeMethods.SetWindowPos(handle, new nint(-1), x - _insetX, y - _insetY, outerWidth, outerHeight,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        UpdateCompositionSurface();
    }

    private void KeepPhysicalImageSize()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!IsVisible || handle == 0 || !NativeMethods.GetWindowRect(handle, out var rect))
            return;
        var dpi = NativeMethods.GetDpiForWindow(handle);
        if (dpi == 0 || dpi == _imageDpi)
            return;

        // WPF preserves DIPs on WM_DPICHANGED; a pin's 100% zoom means physical source pixels.
        int x = rect.Left + _insetX, y = rect.Top + _insetY;
        if (_dragging && NativeMethods.TryGetPhysicalCursorPosition(out var cursor))
        {
            x = _dragImageOrigin.X + cursor.X - _dragStart.X;
            y = _dragImageOrigin.Y + cursor.Y - _dragStart.Y;
        }
        SetZoomedBounds(handle, x, y, dpi);
    }

    private void SetZoomedBounds(nint handle, int x, int y, uint dpi) =>
        SetImageBounds(handle, x, y, Math.Max(1, (int)Math.Round(_sourceBounds.Width * _zoom)),
            Math.Max(1, (int)Math.Round(_sourceBounds.Height * _zoom)), dpi);

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
            item.Click += (_, _) =>
            {
                _imageOpacity = value / 100.0;
                if (_compositor == null)
                    Opacity = _imageOpacity;
                UpdateCompositionSurface();
            };
            opacity.Items.Add(item);
        }

        var outline = new MenuItem { Header = "描边" };
        AddOutlineItem(outline, "无描边", Brushes.Transparent, 0);
        AddOutlineItem(outline, "浅色描边", new SolidColorBrush(Color.FromArgb(220, 235, 238, 242)), 1);
        AddOutlineItem(outline, "深色描边", new SolidColorBrush(Color.FromArgb(220, 43, 48, 55)), 1);
        AddOutlineItem(outline, "蓝色描边", new SolidColorBrush(Color.FromRgb(0, 122, 255)), 2);

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
                    MessageBox.Show(this, $"保存贴图失败：{ex.Message}", "Flick", MessageBoxButton.OK, MessageBoxImage.Error);
                }
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
            _chosenOutline.Stroke = brush;
            _chosenOutline.StrokeThickness = thickness;
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
        if (handle == 0 || !NativeMethods.TryGetPhysicalCursorPosition(out _dragStart) || !NativeMethods.GetWindowRect(handle, out var bounds))
        {
            return;
        }
        _dragImageOrigin = new NativeMethods.NativePoint(bounds.Left + _insetX, bounds.Top + _insetY);
        _dragging = CaptureMouse();
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
            _dragImageOrigin.X + cursor.X - _dragStart.X - _insetX,
            _dragImageOrigin.Y + cursor.Y - _dragStart.Y - _insetY, 0, 0,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoSize);
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
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            return;
        }
        var minZoom = Math.Clamp(MinimumZoomedLongSide / (double)Math.Max(_sourceBounds.Width, _sourceBounds.Height), 0.2, 1.0);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.1, e.Delta / (double)Mouse.MouseWheelDeltaForOneLine), minZoom, 5.0);
        SetZoomedBounds(handle, rect.Left + _insetX, rect.Top + _insetY, NativeMethods.GetDpiForWindow(handle));
        _zoomText.Text = $"{_zoom * 100:0}%";
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, TimeSpan.Zero));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(570)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(750)));
        _zoomBadge.BeginAnimation(UIElement.OpacityProperty, fade);
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
            if (_clickThroughMenuItem is not null)
            {
                _clickThroughMenuItem.IsChecked = enabled;
            }
            return;
        }
        _clickThrough = enabled;
        Hover(_surfaceHost.IsMouseOver);
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
