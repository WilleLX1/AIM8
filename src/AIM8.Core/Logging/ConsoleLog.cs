namespace AIM8.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A single line in the dashboard console.</summary>
/// <param name="Timestamp">When the line was produced (local time).</param>
/// <param name="Level">Severity, used for colouring.</param>
/// <param name="Source">Short origin tag, e.g. "github", "device", "build".</param>
/// <param name="Message">The text itself.</param>
public readonly record struct LogLine(DateTimeOffset Timestamp, LogLevel Level, string Source, string Message)
{
    /// <summary>Renders as "16:51:22 [github] Build requested".</summary>
    public override string ToString() => $"{Timestamp:HH:mm:ss} [{Source}] {Message}";
}

/// <summary>
/// Thread-safe bounded log that every service writes into and the console pane renders.
/// Keeps the most recent <see cref="Capacity"/> lines; older lines are dropped.
/// </summary>
public sealed class ConsoleLog
{
    private readonly Queue<LogLine> _lines = new();
    private readonly object _gate = new();

    public ConsoleLog(int capacity = 5000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Raised on the writing thread for every appended line.</summary>
    public event Action<LogLine>? LineAppended;

    public void Write(LogLevel level, string source, string message)
    {
        var line = new LogLine(DateTimeOffset.Now, level, source, message);
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity) _lines.Dequeue();
        }

        LineAppended?.Invoke(line);
    }

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);

    public void Info(string source, string message) => Write(LogLevel.Info, source, message);

    public void Success(string source, string message) => Write(LogLevel.Success, source, message);

    public void Warn(string source, string message) => Write(LogLevel.Warning, source, message);

    public void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public void Error(string source, string message, Exception ex) =>
        Write(LogLevel.Error, source, $"{message}: {ex.Message}");

    /// <summary>Snapshot of the retained lines, oldest first.</summary>
    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }
}
