using System.Diagnostics;
using System.Text;

namespace Screen.App;

/// <summary>
/// Drains a child process's redirected stderr asynchronously (an unread pipe
/// eventually blocks the child) and keeps the last few lines for diagnostics.
/// </summary>
internal sealed class ProcessErrorTail
{
    private const int MaxLines = 12;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();

    private ProcessErrorTail()
    {
    }

    /// <summary>
    /// Starts draining <paramref name="process"/>'s stderr. The process must
    /// have been started with RedirectStandardError; otherwise nothing is kept.
    /// </summary>
    public static ProcessErrorTail Attach(Process process)
    {
        var tail = new ProcessErrorTail();
        if (!process.StartInfo.RedirectStandardError)
        {
            return tail;
        }
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }
            lock (tail._gate)
            {
                tail._lines.Enqueue(e.Data);
                while (tail._lines.Count > MaxLines)
                {
                    tail._lines.Dequeue();
                }
            }
        };
        process.BeginErrorReadLine();
        return tail;
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                var builder = new StringBuilder();
                foreach (var line in _lines)
                {
                    builder.AppendLine(line);
                }
                return builder.ToString().Trim();
            }
        }
    }
}
