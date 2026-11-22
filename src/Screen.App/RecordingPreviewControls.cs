using System.Windows.Media;

namespace Screen.App;

/// <summary>Shared recording chrome tokens, with frozen fallbacks for standalone control previews.</summary>
internal static class RecordingTheme
{
    internal static Brush Brush(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush resource)
        {
            return resource;
        }

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        brush.Freeze();
        return brush;
    }
}
