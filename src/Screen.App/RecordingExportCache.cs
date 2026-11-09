namespace Screen.App;

internal readonly record struct RecordingTrimRange(double Start, double End);

/// <summary>One export per immutable range; changing the UI cannot relabel an in-flight result.</summary>
internal sealed class RecordingExportCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<RecordingTrimRange, Lazy<Task<string>>> _entries = [];
    private readonly HashSet<string> _files = [];
    private readonly CancellationTokenSource _lifetime = new();
    private int _activeFactories;
    private bool _disposed;
    private bool _cancellationComplete;

    public async Task<string> GetAsync(RecordingTrimRange range, Func<RecordingTrimRange, CancellationToken, Task<string>> create)
    {
        Lazy<Task<string>> entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(range, out entry!))
            {
                entry = new Lazy<Task<string>>(() => CreateAsync(range, create));
                _entries.Add(range, entry);
            }
        }
        try
        {
            return await entry.Value.ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(range, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(range);
                }
            }
            throw;
        }
    }

    private async Task<string> CreateAsync(RecordingTrimRange range, Func<RecordingTrimRange, CancellationToken, Task<string>> create)
    {
        CancellationToken token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token = _lifetime.Token;
            _activeFactories++;
        }
        try
        {
            var path = await create(range, token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _files.Add(path);
                    return path;
                }
            }
            RecordingFileStore.DeleteFileQuietly(path);
            throw new OperationCanceledException("预览窗口已关闭。");
        }
        finally
        {
            lock (_gate)
            {
                _activeFactories--;
                if (_cancellationComplete && _activeFactories == 0)
                {
                    _lifetime.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        string[] files;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            files = _files.ToArray();
            _files.Clear();
            _entries.Clear();
        }
        _lifetime.Cancel();
        lock (_gate)
        {
            _cancellationComplete = true;
            if (_activeFactories == 0)
            {
                _lifetime.Dispose();
            }
        }
        foreach (var path in files)
        {
            RecordingFileStore.DeleteFileQuietly(path);
        }
    }
}
