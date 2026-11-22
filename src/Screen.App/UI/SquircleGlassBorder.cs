using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Screen.App;

[ContentProperty(nameof(Child))]
public sealed class SquircleGlassBorder : Grid
{
    // Ambient shadow has an 18 DIP blur and a 5 DIP downward offset.
    internal static Thickness FloatingShadowMargin { get; } = new(18, 13, 18, 23);

    public static readonly DependencyProperty CornerRadiusRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadiusRadius), typeof(double), typeof(SquircleGlassBorder),
        new PropertyMetadata(10.5, GeometryChanged), value => value is double d && d >= 0 && double.IsFinite(d));
    public static readonly DependencyProperty SmoothingProperty = DependencyProperty.Register(
        nameof(Smoothing), typeof(double), typeof(SquircleGlassBorder),
        new PropertyMetadata(0.60, GeometryChanged), value => value is double d && d >= 0 && d <= 1);
    public static readonly DependencyProperty GlassBackgroundProperty = DependencyProperty.Register(
        nameof(GlassBackground), typeof(Brush), typeof(SquircleGlassBorder),
        new PropertyMetadata(null, (d, e) => ((SquircleGlassBorder)d).UpdateBackground((Brush?)e.NewValue)));
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(SquircleGlassBorder),
        new PropertyMetadata(new Thickness(), (d, _) => ((SquircleGlassBorder)d).UpdatePadding()));
    public static readonly DependencyProperty ChildProperty = DependencyProperty.Register(
        nameof(Child), typeof(UIElement), typeof(SquircleGlassBorder),
        new PropertyMetadata(null, (d, e) => ((SquircleGlassBorder)d)._content.Child = (UIElement?)e.NewValue));
    public static readonly DependencyProperty ShowRimProperty = DependencyProperty.Register(
        nameof(ShowRim), typeof(bool), typeof(SquircleGlassBorder),
        new PropertyMetadata(true, RimChanged));
    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(SquircleGlassBorder), new PropertyMetadata(null, RimChanged));
    public static readonly DependencyProperty OutlineThicknessProperty = DependencyProperty.Register(
        nameof(OutlineThickness), typeof(double), typeof(SquircleGlassBorder), new PropertyMetadata(0d, RimChanged),
        value => value is double width && double.IsFinite(width) && width >= 0);
    public static readonly DependencyProperty ShadowLevelProperty = DependencyProperty.Register(
        nameof(ShadowLevel), typeof(int), typeof(SquircleGlassBorder),
        new PropertyMetadata(2, (d, _) => ((SquircleGlassBorder)d).UpdateShadows()), value => value is int level && level >= 0 && level <= 3);
    public static readonly DependencyProperty CacheShadowsProperty = DependencyProperty.Register(
        nameof(CacheShadows), typeof(bool), typeof(SquircleGlassBorder),
        new PropertyMetadata(false, (d, _) => ((SquircleGlassBorder)d).UpdateShadowCache()));

    private static readonly DropShadowEffect ContactShadow = Shadow(2, 1);
    private static readonly DropShadowEffect SoftShadow = Shadow(6, 2);
    private static readonly DropShadowEffect AmbientShadow = Shadow(18, 5);
    private readonly Canvas _shadows = new() { IsHitTestVisible = false };
    private readonly Border _content = new();
    private readonly Canvas _rim = new() { IsHitTestVisible = false };
    private readonly Path _highlight = new() { StrokeThickness = 2.5 };
    private readonly Path _edge = new();
    private StreamGeometry? _outline;
    private double? _topPointerX;

    public SquircleGlassBorder()
    {
        UpdateBackground(GlassBackground);
        _highlight.SetResourceReference(Shape.StrokeProperty, "Brush.GlassRim");
        UpdateRim();
        _rim.Children.Add(_highlight);
        _rim.Children.Add(_edge);
        Children.Add(_shadows);
        Children.Add(_content);
        Children.Add(_rim);
        UpdateShadows();
    }

    public double CornerRadiusRadius { get => (double)GetValue(CornerRadiusRadiusProperty); set => SetValue(CornerRadiusRadiusProperty, value); }
    public double Smoothing { get => (double)GetValue(SmoothingProperty); set => SetValue(SmoothingProperty, value); }
    public Brush? GlassBackground { get => (Brush?)GetValue(GlassBackgroundProperty); set => SetValue(GlassBackgroundProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public UIElement? Child { get => (UIElement?)GetValue(ChildProperty); set => SetValue(ChildProperty, value); }
    public bool ShowRim { get => (bool)GetValue(ShowRimProperty); set => SetValue(ShowRimProperty, value); }
    public Brush? OutlineBrush { get => (Brush?)GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }
    public double OutlineThickness { get => (double)GetValue(OutlineThicknessProperty); set => SetValue(OutlineThicknessProperty, value); }
    public int ShadowLevel { get => (int)GetValue(ShadowLevelProperty); set => SetValue(ShadowLevelProperty, value); }
    public bool CacheShadows { get => (bool)GetValue(CacheShadowsProperty); set => SetValue(CacheShadowsProperty, value); }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_shadows.CacheMode is BitmapCache cache)
            cache.RenderAtScale = newDpi.DpiScaleX;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateGeometry();
    }

    private static void GeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SquircleGlassBorder)d).UpdateGeometry();

    private static void RimChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SquircleGlassBorder)d).UpdateRim();

    private void UpdateRim()
    {
        _highlight.Visibility = ShowRim ? Visibility.Visible : Visibility.Collapsed;
        if (ShowRim)
        {
            _edge.SetResourceReference(Shape.StrokeProperty, "Brush.OverlayBorder");
            _edge.StrokeThickness = 1.2;
        }
        else
        {
            _edge.Stroke = OutlineBrush;
            // The shared clip keeps the inner half of the centered stroke.
            _edge.StrokeThickness = 2 * OutlineThickness;
        }
        _rim.Visibility = ShowRim || (OutlineBrush is not null && OutlineThickness > 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBackground(Brush? brush)
    {
        if (brush is null)
            _content.SetResourceReference(Border.BackgroundProperty, "Brush.ChromeGradient");
        else
            _content.Background = brush;
    }

    internal void SetTopPointer(double centerX)
    {
        if (!double.IsFinite(centerX) || (_topPointerX is double previous && Math.Abs(centerX - previous) < 0.01))
            return;

        bool first = !_topPointerX.HasValue;
        _topPointerX = centerX;
        if (first)
        {
            // Paint into the existing row gap. Keep the button layout and the body's measured size unchanged.
            _content.Margin = _rim.Margin = _shadows.Margin = new Thickness(0, -SquircleGeometry.TopPointerHeight, 0, 0);
            UpdatePadding();
        }
        UpdateGeometry();
    }

    private void UpdatePadding()
    {
        var padding = Padding;
        if (_topPointerX.HasValue)
            padding.Top += SquircleGeometry.TopPointerHeight;
        _content.Padding = padding;
    }

    private void UpdateGeometry()
    {
        _outline = SquircleGeometry.Create(ActualWidth, ActualHeight, CornerRadiusRadius, Smoothing, _topPointerX);
        _content.Clip = _rim.Clip = _outline;
        _highlight.Data = _edge.Data = _outline;
        foreach (Path shadow in _shadows.Children)
            shadow.Data = _outline;
    }

    private void UpdateShadows()
    {
        _shadows.Children.Clear();
        if (ShadowLevel > 0)
            AddShadow(SoftShadow, 0.11);
        if (ShadowLevel >= 2)
            AddShadow(AmbientShadow, 0.12);
        if (ShadowLevel >= 3)
            AddShadow(ContactShadow, 0.06);
        UpdateShadowCache();
    }

    private void UpdateShadowCache()
    {
        // Fixed-size cards can reuse their blurred outline during scrolling and
        // animation. Text, interactive content and the optical rim stay live.
        if (CacheShadows && ShadowLevel > 0)
            _shadows.CacheMode ??= new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        else
            _shadows.CacheMode = null;
    }

    private void AddShadow(DropShadowEffect effect, double opacity) => _shadows.Children.Add(
        new Path { Data = _outline, Fill = Brushes.White, Effect = effect, Opacity = opacity });

    private static DropShadowEffect Shadow(double blur, double depth)
    {
        var shadow = new DropShadowEffect
        {
            BlurRadius = blur,
            ShadowDepth = depth,
            Direction = 270,
            Color = Color.FromRgb(15, 23, 42),
            Opacity = 1
        };
        shadow.Freeze();
        return shadow;
    }
}
