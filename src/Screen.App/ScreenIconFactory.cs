using System.Windows;
using DrawingIcon = System.Drawing.Icon;

namespace Screen.App;

internal static class ScreenIconFactory
{
    public static DrawingIcon CreateTrayIcon()
    {
        var resource = Application.GetResourceStream(
            new Uri("/Screen;component/Assets/Screen.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("The embedded Screen icon is missing.");
        using var stream = resource.Stream;
        using var icon = new DrawingIcon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        // NotifyIcon owns the returned clone; neither the resource stream nor this
        // temporary Icon has to stay alive after this method returns.
        return (DrawingIcon)icon.Clone();
    }
}
