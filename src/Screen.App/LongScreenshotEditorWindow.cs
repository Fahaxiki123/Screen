using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Automation;
using Screen.Core;

namespace Screen.App;

internal sealed class LongScreenshotEditorWindow : Window
{
    private const int MaxWorkingDimension = 16000;

    private PixelBuffer? _sourceImage;
    private PixelRect _sourceRegion;
    private readonly LongScreenshotHistory _cropHistory = new();
    private readonly ResourceDictionary? _themeResources;
    private int _viewRevision;
    private PixelBuffer _originalImage;
    private int _currentFactor;
    private PixelBuffer _image;
    private CaptureSurface _surface;
    private readonly ScrollViewer _viewport;
    private readonly Border _canvasHost;
    private TextBlock _zoomText = null!;
    private readonly Dictionary<CaptureTool, Button> _toolButtons = [];
    private readonly Dictionary<double, Button> _thicknessButtons = [];
    private readonly Dictionary<uint, Button> _colorButtons = [];
    private SpringTrack _thicknessTrack = null!;
    private SpringTrack _drawingTrack = null!;
    private SpringTrack _colorTrack = null!;
    private StackPanel _styleOptions = null!;
    private StackPanel _cropOptions = null!;
    private Button _cropButton = null!;
    private uint _color = 0xFFFF3B30;
    private double _thickness = 4;
    private double _zoom = 1;
    private bool _fitWidth = true;
    private bool _cropping;
    private CaptureTool _toolBeforeCrop = CaptureTool.Rectangle;

    public LongScreenshotEditorWindow(PixelBuffer image, ResourceDictionary? themeResources = null)
    {
        _sourceImage = image;
        _sourceRegion = new PixelRect(0, 0, image.Width, image.Height);
        _themeResources = themeResources;
        _originalImage = image;
        _currentFactor = WorkingScaleFactor(image);
        _image = _currentFactor == 1 ? image : Downscale(image, _currentFactor);
        Title = "Flick 长截图编辑";
        Width = Math.Min(1320, SystemParameters.WorkArea.Width * 0.92);
        Height = Math.Min(900, SystemParameters.WorkArea.Height * 0.92);
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Theme("Brush.WindowBackdrop", "#F4F6F9");
        Foreground = Theme("Brush.TextPrimary", "#1D1D1F");
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            UseAeroCaptionButtons = false
        });

        // The surface must operate in the downscaled working-copy space: every
        // consumer (SetZoom, InitializeCommittedSelection, ApplyCrop, export)
        // scales selection coordinates by _currentFactor, and starting from the
        // original full-resolution image would scale them twice.
        _surface = CreateSurface(_image, CaptureTool.Rectangle);
        UpdateAnnotationStyle();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var (header, footer) = BuildToolbars();
        root.Children.Add(header);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _canvasHost = new Border
        {
            Background = Theme("Brush.Surface", "#FFFFFF"),
            BorderBrush = Theme("Brush.Border", "#210F172A"),
            BorderThickness = new Thickness(1),
            Child = _surface
        };
        _viewport = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Theme("Brush.WindowBackdrop", "#F4F6F9"),
            Padding = new Thickness(38),
            Content = _canvasHost
        };
        _viewport.PreviewMouseWheel += OnViewportMouseWheel;
        Grid.SetRow(_viewport, 1);
        root.Children.Add(_viewport);
        DesktopWindowChrome.SetEditorContent(this, root, Theme("Brush.Border", "#210F172A"));

        Loaded += (_, _) => FitWidth();
        DpiChanged += (_, _) =>
        {
            if (_fitWidth)
                FitWidth();
            else
                SetZoom(_zoom);
        };
        SizeChanged += (_, _) =>
        {
            if (_fitWidth && IsLoaded)
            {
                FitWidth();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (CaptureKeyboardRouting.PreserveTextInput(e.Key, Keyboard.FocusedElement as DependencyObject))
            {
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (_cropping)
                {
                    CancelCrop();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
            }
            else if ((e.Key is Key.Delete or Key.Back) && Keyboard.Modifiers == ModifierKeys.None && _surface.DeleteSelectedAnnotation())
            {
                e.Handled = true;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z)
            {
                UndoEdit();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y)
            {
                RedoEdit();
                e.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            _cropHistory.Clear();
            _sourceImage = null;
        };
    }

    private CaptureSurface CreateSurface(PixelBuffer image, CaptureTool tool)
    {
        var frame = new CaptureFrame(
            image.Pixels,
            image.Width,
            image.Height,
            image.Stride,
            96,
            96,
            new PixelRect(0, 0, image.Width, image.Height),
            CaptureColorSpace.Srgb,
            DateTimeOffset.UtcNow);
        var surface = new CaptureSurface(frame, [], new WindowSnapService(), _themeResources)
        {
            Tool = tool,
            ShowSelectionChrome = false
        };
        surface.InitializeCommittedSelection(new PixelRect(0, 0, image.Width, image.Height), showSelectionChrome: false);
        surface.ScreenPointMapper = point =>
        {
            var display = new Point(
                point.X * surface.ActualWidth / Math.Max(1, image.Width),
                point.Y * surface.ActualHeight / Math.Max(1, image.Height));
            var screen = surface.PointToScreen(display);
            return new PixelPoint(screen.X, screen.Y);
        };
        surface.SelectedAnnotationChanged += (_, _) => SyncStyleFromSelectedAnnotation();
        surface.AnnotationEdited += (_, _) => _cropHistory.ClearRedo();
        return surface;
    }

    private (FrameworkElement Header, FrameworkElement Footer) BuildToolbars()
    {
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var zoom = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(zoom, 1);
        footer.Children.Add(zoom);
        zoom.Children.Add(Separator());
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 0, 2, 0)
        };
        zoom.Children.Add(IconButton("zoom-out", "缩小", (_, _) => SetZoom(_zoom / 1.15)));
        _zoomText = new TextBlock
        {
            Width = 54,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };
        _zoomText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Typography.SetNumeralAlignment(_zoomText, FontNumeralAlignment.Tabular);
        zoom.Children.Add(_zoomText);
        zoom.Children.Add(IconButton("zoom-in", "放大", (_, _) => SetZoom(_zoom * 1.15)));
        zoom.Children.Add(TextButton("适应宽度", (_, _) => FitWidth()));
        zoom.Children.Add(TextButton("1:1", (_, _) => SetZoom(1)));
        var drawingTools = new StackPanel { Orientation = Orientation.Horizontal };
        _drawingTrack = new SpringTrack(drawingTools, 6.5);
        tools.Children.Add(_drawingTrack);
        AddTool(drawingTools, CaptureTool.Select, "cursor", "选择和调整标注");
        AddTool(drawingTools, CaptureTool.Rectangle, "rect", "矩形");
        AddTool(drawingTools, CaptureTool.Ellipse, "ellipse", "圆形");
        AddTool(drawingTools, CaptureTool.Line, "line", "直线");
        AddTool(drawingTools, CaptureTool.Arrow, "arrow", "箭头");
        AddTool(drawingTools, CaptureTool.Pen, "pen", "画笔");
        AddTool(drawingTools, CaptureTool.Text, "text", "文字");
        AddTool(drawingTools, CaptureTool.Number, "number", "序号");
        AddTool(drawingTools, CaptureTool.Mosaic, "mosaic", "马赛克");
        _cropButton = IconButton("crop", "在当前窗口裁切", (_, _) => BeginCrop());
        tools.Children.Add(_cropButton);
        tools.Children.Add(Separator());
        tools.Children.Add(IconButton("undo", "撤销", (_, _) => UndoEdit()));
        tools.Children.Add(IconButton("redo", "重做", (_, _) => RedoEdit()));

        var topBar = new Grid();
        topBar.ColumnDefinitions.Add(new ColumnDefinition());
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toolScroller = new ScrollViewer
        {
            Content = tools,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        topBar.Children.Add(toolScroller);

        var dragHandle = new SquircleGlassBorder
        {
            Height = 34,
            Margin = new Thickness(8, 0, 8, 0),
            Padding = new Thickness(10, 0, 10, 0),
            GlassBackground = Theme("Brush.SurfaceSunken", "#0E0F172A"),
            CornerRadiusRadius = 6.5,
            ShowRim = false,
            ShadowLevel = 0,
            Cursor = Cursors.SizeAll,
            ToolTip = "按住这里拖动窗口，双击最大化或还原",
            Child = new TextBlock
            {
                Text = "拖动窗口",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme("Brush.TextSecondary", "#9E1D1D1F")
            }
        };
        dragHandle.MouseLeftButtonDown += (_, e) => BeginWindowDrag(e);
        Grid.SetColumn(dragHandle, 1);
        topBar.Children.Add(dragHandle);
        topBar.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<Button>(source) is not null
                || FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is not null)
            {
                return;
            }
            BeginWindowDrag(e);
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent
        };
        actions.Children.Add(TextButton("完成并复制", (_, _) => FinishAndCopy(), primary: true));
        actions.Children.Add(IconButton("minus", "最小化", (_, _) => WindowState = WindowState.Minimized));
        actions.Children.Add(IconButton("close", "关闭", (_, _) => Close()));
        Grid.SetColumn(actions, 2);
        topBar.Children.Add(actions);


        _styleOptions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 0)
        };
        _styleOptions.Children.Add(new TextBlock
        {
            Text = "粗细",
            FontSize = 13,
            Foreground = Theme("Brush.TextSecondary", "#9E1D1D1F"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var thicknessOptions = new StackPanel { Orientation = Orientation.Horizontal };
        _thicknessTrack = new SpringTrack(thicknessOptions, 6);
        _styleOptions.Children.Add(_thicknessTrack);
        foreach (var size in new[] { 2d, 4d, 7d })
        {
            var button = new Button
            {
                Width = 32,
                MinWidth = 32,
                Height = 30,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Content = new System.Windows.Shapes.Ellipse
                {
                    Width = Math.Max(5, size * 1.6),
                    Height = Math.Max(5, size * 1.6),
                    Fill = Theme("Brush.TextPrimary", "#1D1D1F")
                },
                ToolTip = $"{size:0}px"
            };
            button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
            button.Click += (_, _) =>
            {
                _thickness = size;
                UpdateAnnotationStyle();
                ApplyCurrentStyleToSelection(sizeChanged: true);
                UpdateStyleSelectionVisuals();
            };
            _thicknessButtons[size] = button;
            thicknessOptions.Children.Add(button);
        }
        _styleOptions.Children.Add(Separator());
        var colors = new StackPanel { Orientation = Orientation.Horizontal };
        _colorTrack = new SpringTrack(colors, 12, colorRing: true);
        _styleOptions.Children.Add(_colorTrack);
        foreach (var color in new[] { 0xFFFF3B30u, 0xFFFFCC00u, 0xFF34C759u, 0xFF007AFFu, 0xFF1C1C1Eu, 0xFF8E8E93u, 0xFFFFFFFFu })
        {
            var swatch = new Button { Width = 34, MinWidth = 34, Height = 32, Margin = new Thickness(2), Padding = new Thickness(0) };
            swatch.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
            swatch.Content = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = BrushFromArgb(color),
                Stroke = Theme("Brush.Border", "#210F172A"),
                StrokeThickness = 1
            };
            swatch.ToolTip = ColorText.ToHex(color);
            AutomationProperties.SetName(swatch, swatch.ToolTip.ToString());
            swatch.Click += (_, _) =>
            {
                _color = color;
                UpdateAnnotationStyle();
                ApplyCurrentStyleToSelection();
                UpdateStyleSelectionVisuals();
            };
            _colorButtons[color] = swatch;
            colors.Children.Add(swatch);
        }
        footer.Children.Add(_styleOptions);

        _cropOptions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 0),
            Visibility = Visibility.Collapsed
        };
        _cropOptions.Children.Add(new TextBlock
        {
            Text = "拖动边框或控制点调整裁切范围",
            FontSize = 13,
            Foreground = Theme("Brush.TextSecondary", "#9E1D1D1F"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        _cropOptions.Children.Add(TextButton("应用裁切", (_, _) => ApplyCrop(), primary: true));
        _cropOptions.Children.Add(TextButton("取消", (_, _) => CancelCrop()));
        footer.Children.Add(_cropOptions);
        SelectTool(CaptureTool.Rectangle);
        UpdateStyleSelectionVisuals();

        var headerBar = new SquircleGlassBorder { Child = topBar, Padding = new Thickness(4), Margin = new Thickness(10, 8, 10, 4) };
        var footerBar = new SquircleGlassBorder
        {
            Child = footer,
            Padding = new Thickness(4),
            Margin = new Thickness(10, 4, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        return (headerBar, footerBar);
    }

    private void BeginWindowDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
        DragMove();
        e.Handled = true;
    }

    private void AddTool(Panel panel, CaptureTool tool, string icon, string tooltip)
    {
        var button = IconButton(icon, tooltip, (_, _) => SelectTool(tool));
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    private void SelectTool(CaptureTool tool)
    {
        if (_cropping && tool != CaptureTool.Select)
        {
            ApplyCrop();
        }
        _surface.Tool = tool;
        if (tool == CaptureTool.Mosaic)
        {
            _surface.MosaicMode = MosaicDrawMode.Brush;
        }
        _drawingTrack.Select(_cropping ? null : _toolButtons.GetValueOrDefault(tool));
        foreach (var (candidate, button) in _toolButtons)
        {
            button.Foreground = !_cropping && candidate == tool
                ? Theme("Brush.AccentText", "#007AFF")
                : Theme("Brush.TextPrimary", "#1D1D1F");
        }
        UpdateAnnotationStyle();
        UpdateStyleSelectionVisuals();
    }

    private void UpdateAnnotationStyle()
    {
        var variant = _surface.Tool switch
        {
            CaptureTool.Mosaic => AnnotationVariants.MosaicPixelate + 100,
            CaptureTool.Text => AnnotationVariants.TextRegular,
            _ => 0
        };
        var thickness = _surface.Tool == CaptureTool.Mosaic ? 36 : _thickness;
        var fontSize = _surface.Tool == CaptureTool.Number ? NumberSizeForOption(_thickness) : 22;
        _surface.CurrentStyle = new AnnotationStyle(_color, thickness, fontSize, variant, 0.72);
    }

    private static double NumberSizeForOption(double option) => option <= 2 ? 24 : option <= 4 ? 32 : 44;

    private void ApplyCurrentStyleToSelection(bool sizeChanged = false)
    {
        if (_surface.SelectedAnnotationStyle is not { } selected)
        {
            return;
        }
        var style = sizeChanged ? _surface.SelectedAnnotationKind switch
        {
            AnnotationKind.Mosaic => selected,
            AnnotationKind.Number => selected with { FontSize = NumberSizeForOption(_thickness) },
            _ => selected with { Thickness = _thickness }
        } : selected with { Argb = _color };
        _surface.ApplyStyleToSelected(style);
    }

    private void SyncStyleFromSelectedAnnotation()
    {
        if (_surface.SelectedAnnotationStyle is { } selected)
        {
            _color = selected.Argb;
            if (_surface.SelectedAnnotationKind == AnnotationKind.Number)
            {
                _thickness = _thicknessButtons.Keys.MinBy(value => Math.Abs(NumberSizeForOption(value) - selected.FontSize));
            }
            else if (_surface.SelectedAnnotationKind != AnnotationKind.Mosaic)
            {
                _thickness = _thicknessButtons.Keys.MinBy(value => Math.Abs(value - selected.Thickness));
            }
        }
        UpdateStyleSelectionVisuals();
    }

    private void UpdateStyleSelectionVisuals()
    {
        var numberTool = _surface.SelectedAnnotationKind is { } selectedKind
            ? selectedKind == AnnotationKind.Number
            : _surface.Tool == CaptureTool.Number;
        foreach (var (size, button) in _thicknessButtons)
        {
            button.ToolTip = numberTool ? $"序号直径 {NumberSizeForOption(size):0}px" : $"{size:0}px";
        }
        _thicknessTrack.Select(_thicknessButtons.GetValueOrDefault(_thickness));
        _colorTrack.Select(_colorButtons.GetValueOrDefault(_color));
    }

    private double FitWidthScale()
    {
        var available = Math.Max(100, _viewport.ActualWidth - _viewport.Padding.Left - _viewport.Padding.Right - 24);
        return available * VisualTreeHelper.GetDpi(this).DpiScaleX / _originalImage.Width;
    }

    private void FitWidth() => SetZoom(FitWidthScale(), preserveFitMode: true);

    private void SetZoom(double zoom, bool preserveFitMode = false)
    {
        _fitWidth = preserveFitMode;
        _zoom = Math.Clamp(zoom, Math.Min(0.08, FitWidthScale()), 4.0);
        // Display scale is relative to source pixels. An integer reduction of
        // the working bitmap must not redefine 1:1 or the scale readout.
        var dpi = VisualTreeHelper.GetDpi(this);
        _surface.Width = Math.Max(1, _originalImage.Width * _zoom / dpi.DpiScaleX);
        _surface.Height = Math.Max(1, _originalImage.Height * _zoom / dpi.DpiScaleY);
        _zoomText.Text = $"{_zoom:P0}";
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }
        SetZoom(_zoom * (e.Delta > 0 ? 1.12 : 1 / 1.12));
        e.Handled = true;
    }

    private void BeginCrop()
    {
        if (_cropping)
        {
            return;
        }
        _cropping = true;
        _toolBeforeCrop = _surface.Tool;
        _surface.ClearAnnotationSelection();
        _surface.AllowAnnotationEditing = false;
        SelectTool(CaptureTool.Select);
        _surface.InitializeCommittedSelection(new PixelRect(0, 0, _image.Width, _image.Height), showSelectionChrome: true);
        _styleOptions.Visibility = Visibility.Collapsed;
        _cropOptions.Visibility = Visibility.Visible;
        _cropButton.Background = Theme("Brush.AccentSubtleDark", "#1C007AFF");
        _cropButton.Foreground = Theme("Brush.AccentText", "#007AFF");
    }

    private void ApplyCrop()
    {
        if (!_cropping || _surface.Selection.IsEmpty)
        {
            return;
        }
        var selectionOriginal = ScaleRect(_surface.Selection, _currentFactor)
            .Intersect(new PixelRect(0, 0, _originalImage.Width, _originalImage.Height));
        if (selectionOriginal == new PixelRect(0, 0, _originalImage.Width, _originalImage.Height))
        {
            CancelCrop();
            return;
        }
        var before = CaptureEditState(_toolBeforeCrop);
        var sourceRegion = new PixelRect(
            _sourceRegion.X + selectionOriginal.X,
            _sourceRegion.Y + selectionOriginal.Y,
            selectionOriginal.Width,
            selectionOriginal.Height);
        var factor = WorkingScaleFactor(sourceRegion.Width, sourceRegion.Height);
        var annotations = LongScreenshotHistory.RebaseAnnotations(
            before.Annotations.Current, selectionOriginal, _currentFactor, factor);
        var after = before with
        {
            SourceRegion = sourceRegion,
            Annotations = annotations,
            FitWidth = true,
            HorizontalOffset = 0,
            VerticalOffset = 0
        };
        // Keep the unannotated source and transform the annotation coordinates;
        // flattening them into pixels would make them uneditable after a crop.
        RestoreEditState(after);
        _cropHistory.RecordCrop(before);
    }

    private void UndoEdit()
    {
        if (_cropping)
        {
            CancelCrop();
            return;
        }
        _surface.CompletePendingAnnotationEdit();
        if (_surface.CanUndo)
        {
            _surface.Undo();
        }
        else if (_cropHistory.Undo(CaptureEditState()) is { } previous)
        {
            RestoreEditState(previous);
        }
    }

    private void RedoEdit()
    {
        if (_cropping)
        {
            CancelCrop();
            return;
        }
        _surface.CompletePendingAnnotationEdit();
        if (_surface.CanRedo)
        {
            _surface.Redo();
        }
        else if (_cropHistory.Redo(CaptureEditState()) is { } next)
        {
            RestoreEditState(next);
        }
    }

    private LongScreenshotEditState CaptureEditState(CaptureTool? tool = null) => new(
        _sourceRegion,
        _surface.ExportEditingState(),
        tool ?? _surface.Tool,
        _color,
        _thickness,
        _zoom,
        _fitWidth,
        _viewport.HorizontalOffset,
        _viewport.VerticalOffset);

    private void RestoreEditState(LongScreenshotEditState state)
    {
        var source = _sourceImage ?? throw new ObjectDisposedException(nameof(LongScreenshotEditorWindow));
        var image = state.SourceRegion == new PixelRect(0, 0, source.Width, source.Height)
            ? source
            : source.Crop(state.SourceRegion);
        var factor = WorkingScaleFactor(image);
        var workingImage = factor == 1 ? image : Downscale(image, factor);
        var surface = CreateSurface(workingImage, state.Tool);

        _sourceRegion = state.SourceRegion;
        _originalImage = image;
        _currentFactor = factor;
        _image = workingImage;
        _surface = surface;
        var viewRevision = ++_viewRevision;
        _canvasHost.Child = surface;
        _color = state.Color;
        _thickness = state.Thickness;
        ExitCropUi();
        surface.ImportEditingState(state.Annotations);
        SelectTool(state.Tool);
        UpdateStyleSelectionVisuals();
        if (state.FitWidth)
        {
            FitWidth();
        }
        else
        {
            SetZoom(state.Zoom);
        }
        // The new ScrollViewer extent is available after layout. Ignore a
        // queued restoration when a later undo/redo already installed a view.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_sourceImage is not null && _viewRevision == viewRevision)
            {
                _viewport.ScrollToHorizontalOffset(state.HorizontalOffset);
                _viewport.ScrollToVerticalOffset(state.VerticalOffset);
            }
        }));
    }

    private void CancelCrop()
    {
        if (!_cropping)
        {
            return;
        }
        _surface.AllowAnnotationEditing = true;
        _surface.InitializeCommittedSelection(new PixelRect(0, 0, _image.Width, _image.Height), showSelectionChrome: false);
        _surface.Tool = _toolBeforeCrop;
        ExitCropUi();
        SelectTool(_toolBeforeCrop);
    }

    private void ExitCropUi()
    {
        _cropping = false;
        _styleOptions.Visibility = Visibility.Visible;
        _cropOptions.Visibility = Visibility.Collapsed;
        _cropButton.Background = Brushes.Transparent;
        _cropButton.Foreground = Theme("Brush.TextPrimary", "#1D1D1F");
    }

    private void FinishAndCopy()
    {
        try
        {
            var selectionOriginal = ScaleRect(_surface.Selection, _currentFactor);
            BitmapUtilities.CopyToClipboard(
                _surface.RenderSelection(_originalImage, selectionOriginal, _currentFactor));
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Long screenshot editor copy failed", exception);
            MessageBox.Show(this, $"长截图复制失败，请重试。\n\n{exception.Message}",
                "Flick", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Close();
    }

    private static int WorkingScaleFactor(PixelBuffer image) => WorkingScaleFactor(image.Width, image.Height);

    private static int WorkingScaleFactor(int width, int height)
    {
        var maxSide = Math.Max(width, height);
        return maxSide <= MaxWorkingDimension
            ? 1
            : (int)Math.Ceiling(maxSide / (double)MaxWorkingDimension);
    }

    private static PixelRect ScaleRect(PixelRect rectangle, int factor) => new(
        (int)Math.Round(rectangle.X * (double)factor),
        (int)Math.Round(rectangle.Y * (double)factor),
        Math.Max(1, (int)Math.Round(rectangle.Width * (double)factor)),
        Math.Max(1, (int)Math.Round(rectangle.Height * (double)factor)));

    private static PixelBuffer Downscale(PixelBuffer image, int factor)
    {
        var width = Math.Max(1, (image.Width + factor - 1) / factor);
        var height = Math.Max(1, (image.Height + factor - 1) / factor);
        var stride = checked(width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        var columnSums = new long[width * 3];
        for (var y = 0; y < height; y++)
        {
            Array.Clear(columnSums);
            var sourceY = y * factor;
            var rowCount = Math.Min(factor, image.Height - sourceY);
            for (var row = 0; row < rowCount; row++)
            {
                var sourceRow = (sourceY + row) * image.Stride;
                for (var x = 0; x < width; x++)
                {
                    var sourceX = x * factor;
                    var columnCount = Math.Min(factor, image.Width - sourceX);
                    var source = sourceRow + sourceX * 4;
                    for (var column = 0; column < columnCount; column++)
                    {
                        columnSums[x * 3] += image.Pixels[source];
                        columnSums[x * 3 + 1] += image.Pixels[source + 1];
                        columnSums[x * 3 + 2] += image.Pixels[source + 2];
                        source += 4;
                    }
                }
            }
            var targetRow = y * stride;
            for (var x = 0; x < width; x++)
            {
                var sampleCount = rowCount * Math.Min(factor, image.Width - x * factor);
                var target = targetRow + x * 4;
                pixels[target] = (byte)(columnSums[x * 3] / sampleCount);
                pixels[target + 1] = (byte)(columnSums[x * 3 + 1] / sampleCount);
                pixels[target + 2] = (byte)(columnSums[x * 3 + 2] / sampleCount);
                pixels[target + 3] = 255;
            }
        }
        return new PixelBuffer(pixels, width, height, stride);
    }

    private static Button IconButton(string icon, string tooltip, RoutedEventHandler handler)
    {
        var button = new Button { Content = AppleVectorIcons.Create(icon), ToolTip = tooltip };
        AutomationProperties.SetName(button, tooltip);
        button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
        button.Click += handler;
        return button;
    }

    private static Button TextButton(string text, RoutedEventHandler handler, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Height = 36,
            MinWidth = primary ? 104 : 58,
            Padding = new Thickness(11, 0, 11, 0),
            FontSize = 14,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = primary ? new Thickness(8, 0, 0, 0) : new Thickness(2),
            Background = primary ? Theme("Brush.PrimaryAction", "#007AFF") : Brushes.Transparent,
            Foreground = primary ? Theme("Brush.OnAccent", "#FFFFFF") : Theme("Brush.TextPrimary", "#1D1D1F")
        };
        button.SetResourceReference(StyleProperty, primary ? "CardPrimaryButtonStyle" : "CardSecondaryButtonStyle");
        button.Click += handler;
        return button;
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 22,
        Background = Theme("Brush.Border", "#210F172A"),
        Margin = new Thickness(8, 5, 8, 5)
    };

    private static Brush BrushFromArgb(uint color) => new SolidColorBrush(Color.FromArgb(
        (byte)(color >> 24),
        (byte)(color >> 16),
        (byte)(color >> 8),
        (byte)color));

    private static Brush Theme(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as Brush
        ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

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

}
