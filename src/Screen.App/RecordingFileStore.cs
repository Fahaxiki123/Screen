namespace Screen.App;

internal sealed record RecordingSaveResult(string? SavedPath, string? Warning, string? Error, string? RecoveryDirectory)
{
    public bool Saved => SavedPath is not null;
}

/// <summary>Publishes completed recordings without consuming the recovery copy until commit succeeds.</summary>
internal static class RecordingFileStore
{
    public static RecordingSaveResult Save(string source, string destination, string recoveryDirectory, string? warning = null, bool preserveRecovery = false,
        CancellationToken cancellationToken = default)
    {
        string savedPath;
        try
        {
            savedPath = StageAndPublish(source, destination, keepBoth: true, cancellationToken);
        }
        catch (Exception exception)
        {
            RecordingTempCleaner.PreserveRecovery(recoveryDirectory);
            return new RecordingSaveResult(null, warning,
                exception is OperationCanceledException ? "已取消保存，录制素材已保留。" : exception.Message, recoveryDirectory);
        }

        if (preserveRecovery)
        {
            RecordingTempCleaner.PreserveRecovery(recoveryDirectory);
            return new RecordingSaveResult(savedPath, warning, null, recoveryDirectory);
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
        return new RecordingSaveResult(savedPath, warning, null, null);
    }

    public static void CopyAtomically(string source, string destination, CancellationToken cancellationToken = default) =>
        StageAndPublish(source, destination, keepBoth: false, cancellationToken);

    private static string StageAndPublish(string source, string destination, bool keepBoth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(destination);
        if (!keepBoth && string.Equals(Path.GetFullPath(source), target, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("导出文件不存在。", source);
            }
            return target;
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
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (keepBoth)
                return PublishWithAvailableName(staging, target, cancellationToken);
            File.Move(staging, target, overwrite: true);
            return target;
        }
        finally
        {
            DeleteFileQuietly(staging);
        }
    }

    private static string PublishWithAvailableName(string staging, string target, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        for (var suffix = 0; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = suffix == 0 ? target : Path.Combine(directory, $"{stem} ({suffix}){extension}");
            try
            {
                // Only the no-overwrite rename decides the winner, including across processes on a share.
                File.Move(staging, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 80 or 183)
            {
                // Win32 ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS. Reuse the completed staging file.
            }
        }
    }

    public static string CreateClipboardCopy(string source, string recordingPath)
    {
        // Clipboard file drops contain paths, not file contents. Treat the
        // trimmed copy as a delivered recording, with the same lifetime as an
        // ordinary saved video, independent of the preview window/cache.
        var destination = ClipboardCopyPath(recordingPath);
        CopyAtomically(source, destination);
        return destination;
    }

    public static Task CopyAtomicallyAsync(string source, string destination,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(destination);
        if (string.Equals(Path.GetFullPath(source), target, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("导出文件不存在。", source);
            progress?.Report(1);
            return;
        }
        var staging = Path.Combine(Path.GetDirectoryName(target)!, $".screen-export-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                if (input.Length == 0)
                    throw new IOException("录屏文件为空，已保留原始素材。");
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                var reported = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    var percent = (int)(copied * 100 / input.Length);
                    if (percent != reported)
                    {
                        reported = percent;
                        progress?.Report(copied / (double)input.Length);
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite: true);
        }
        finally
        {
            DeleteFileQuietly(staging);
        }
    }, cancellationToken);

    public static string ClipboardCopyPath(string recordingPath, Screen.Core.RecordingFormat format = Screen.Core.RecordingFormat.Mp4)
    {
        var suffix = format == Screen.Core.RecordingFormat.Gif ? $"动图-{Guid.NewGuid():N}.gif" : $"裁切-{Guid.NewGuid():N}.mp4";
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordingPath))!, $"{Path.GetFileNameWithoutExtension(recordingPath)}-{suffix}");
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
