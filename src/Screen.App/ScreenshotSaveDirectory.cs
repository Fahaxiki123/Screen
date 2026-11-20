using Screen.Core;

namespace Screen.App;

/// <summary>Remembering a location is optional; it is not part of committing the image.</summary>
internal static class ScreenshotSaveDirectory
{
    public static string GetInitialDirectory(JsonSettingsStore settings)
    {
        try
        {
            var path = settings.Load().SaveDirectory;
            if (Directory.Exists(path))
                return path;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Could not read the last screenshot directory", exception);
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    public static bool TryRemember(JsonSettingsStore settings, string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            settings.Update(latest => latest.SaveDirectory = directory);
            return true;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Screenshot saved, but its directory could not be remembered", exception);
            return false;
        }
    }
}
