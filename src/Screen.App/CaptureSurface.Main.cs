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
    private readonly HashSet<int> _workingStrokeBreaks = [];
    private PixelPoint? _lastOutsideMosaicPoint;
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
    private readonly CaptureChrome _chrome;

    private sealed class CaptureChrome(ResourceDictionary resources)
    {
        public Brush Before { get; } = (Brush)resources["Capture.Dim.Before"];
        public Brush After { get; } = (Brush)resources["Capture.Dim.After"];
        public Pen Selection { get; } = (Pen)resources["Capture.Selection"];
        public Pen Crosshair { get; } = (Pen)resources["Capture.Crosshair"];
        public Pen LoupeRing { get; } = (Pen)resources["Capture.LoupeRing"];
        public Pen Outline { get; } = (Pen)resources["Capture.LoupeOutline"];
        public Pen PixelGrid { get; } = (Pen)resources["Capture.PixelGrid"];
        public Pen PixelBorder { get; } = (Pen)resources["Capture.PixelBorder"];
        public Pen BrushOutline { get; } = (Pen)resources["Capture.BrushCursorOutline"];
        public Pen BrushRim { get; } = (Pen)resources["Capture.BrushCursorRim"];
        public Brush BrushFill { get; } = (Brush)resources["Capture.BrushCursorFill"];
        public Brush HandleFill { get; } = (Brush)resources["Capture.HandleFill"];
        public Brush ScanBand { get; } = (Brush)resources["Capture.ScanBand"];
        public Brush ScanLine { get; } = (Brush)resources["Capture.ScanLine"];
        public Style OcrHighlight { get; } = (Style)resources["Capture.OcrHighlight"];
        public ImageSource Handle { get; } = (ImageSource)resources["Capture.Handle"];
    }

    // Preserve the original wheel stops (1x–38x); the new lens is a 128-DIP circle.
    private static readonly int[] _magnifierSamples =
        [6, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 29, 33, 39, 45, 53, 61, 71, 83, 97, 115, 135, 159, 187, 230];
    internal double Magnification => 230d / _magnifierSamples[_magnifierSampleIndex];

    public CaptureSurface(
        CaptureFrame frame,
        IReadOnlyList<WindowTarget> windows,
        WindowSnapService snapService,
        ResourceDictionary? themeResources = null)
    {
        _frame = frame;
        if (frame.DisplayBounds.Count > 0 && NativeMethods.TryGetPhysicalCursorPosition(out var cursor))
            _cursor = new PixelPoint(cursor.X - frame.ScreenBounds.X, cursor.Y - frame.ScreenBounds.Y);
        _source = frame.ToBitmapSource();
        _sourceBuffer = frame.ToPixelBuffer();
        _windows = windows;
        _snapService = snapService;
        var resources = themeResources ?? Application.Current.Resources;
        _accentBrush = (Brush)resources["Brush.Accent"];
        // Isolated render workers deliberately supply only the shared compatibility keys.
        var chromeResources = resources.Contains("Capture.Dim.Before") ? resources :
            new ResourceDictionary { Source = new Uri("/Screen;component/Themes/CaptureSurface.xaml", UriKind.Relative) };
        _chrome = new CaptureChrome(chromeResources);
        CurrentStyle = new AnnotationStyle((uint)chromeResources["Capture.DefaultAnnotationArgb"], 3, 24);
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
    internal Rect CursorViewport => ViewportFor(new PixelRect((int)SampledPixel.X, (int)SampledPixel.Y, 1, 1));
    internal Rect SelectionViewport => ViewportFor(Selection);

    private Rect ViewportFor(PixelRect anchor) => DisplayViewport(_frame.ScreenBounds, _frame.DisplayBounds, RenderSize, anchor);

    internal static Rect DisplayViewport(PixelRect frame, IReadOnlyList<PixelRect> displays, Size size, PixelRect anchor)
    {
        // The overlay is one HWND with one DIP scale, even when its pixels span different monitor DPIs.
        var target = anchor with { X = frame.X + anchor.X, Y = frame.Y + anchor.Y };
        var centerX = target.X + target.Width / 2.0;
        var centerY = target.Y + target.Height / 2.0;
        var best = frame;
        long largestOverlap = -1;
        var nearest = double.MaxValue;
        foreach (var display in displays)
        {
            var visible = display.Intersect(frame);
            if (visible.IsEmpty)
                continue;
            var overlap = visible.Intersect(target);
            var area = (long)overlap.Width * overlap.Height;
            var dx = centerX - Math.Clamp(centerX, visible.X, visible.Right);
            var dy = centerY - Math.Clamp(centerY, visible.Y, visible.Bottom);
            var distance = dx * dx + dy * dy;
            if (area > largestOverlap || area == largestOverlap && distance < nearest)
            {
                largestOverlap = area;
                nearest = distance;
                best = visible;
            }
        }
        var sx = size.Width / frame.Width;
        var sy = size.Height / frame.Height;
        return new Rect((best.X - frame.X) * sx, (best.Y - frame.Y) * sy, best.Width * sx, best.Height * sy);
    }
    public WindowTarget? SelectedWindow { get; private set; }
    public bool IsSelectionCommitted => _selectionCommitted;
    internal event Action? AdornmentsRendered;
    internal PixelPoint SampledPixel => new(Math.Clamp(Math.Round(_cursor.X), 0, _frame.Width - 1), Math.Clamp(Math.Round(_cursor.Y), 0, _frame.Height - 1));
    internal PixelPoint SampledScreenPoint => ToScreenPoint(SampledPixel);
    internal Rect SelectionInDips => new(Selection.X * ActualWidth / _frame.Width, Selection.Y * ActualHeight / _frame.Height,
        Selection.Width * ActualWidth / _frame.Width, Selection.Height * ActualHeight / _frame.Height);
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

    public AnnotationStyle CurrentStyle { get; set; }
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

    private bool IsMosaicBrush => Tool == CaptureTool.Mosaic && MosaicMode == MosaicDrawMode.Brush;
    private bool CanShowMosaicBrushCursor => IsMouseOver
        && _selectionCommitted
        && !Selection.IsEmpty
        && IsMosaicBrush
        && Contains(Selection, _cursor);
    public bool ColorRgbMode { get; set; }
    public string ColorStatus { get; set; } = "Ctrl+C 复制色值 · Shift 切换格式";
    internal int NextNumber => _nextNumber;
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
    private PixelPoint ToPixel(Point point)
    {
        var x = point.X * _frame.Width / Math.Max(1, ActualWidth);
        var y = point.Y * _frame.Height / Math.Max(1, ActualHeight);
        // Captured mouse events can be outside even a full-frame selection.
        // Keep that position for the brush instead of sliding it along the frame edge.
        return _drawing && IsMosaicBrush
            ? new PixelPoint(x, y)
            : new PixelPoint(Math.Clamp(x, 0, _frame.Width - 1), Math.Clamp(y, 0, _frame.Height - 1));
    }

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

    internal static AnnotationItem OffsetItem(AnnotationItem item, double x, double y)
    {
        var bounds = GetPreciseBounds(item);
        var moved = bounds with { X = bounds.X + x, Y = bounds.Y + y };
        return item with
        {
            Bounds = RoundBounds(moved),
            PreciseBounds = moved,
            Points = item.Points.Select(point => new PixelPoint(point.X + x, point.Y + y)).ToArray()
        };
    }

    private static PreciseAnnotationBounds GetPreciseBounds(AnnotationItem item) => item.PreciseBounds
        ?? new PreciseAnnotationBounds(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height);

    private static PixelRect RoundBounds(PreciseAnnotationBounds bounds) => new(
        (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
        Math.Max(1, (int)Math.Round(bounds.Width)), Math.Max(1, (int)Math.Round(bounds.Height)));

    private static PixelRect CoverBounds(PreciseAnnotationBounds bounds) => new(
        (int)Math.Floor(bounds.X), (int)Math.Floor(bounds.Y),
        Math.Max(1, (int)Math.Ceiling(bounds.X + bounds.Width) - (int)Math.Floor(bounds.X)),
        Math.Max(1, (int)Math.Ceiling(bounds.Y + bounds.Height) - (int)Math.Floor(bounds.Y)));

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
