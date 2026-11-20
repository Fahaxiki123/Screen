using System.Windows;
using System.Windows.Media;

namespace Screen.App;

internal static class GlassTheme
{
    // Detached renderers and STA component tests have no Application. Load the same
    // compiled dictionary on their dispatcher rather than duplicating fallback colors.
    [ThreadStatic] private static ResourceDictionary? _tokens;

    public static T Get<T>(string key)
    {
        if (Application.Current?.TryFindResource(key) is T resource)
            return resource;
        _tokens ??= new ResourceDictionary { Source = new Uri("/Screen;component/Themes/Tokens.xaml", UriKind.Relative) };
        return _tokens[key] is T value ? value : throw new ResourceReferenceKeyNotFoundException($"Missing Screen resource: {key}", key);
    }

    public static Brush Brush(string key) => Get<Brush>(key);
    public static CornerRadius Radius(string key) => Get<CornerRadius>(key);
}
