using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Screen.App;

/// <summary>Shares one layout origin between the controls and their independently moving indicator.</summary>
internal sealed class SpringTrack : Grid
{
    private readonly LiquidSpringTracker _tracker;

    public SpringTrack(Panel items, double radius, Axis axis = Axis.X, bool colorRing = false, Brush? fill = null,
        int shadowLevel = 1, bool cacheShadows = false)
    {
        FrameworkElement indicator;
        if (colorRing)
        {
            indicator = new Ellipse
            {
                StrokeThickness = 2,
                Effect = new DropShadowEffect { Color = Color.FromRgb(15, 23, 42), Opacity = 0.14, BlurRadius = 5, ShadowDepth = 1.5, Direction = 270 }
            };
            indicator.SetResourceReference(Shape.StrokeProperty, "Brush.TextPrimary");
            indicator.SetResourceReference(Shape.FillProperty, "Brush.LiquidSlider");
        }
        else
        {
            var lens = new SquircleGlassBorder
            {
                CornerRadiusRadius = radius,
                ShowRim = false,
                ShadowLevel = shadowLevel,
                CacheShadows = cacheShadows
            };
            if (fill is null)
            {
                lens.SetResourceReference(SquircleGlassBorder.GlassBackgroundProperty, "Brush.LiquidSlider");
            }
            else
            {
                lens.GlassBackground = fill;
            }
            indicator = lens;
        }

        var layer = new Canvas { IsHitTestVisible = false };
        layer.Children.Add(indicator);
        Children.Add(layer);
        Children.Add(items);
        _tracker = new LiquidSpringTracker(this, indicator, axis, colorRing);
    }

    public void Select(FrameworkElement? target, bool instant = false) => _tracker.MoveTo(target, instant);
}
