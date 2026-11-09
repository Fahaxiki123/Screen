using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screen.Core;

namespace Screen.App;

internal enum CaptureTool
{
    Select,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Pen,
    Text,
    Number,
    Mosaic
}

internal enum MosaicDrawMode
{
    Brush,
    Rectangle
}

internal static class AnnotationVariants
{
    public const int TextRegular = 0;
    public const int TextMedium = 1;
    public const int TextBold = 2;
    public const int MosaicPixelate = 10;
    public const int MosaicBlur = 11;
}

/// <summary>
/// Interactive capture surface with selection, annotation, and magnifier features.
/// Split into partial classes: Main (fields/properties), Input (mouse/keyboard),
/// Rendering (drawing), Annotations (annotation management), Processing (scan glow).
/// </summary>
internal sealed partial class CaptureSurface : FrameworkElement
{
    private readonly CaptureFrame _frame;
    private readonly BitmapSource _source;
    private readonly PixelBuffer _sourceBuffer;
    private readonly IReadOnlyList<WindowTarget> _windows;
    private readonly WindowSnapService _snapService;
    private readonly List<AnnotationItem> _annotations = [];
    private readonly AnnotationHistory _history = new();
    private AnnotationSnapshot? _annotationGestureStart;
    private readonly List<PixelPoint> _workingPoints = [];
    private PixelPoint _start;
    private PixelPoint _cursor;
    private bool _draggingSelection;
    private bool _movingSelection;
    private bool _resizingSelection;
    private SelectionResizeHandle _resizeHandle;
    private PixelRect _selectionAtDragStart;
    private bool _drawing;
    private bool _draggingAnnotation;
    private bool _resizingAnnotation;
    private SelectionResizeHandle _annotationResizeHandle;
    private bool _annotationMoved;
    private bool _annotationWasSelected;
    private AnnotationItem? _annotationAtDragStart;
    private Guid? _selectedAnnotationId;
    private bool _selectionCommitted;
    private int _nextNumber = 1;
    private CaptureTool _tool = CaptureTool.Select;
    private MosaicDrawMode _mosaicMode = MosaicDrawMode.Brush;
    private PixelBuffer? _mosaicComposite;
    private WriteableBitmap? _mosaicCompositeSource;
    private bool _mosaicCompositeDirty = true;
    private List<MosaicSnapshot>? _appliedMosaicSnapshots;
    private int _magnifierSampleIndex = 9;

    // Theme tokens used by the per-frame render path. Looked up once in the
    // constructor: fetching resources on every OnRender is measurable.
    private readonly Brush _accentBrush;
    private readonly Brush _overlayBaseBrush;
    private readonly Brush _overlayBorderBrush;
    private readonly Brush _overlayTextBrush;
    private readonly Brush _overlayTextDimBrush;
    private readonly Brush _overlayIconBrush;
    private readonly Brush _neutral500Brush;

    // Sample widths → zoom = MagnifierWidth / sample, from 1× to 38×.
    // Default = sample 23 ≈ 10×.
    private static readonly int[] MagnifierSamples =
        [6, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 29, 33, 39, 45, 53, 61, 71, 83, 97, 115, 135, 159, 187, 230];
    private const int MagnifierWidth = 230;
    private const int MagnifierHeight = 160;

    // The displayed height varies slightly per zoom (≈160, up to 167); keep a
    // few spare rows so the shared buffer can never be overrun.
    private readonly byte[] _magnifierBuffer = new byte[MagnifierWidth * (MagnifierHeight + 8) * 4];

    private static readonly Typeface PanelMono = new(
        new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface PanelMonoBold = new(
        new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface PanelUi = new("Segoe UI");

    public CaptureSurface(
        CaptureFrame frame,
        IReadOnlyList<WindowTarget> windows,
        WindowSnapService snapService,
        ResourceDictionary? themeResources = null)
    {
        _frame = frame;
        _source = frame.ToBitmapSource();
        _sourceBuffer = frame.ToPixelBuffer();
        _windows = windows;
        _snapService = snapService;
        var resources = themeResources ?? Application.Current.Resources;
        _accentBrush = (Brush)resources["Brush.Accent"];
        _overlayBaseBrush = (Brush)resources["Brush.OverlayBase"];
        _overlayBorderBrush = (Brush)resources["Brush.OverlayBorder"];
        _overlayTextBrush = (Brush)resources["Brush.OverlayText"];
        _overlayTextDimBrush = (Brush)resources["Brush.OverlayTextDim"];
        _overlayIconBrush = (Brush)resources["Brush.OverlayIcon"];
        _neutral500Brush = (Brush)resources["Brush.Neutral500"];
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        InitializeProcessingVisual();
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? SelectionCommitted;
    public event EventHandler? SelectedAnnotationChanged;
    internal event EventHandler? AnnotationEdited;
    internal event EventHandler? AnnotationContentChanged;
    public PixelRect Selection { get; private set; }
    public WindowTarget? SelectedWindow { get; private set; }
    public bool IsSelectionCommitted => _selectionCommitted;
    public PixelRect MagnifierAvoidanceArea { get; set; }
    public bool ShowSelectionChrome { get; set; } = true;
    public bool AllowAnnotationEditing { get; set; } = true;
    public Func<PixelPoint, PixelPoint>? ScreenPointMapper { get; set; }

    public CaptureTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            UpdateIdleCursor();
            InvalidateVisual();
        }
    }

    public AnnotationStyle CurrentStyle { get; set; } = new(0xFFFF3B30, 3, 24);
    public MosaicDrawMode MosaicMode
    {
        get => _mosaicMode;
        set
        {
            _mosaicMode = value;
            UpdateIdleCursor();
            InvalidateVisual();
        }
    }

    private bool CanShowMosaicBrushCursor => IsMouseOver
        && _selectionCommitted
        && !Selection.IsEmpty
        && Tool == CaptureTool.Mosaic
        && MosaicMode == MosaicDrawMode.Brush
        && Contains(Selection, _cursor);
    public bool ColorRgbMode { get; set; }
    public string ColorStatus { get; set; } = "Ctrl+C 复制色值 · Shift 切换格式";
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    public AnnotationStyle? SelectedAnnotationStyle => _selectedAnnotationId is Guid id
        ? _annotations.FirstOrDefault(item => item.Id == id)?.Style
        : null;

    public AnnotationKind? SelectedAnnotationKind => _selectedAnnotationId is Guid id
        ? _annotations.FirstOrDefault(item => item.Id == id)?.Kind
        : null;

    public void InitializeCommittedSelection(PixelRect selection, bool showSelectionChrome = true)
    {
        Selection = selection.Intersect(new PixelRect(0, 0, _frame.Width, _frame.Height));
        _selectionCommitted = !Selection.IsEmpty;
        ShowSelectionChrome = showSelectionChrome;
        SelectedWindow = null;
        UpdateIdleCursor();
        RefreshProcessingVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    internal uint HoverColor => GetPixelColor(_cursor);

    // Utility methods
    private PixelPoint ToPixel(Point point) => new(
        Math.Clamp(point.X * _frame.Width / Math.Max(1, ActualWidth), 0, _frame.Width - 1),
        Math.Clamp(point.Y * _frame.Height / Math.Max(1, ActualHeight), 0, _frame.Height - 1));

    private PixelPoint ClampAnnotationPoint(PixelPoint point) => new(
        Math.Clamp(point.X, Selection.X, Selection.Right),
        Math.Clamp(point.Y, Selection.Y, Selection.Bottom));

    private uint GetPixelColor(PixelPoint point)
    {
        var x = (int)Math.Round(point.X);
        var y = (int)Math.Round(point.Y);
        x = Math.Clamp(x, 0, _sourceBuffer.Width - 1);
        y = Math.Clamp(y, 0, _sourceBuffer.Height - 1);
        var offset = y * _sourceBuffer.Stride + x * 4;
        return 0xFF000000u
               | ((uint)_sourceBuffer.Pixels[offset + 2] << 16)
               | ((uint)_sourceBuffer.Pixels[offset + 1] << 8)
               | _sourceBuffer.Pixels[offset];
    }

    private PixelPoint ToScreenPoint(PixelPoint point) => ScreenPointMapper?.Invoke(point)
        ?? new PixelPoint(_frame.ScreenBounds.X + point.X, _frame.ScreenBounds.Y + point.Y);

    private static Rect ToRect(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static PixelRect Offset(PixelRect rect, int x, int y) => new(rect.X + x, rect.Y + y, rect.Width, rect.Height);

    private static PixelRect Inflate(PixelRect rect, int amount) => new(
        rect.X - amount,
        rect.Y - amount,
        rect.Width + amount * 2,
        rect.Height + amount * 2);

    private static AnnotationItem OffsetItem(AnnotationItem item, int x, int y) => item with
    {
        Bounds = Offset(item.Bounds, x, y),
        Points = item.Points.Select(point => new PixelPoint(point.X + x, point.Y + y)).ToArray()
    };

    private static PixelRect BoundsAroundPoints(IReadOnlyList<PixelPoint> points, double padding)
    {
        if (points.Count == 0)
        {
            return new PixelRect(0, 0, 1, 1);
        }
        var left = (int)Math.Floor(points.Min(point => point.X) - padding);
        var top = (int)Math.Floor(points.Min(point => point.Y) - padding);
        var right = (int)Math.Ceiling(points.Max(point => point.X) + padding);
        var bottom = (int)Math.Ceiling(points.Max(point => point.Y) + padding);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static bool Contains(PixelRect rect, PixelPoint point) =>
        point.X >= rect.X && point.X <= rect.Right && point.Y >= rect.Y && point.Y <= rect.Bottom;

    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static double DistanceToSegment(PixelPoint point, PixelPoint start, PixelPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= double.Epsilon)
        {
            return Distance(point, start);
        }
        var t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0, 1);
        return Distance(point, new PixelPoint(start.X + t * dx, start.Y + t * dy));
    }

    private static int ClampDelta(int value, int minimum, int maximum) => minimum <= maximum
        ? Math.Clamp(value, minimum, maximum)
        : 0;

    private static PixelRect RectFromPoints(PixelPoint a, PixelPoint b) => new(
        (int)Math.Floor(Math.Min(a.X, b.X)),
        (int)Math.Floor(Math.Min(a.Y, b.Y)),
        Math.Max(1, (int)Math.Ceiling(Math.Abs(a.X - b.X))),
        Math.Max(1, (int)Math.Ceiling(Math.Abs(a.Y - b.Y))));
}
