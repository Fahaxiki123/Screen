namespace Screen.App;

internal sealed record RecordingSaveResult(bool Saved, string? Warning, string? Error, string? RecoveryDirectory);

/// <summary>Publishes completed recordings without consuming the recovery copy until commit succeeds.</summary>
internal static class RecordingFileStore
{
    public static RecordingSaveResult Save(string source, string destination, string recoveryDirectory, string? warning = null, bool preserveRecovery = false)
    {
        try
        {
            CopyAtomically(source, destination);
        }
        catch (Exception exception)
        {
            return new RecordingSaveResult(false, warning, exception.Message, recoveryDirectory);
        }

        if (preserveRecovery)
        {
            return new RecordingSaveResult(true, warning, null, recoveryDirectory);
        }
        try
        {
            Directory.Delete(recoveryDirectory, recursive: true);
        }
        catch (IOException)
        {
            // The destination is committed; an open audio device can delay scratch cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return new RecordingSaveResult(true, warning, null, null);
    }

    public static void CopyAtomically(string source, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (string.Equals(Path.GetFullPath(source), target, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("导出文件不存在。", source);
            }
            return;
        }
        var directory = Path.GetDirectoryName(target)!;
        var staging = Path.Combine(directory, $".screen-export-{Guid.NewGuid():N}.tmp");
        try
        {
            // Copy to the destination volume first. A failed copy or rename never
            // deletes the source, and never truncates an existing destination.
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length == 0)
                {
                    throw new IOException("录屏文件为空，已保留原始素材。");
                }
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, target, overwrite: true);
        }
        finally
        {
            DeleteFileQuietly(staging);
        }
    }

    public static string CreateClipboardCopy(string source, string recordingPath)
    {
        // Clipboard file drops contain paths, not file contents. Treat the
        // trimmed copy as a delivered recording, with the same lifetime as an
        // ordinary saved video, independent of the preview window/cache.
        var destination = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(recordingPath))!,
            $"{Path.GetFileNameWithoutExtension(recordingPath)}-裁切-{Guid.NewGuid():N}.mp4");
        CopyAtomically(source, destination);
        return destination;
    }

    internal static void DeleteFileQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
