using System.ComponentModel;
using System.Runtime.InteropServices;
using Screen.Core;

namespace Screen.App;

internal static class RecordingStorage
{
    public static string? ValidateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        try
        {
            if (!Path.IsPathFullyQualified(directory))
                return "录屏临时目录需要使用绝对路径。";
            var full = Path.GetFullPath(directory);
            if (File.Exists(full))
                return "录屏临时目录不能是文件。";
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return "录屏临时目录无效：" + exception.Message;
        }
    }

    public static string GetRoot(RecordingProfile profile)
    {
        if (ValidateDirectory(profile.TempDirectory) is { } error)
            throw new IOException(error);
        var directory = string.IsNullOrWhiteSpace(profile.TempDirectory) ? Path.GetTempPath() : profile.TempDirectory;
        return Path.Combine(Path.GetFullPath(directory), "Screen.Recording");
    }

    public static string CreateSession(RecordingProfile profile)
    {
        var root = GetRoot(profile);
        Directory.CreateDirectory(root);
        EnsureAvailableSpace(root, 512L * 1024 * 1024);
        // Check write permissions before constructing video/audio capture devices.
        var probe = Path.Combine(root, ".write-check-" + Guid.NewGuid().ToString("N"));
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            stream.WriteByte(0);
        }
        var session = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        return session;
    }

    public static void EnsureAvailableSpace(string directory, long minimumBytes = 64L * 1024 * 1024)
    {
        var path = Path.GetFullPath(directory);
        // Query the selected directory so SMB quotas and mounted volumes are respected.
        // GetDiskFreeSpaceEx requires a trailing separator for UNC paths.
        if (!Path.EndsInDirectorySeparator(path))
            path += Path.DirectorySeparatorChar;
        if (!NativeMethods.GetDiskFreeSpaceEx(path, out var available, 0, 0))
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new IOException($"无法查询录屏临时目录的可用空间：{directory}。{error.Message}", error);
        }
        if (minimumBytes > 0 && available < (ulong)minimumBytes)
        {
            throw new IOException($"录屏临时目录空间不足：{directory}，剩余 {available / (1024 * 1024)} MB，至少需要 {minimumBytes / (1024 * 1024)} MB。");
        }
    }
}
