using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureSurface
{
    private static readonly int[] Magnifications = [4, 8, 12];
    private static readonly Pen SelectionInnerLine = FrozenPen(0xEB, 255, 255, 255, 1);
    private readonly Canvas _captureHud = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly TextBlock _sizeText = NumericText(12);
    private readonly TextBlock _colorText = NumericText(11);
    private readonly TextBlock _coordinates = NumericText(11);
    private readonly TextBlock _zoomText = NumericText(10);
    private readonly SolidColorBrush _swatchBrush = new();
    private readonly StackPanel _loupe = new() { Orientation = Orientation.Vertical };
    private readonly TranslateTransform _loupeOffset = new();
    private readonly MagnifierLens _lens = new();
    private readonly Grid _lensHolder = new() { Width = 116, Height = 116 };
    private readonly SquircleGlassBorder _zoomBadge = new()
    {
        GlassBackground = new SolidColorBrush(Color.FromArgb(145, 0, 0, 0)),
        CornerRadiusRadius = 5.5,
        ShadowLevel = 0,
        ShowRim = false,
        Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(0, 8, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Opacity = 0
    };
    private readonly SquircleGlassBorder _sizeBadge = Badge();
    private readonly SquircleGlassBorder _colorBadge = Badge();
    private readonly Ellipse[] _handles = new Ellipse[8];
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Point _loupeTarget;
    private bool _followingLoupe;
    private bool _placedLoupe;
    private int _zoomIndex = 1;
    private string _colorStatus = string.Empty;

    public string ColorStatus
    {
        get => _colorStatus;
        set
        {
            _colorStatus = value;
            _colorText.SetResourceReference(TextBlock.ForegroundProperty,
                string.IsNullOrEmpty(value) ? "Brush.TextPrimary" : value.StartsWith('✓') ? "Brush.Success" : "Brush.Danger");
            _feedbackTimer.Stop();
            _feedbackTimer.Start();
            Pulse(_colorBadge);
            InvalidateVisual();
        }
    }

    private static SquircleGlassBorder Badge() => new()
    {
        CornerRadiusRadius = 7.5,
        Padding = new Thickness(9, 4, 9, 4),
        ShadowLevel = 1,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static TextBlock NumericText(double fontSize)
    {
        var text = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center
        };
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        return text;
    }

    private void InitializeCaptureChrome()
    {
        AddVisualChild(_captureHud);
        AddLogicalChild(_captureHud);
        _sizeBadge.Child = _sizeText;
        _captureHud.Children.Add(_sizeBadge);
        for (int i = 0; i < _handles.Length; i++)
        {
            var handle = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = Brushes.White,
                Stroke = FrozenPen(0x59, 15, 23, 42, 0.5).Brush,
                StrokeThickness = 0.5,
                Effect = new DropShadowEffect { BlurRadius = 2.5, ShadowDepth = 1, Opacity = 0.28 },
                RenderTransform = new ScaleTransform(),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            _handles[i] = handle;
            _captureHud.Children.Add(handle);
        }

        _lensHolder.Children.Add(new Ellipse
        {
            Fill = Brushes.White,
            Effect = new DropShadowEffect { ShadowDepth = 5, BlurRadius = 15, Direction = 270, Opacity = 0.3 }
        });
        _lensHolder.Children.Add(_lens);
        _zoomBadge.Child = _zoomText;
        _zoomText.Foreground = Brushes.White;
        _lensHolder.Children.Add(_zoomBadge);
        _loupe.Children.Add(_lensHolder);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = _swatchBrush,
            Stroke = FrozenPen(36, 15, 23, 42, 1).Brush,
            StrokeThickness = 1,
            Margin = new Thickness(0, 0, 6, 0)
        });
        _colorText.FontWeight = FontWeights.SemiBold;
        row.Children.Add(_colorText);
        _coordinates.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        row.Children.Add(_coordinates);
        _colorBadge.Margin = new Thickness(0, 8, 0, 0);
        _colorBadge.Child = row;
        _loupe.Children.Add(_colorBadge);
        _loupe.RenderTransform = _loupeOffset;
        _captureHud.Children.Add(_loupe);

        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            _colorStatus = string.Empty;
            _colorText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
            InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            StopFollowingLoupe();
            _feedbackTimer.Stop();
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _captureHud.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _captureHud.Arrange(new Rect(finalSize));
        return base.ArrangeOverride(finalSize);
    }

    private void RefreshCaptureChrome()
    {
        var scaleX = ActualWidth / _frame.Width;
        var scaleY = ActualHeight / _frame.Height;
        var rect = new Rect(Selection.X * scaleX, Selection.Y * scaleY, Selection.Width * scaleX, Selection.Height * scaleY);
        bool hasChrome = ShowSelectionChrome && !Selection.IsEmpty;
        _sizeBadge.Visibility = hasChrome ? Visibility.Visible : Visibility.Collapsed;
        if (hasChrome)
        {
            _sizeText.Text = $"{Selection.Width} × {Selection.Height}";
            _sizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_sizeBadge, Math.Clamp(rect.Left, 0, Math.Max(0, ActualWidth - _sizeBadge.DesiredSize.Width)));
            Canvas.SetTop(_sizeBadge, rect.Top >= _sizeBadge.DesiredSize.Height + 6
                ? rect.Top - _sizeBadge.DesiredSize.Height - 6 : Math.Max(0, Math.Min(rect.Top + 7, ActualHeight - _sizeBadge.DesiredSize.Height)));
        }
        PositionHandles(rect, hasChrome && _selectionCommitted);
        _loupe.Visibility = _selectionCommitted ? Visibility.Collapsed : Visibility.Visible;
        if (_selectionCommitted)
        {
            StopFollowingLoupe();
            _placedLoupe = false;
            return;
        }

        var (px, py) = HoverPixel;
        int zoom = Magnifications[_zoomIndex];
        _lens.UpdateSample(_sourceBuffer, px, py, zoom);
        uint argb = HoverColor;
        _swatchBrush.Color = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        _colorText.Text = string.IsNullOrEmpty(_colorStatus)
            ? ColorRgbMode ? ColorText.ToRgb(argb) : ColorText.ToHex(argb)
            : _colorStatus;
        _coordinates.Text = $"  ·  {px + _frame.ScreenBounds.X}, {py + _frame.ScreenBounds.Y}";
        _loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var avoid = new Rect(MagnifierAvoidanceArea.X * scaleX, MagnifierAvoidanceArea.Y * scaleY,
            MagnifierAvoidanceArea.Width * scaleX, MagnifierAvoidanceArea.Height * scaleY);
        PositionLoupe(new Point(_cursor.X * scaleX, _cursor.Y * scaleY), avoid);
    }

    private void PositionHandles(Rect rect, bool visible)
    {
        for (int i = 0; i < _handles.Length; i++)
        {
            var handle = _handles[i];
            handle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
                continue;
            int cell = i < 4 ? i : i + 1; // All points of a 3 × 3 grid except the center.
            Canvas.SetLeft(handle, rect.Left + cell % 3 * rect.Width / 2 - 3);
            Canvas.SetTop(handle, rect.Top + cell / 3 * rect.Height / 2 - 3);
        }
    }

    private void AnimateSelectionHandles()
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.22, TimeSpan.FromMilliseconds(155), new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(280), new SineEase { EasingMode = EasingMode.EaseOut }));
        foreach (var handle in _handles)
        {
            var scale = (ScaleTransform)handle.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }

    private void ChangeMagnification(int direction)
    {
        _zoomIndex = Math.Clamp(_zoomIndex + direction, 0, Magnifications.Length - 1);
        _zoomText.Text = $"{Magnifications[_zoomIndex]}×";
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, TimeSpan.Zero));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(740)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(900)));
        _zoomBadge.BeginAnimation(OpacityProperty, animation);
        Pulse(_lensHolder);
        InvalidateVisual();
    }

    private static void Pulse(FrameworkElement element)
    {
        var scale = new ScaleTransform();
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.975, TimeSpan.Zero));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.035, TimeSpan.FromMilliseconds(100)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(240)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void PositionLoupe(Point cursor, Rect avoid)
    {
        const double gap = 22;
        var size = _loupe.DesiredSize;
        var maxLeft = Math.Max(0, ActualWidth - size.Width - 6);
        var maxTop = Math.Max(0, ActualHeight - size.Height - 6);
        var preferred = new Point(
            cursor.X + gap + size.Width <= ActualWidth ? cursor.X + gap : cursor.X - gap - size.Width,
            cursor.Y + gap + size.Height <= ActualHeight ? cursor.Y + gap : cursor.Y - gap - size.Height);
        Point Bound(double x, double y) => new(Math.Clamp(x, 0, maxLeft), Math.Clamp(y, 0, maxTop));
        _loupeTarget = Bound(preferred.X, preferred.Y);
        if (!MagnifierAvoidanceArea.IsEmpty && new Rect(_loupeTarget, size).IntersectsWith(avoid))
        {
            foreach (var candidate in new[]
                {
                    Bound(preferred.X, avoid.Bottom + 6), Bound(avoid.Right + 6, preferred.Y),
                    Bound(avoid.Left - size.Width - 6, preferred.Y), Bound(preferred.X, avoid.Top - size.Height - 6)
                })
            {
                if (new Rect(candidate, size).IntersectsWith(avoid))
                    continue;
                _loupeTarget = candidate;
                break;
            }
        }
        if (!_placedLoupe)
        {
            _loupeOffset.X = _loupeTarget.X;
            _loupeOffset.Y = _loupeTarget.Y;
            _placedLoupe = true;
        }
        else if (!_followingLoupe && IsLoaded && (_loupeTarget - new Point(_loupeOffset.X, _loupeOffset.Y)).LengthSquared > 0.01)
        {
            _followingLoupe = true;
            CompositionTarget.Rendering += FollowLoupe;
        }
    }

    private void FollowLoupe(object? sender, EventArgs e)
    {
        var delta = _loupeTarget - new Point(_loupeOffset.X, _loupeOffset.Y);
        if (Math.Abs(delta.X) < 0.25 && Math.Abs(delta.Y) < 0.25)
        {
            _loupeOffset.X = _loupeTarget.X;
            _loupeOffset.Y = _loupeTarget.Y;
            StopFollowingLoupe();
            return;
        }
        _loupeOffset.X += delta.X * 0.30;
        _loupeOffset.Y += delta.Y * 0.30;
    }

    private void StopFollowingLoupe()
    {
        if (!_followingLoupe)
            return;
        CompositionTarget.Rendering -= FollowLoupe;
        _followingLoupe = false;
    }

    private static Pen FrozenPen(byte alpha, byte r, byte g, byte b, double width)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, r, g, b)), width);
        pen.Freeze();
        return pen;
    }

    private sealed class MagnifierLens : FrameworkElement
    {
        private const double Radius = 58;
        private static readonly Point Center = new(Radius, Radius);
        private static readonly Pen GridLine = FrozenPen(0x21, 255, 255, 255, 1);
        private static readonly Pen OuterRim = FrozenPen(0x24, 15, 23, 42, 1);
        private static readonly Pen InnerRim = FrozenPen(0xEB, 255, 255, 255, 2.5);
        private static readonly Pen FocusShadow = FrozenPen(180, 0, 0, 0, 2.5);
        private static readonly Pen FocusLine = FrozenPen(245, 255, 255, 255, 1);
        private readonly Pen _innerShade = new(new LinearGradientBrush(
            Color.FromArgb(90, 255, 255, 255), Color.FromArgb(68, 0, 0, 0), 90), 2);
        private WriteableBitmap? _sample;
        private byte[] _pixels = [];
        private int _px = -1, _py = -1, _zoom;

        public void UpdateSample(PixelBuffer source, int px, int py, int zoom)
        {
            if (_px == px && _py == py && _zoom == zoom)
                return;
            _px = px;
            _py = py;
            _zoom = zoom;
            int half = (int)Math.Ceiling(Radius / zoom);
            int width = 2 * half + 1;
            if (_sample?.PixelWidth != width)
            {
                _sample = new WriteableBitmap(width, width, 96, 96, PixelFormats.Bgra32, null);
                _pixels = new byte[width * width * 4];
            }
            // Keep the sampled pixel in the exact center even at a virtual desktop edge.
            for (int row = 0; row < width; row++)
            {
                int sourceRow = Math.Clamp(py + row - half, 0, source.Height - 1) * source.Stride;
                for (int col = 0; col < width; col++)
                {
                    int offset = sourceRow + 4 * Math.Clamp(px + col - half, 0, source.Width - 1);
                    int output = 4 * (row * width + col);
                    _pixels[output] = source.Pixels[offset];
                    _pixels[output + 1] = source.Pixels[offset + 1];
                    _pixels[output + 2] = source.Pixels[offset + 2];
                    _pixels[output + 3] = 255;
                }
            }
            _sample.WritePixels(new Int32Rect(0, 0, width, width), _pixels, width * 4, 0);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_sample is null)
                return;
            double extent = _sample.PixelWidth * _zoom;
            double first = Radius - extent / 2;
            dc.PushClip(new EllipseGeometry(Center, Radius, Radius));
            dc.DrawImage(_sample, new Rect(first, first, extent, extent));
            if (_zoom >= 6)
            {
                for (double line = first; line < Radius * 2; line += _zoom)
                {
                    dc.DrawLine(GridLine, new Point(line, 0), new Point(line, Radius * 2));
                    dc.DrawLine(GridLine, new Point(0, line), new Point(Radius * 2, line));
                }
            }
            var cell = new Rect(Radius - _zoom / 2.0, Radius - _zoom / 2.0, _zoom, _zoom);
            dc.DrawRectangle(null, FocusShadow, cell);
            dc.DrawRectangle(null, FocusLine, cell);
            dc.DrawEllipse(null, _innerShade, Center, Radius - 4, Radius - 4);
            dc.DrawEllipse(null, InnerRim, Center, Radius - 2, Radius - 2);
            dc.DrawEllipse(null, OuterRim, Center, Radius - 0.5, Radius - 0.5);
            dc.Pop();
        }
    }
}
