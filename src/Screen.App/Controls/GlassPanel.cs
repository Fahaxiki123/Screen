using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Screen.App;

public enum GlassVariant { Glass, Solid, DarkSolid }

/// <summary>A piece of chrome. Only its background layers sample the captured image.</summary>
public sealed class GlassPanel : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner(typeof(GlassPanel));
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(nameof(Variant), typeof(GlassVariant), typeof(GlassPanel));
    public static readonly DependencyProperty BackdropSourceProperty = DependencyProperty.Register(nameof(BackdropSource), typeof(ImageSource), typeof(GlassPanel));
    public static readonly DependencyProperty BackdropViewboxProperty = DependencyProperty.Register(nameof(BackdropViewbox), typeof(Rect), typeof(GlassPanel), new PropertyMetadata(new Rect(0, 0, 1, 1)));
    public static readonly DependencyProperty IsDimmedProperty = DependencyProperty.Register(nameof(IsDimmed), typeof(bool), typeof(GlassPanel));

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public GlassVariant Variant { get => (GlassVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public ImageSource? BackdropSource { get => (ImageSource?)GetValue(BackdropSourceProperty); set => SetValue(BackdropSourceProperty, value); }
    public Rect BackdropViewbox { get => (Rect)GetValue(BackdropViewboxProperty); set => SetValue(BackdropViewboxProperty, value); }
    public bool IsDimmed { get => (bool)GetValue(IsDimmedProperty); set => SetValue(IsDimmedProperty, value); }
}

public static class HudActiveButton
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(HudActiveButton));
    public static bool GetIsActive(DependencyObject target) => (bool)target.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject target, bool value) => target.SetValue(IsActiveProperty, value);
}
