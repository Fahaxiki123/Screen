using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Screen.App;

/// <summary>Only animate live changes. A checked switch already has a correct static template.</summary>
public static class SwitchMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SwitchMotion), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool enabled) => target.SetValue(EnabledProperty, enabled);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ToggleButton button)
            return;
        if (e.NewValue is true)
        {
            button.Checked += Animate;
            button.Unchecked += Animate;
        }
        else
        {
            button.Checked -= Animate;
            button.Unchecked -= Animate;
        }
    }

    private static void Animate(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        if (button != e.OriginalSource || !button.IsLoaded || !SystemParameters.ClientAreaAnimation)
            return;
        var template = button.Template;
        if (template?.FindName("ThumbMove", button) is TranslateTransform thumb &&
            template.FindName("ColorTrack", button) is Border track)
        {
            var on = button.IsChecked == true;
            thumb.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(on ? 0 : 16, on ? 16 : 0, 350, Motion.Spring()));
            track.BeginAnimation(UIElement.OpacityProperty, Motion.Tween(on ? 0 : 1, on ? 1 : 0, 200));
        }
    }
}
