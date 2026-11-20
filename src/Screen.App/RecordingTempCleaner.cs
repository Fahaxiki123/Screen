namespace Screen.App;

/// <summary>
/// Cleans abandoned export scratch files. Nonempty recording sessions may be
/// the only surviving copy of a failed recording and are never aged out.
/// </summary>
internal static class RecordingTempCleaner
{
    private static readonly TimeSpan _emptyDirectoryRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan _scratchRetention = TimeSpan.FromHours(1);

    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "Screen.Recording");

    public static void PreserveRecovery(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "RECOVERY.txt"),
                "Screen 录屏恢复素材。Screen 不会自动删除此目录。请恢复视频和音频后自行清理。\n" + DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SimpleLogger.LogWarning($"无法写入恢复说明：{exception.Message}");
        }
    }

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
            // GIF clipboard deliveries live alongside the original and must never be aged out.
            // Match only our uniquely named transient palette exports after an interrupted preview.
            scratch |= name.Length == "gif-preview-".Length + 32 + ".gif".Length
                       && name.StartsWith("gif-preview-", StringComparison.OrdinalIgnoreCase)
                       && name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                       && Guid.TryParseExact(name.AsSpan("gif-preview-".Length, 32), "N", out _);
            if (scratch && utcNow - File.GetLastWriteTimeUtc(file) > _scratchRetention && TryDelete(() => File.Delete(file)))
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
            // Do not interpret an inaccessible or partially enumerated directory
            // as empty, and never traverse/delete nested links or media folders.
            if (utcNow - info.LastWriteTimeUtc > _emptyDirectoryRetention
                && TryDelete(() => Directory.Delete(directory, recursive: false)))
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
