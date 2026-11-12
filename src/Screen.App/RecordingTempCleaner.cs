namespace Screen.App;

/// <summary>
/// Housekeeping for %TEMP%\Screen.Recording. Recovery folders are kept for
/// <see cref="RecoveryRetention"/> so a failed save can still be rescued from
/// the path shown to the user; export scratch files and the retired preview
/// log are always orphans at startup (single instance, nothing open yet).
/// </summary>
internal static class RecordingTempCleaner
{
    public static readonly TimeSpan RecoveryRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan ScratchRetention = TimeSpan.FromHours(1);

    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "Screen.Recording");

    /// <returns>The number of files and folders removed.</returns>
    public static int Clean(string root, DateTime utcNow)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }
        var removed = 0;
        foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(root)))
        {
            var name = Path.GetFileName(file);
            var scratch = name.Equals("preview-audio.log", StringComparison.OrdinalIgnoreCase)
                          || (name.StartsWith("trim-", StringComparison.OrdinalIgnoreCase)
                              && name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            if (scratch && utcNow - File.GetLastWriteTimeUtc(file) > ScratchRetention && TryDelete(() => File.Delete(file)))
            {
                removed++;
            }
        }
        foreach (var directory in SafeEnumerate(() => Directory.EnumerateDirectories(root)))
        {
            // Only folders this app creates: 32 hex digits (Guid "N").
            var name = Path.GetFileName(directory);
            if (name.Length != 32 || !name.All(Uri.IsHexDigit))
            {
                continue;
            }
            var info = new DirectoryInfo(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }
            var lastWrite = SafeEnumerate(() => info.EnumerateFiles("*", SearchOption.AllDirectories))
                .Select(file => file.LastWriteTimeUtc)
                .DefaultIfEmpty(info.LastWriteTimeUtc)
                .Max();
            if (utcNow - lastWrite > RecoveryRetention && TryDelete(() => Directory.Delete(directory, recursive: true)))
            {
                removed++;
            }
        }
        return removed;
    }

    private static IEnumerable<T> SafeEnumerate<T>(Func<IEnumerable<T>> source)
    {
        try
        {
            return source().ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDelete(Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
