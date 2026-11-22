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
    private PixelBuffer? _translatedSelection;
    private BitmapSource? _translatedSelectionSource;
    private PixelBuffer? _translatedFrame;
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
    private readonly Brush _accentBrush;
    private readonly Brush _selectionMask;

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
        _selectionMask = resources["Brush.SelectionMask"] as Brush ?? new SolidColorBrush(Color.FromArgb(0x57, 15, 23, 42));
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        InitializeProcessingVisual();
        InitializeCaptureChrome();
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

    // A source pixel occupies [x, x + 1) even when monitor scaling places
    // the pointer between integral coordinates on the capture surface.
    private (int X, int Y) HoverPixel => ((int)_cursor.X, (int)_cursor.Y);

    internal uint HoverColor
    {
        get
        {
            var (x, y) = HoverPixel;
            var offset = y * _sourceBuffer.Stride + x * 4;
            return 0xFF000000u
                   | ((uint)_sourceBuffer.Pixels[offset + 2] << 16)
                   | ((uint)_sourceBuffer.Pixels[offset + 1] << 8)
                   | _sourceBuffer.Pixels[offset];
        }
    }

    // Remove floating-point noise at exact device-pixel boundaries before
    // the selection and magnifier take the integral part (e.g. at 150% DPI).
    private PixelPoint ToPixel(Point point) => new(
        Math.Clamp(Math.Round(point.X * _frame.Width / Math.Max(1, ActualWidth), 8), 0, _frame.Width - 1),
        Math.Clamp(Math.Round(point.Y * _frame.Height / Math.Max(1, ActualHeight), 8), 0, _frame.Height - 1));

    private PixelPoint ClampAnnotationPoint(PixelPoint point) => new(
        Math.Clamp(point.X, Selection.X, Selection.Right),
        Math.Clamp(point.Y, Selection.Y, Selection.Bottom));

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

    internal static PixelRect SelectionFromDrag(PixelPoint start, PixelPoint end)
    {
        var left = (int)Math.Floor(Math.Min(start.X, end.X));
        var top = (int)Math.Floor(Math.Min(start.Y, end.Y));
        var right = (int)Math.Floor(Math.Max(start.X, end.X));
        var bottom = (int)Math.Floor(Math.Max(start.Y, end.Y));
        return new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }
}
