using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using AIM8.Core.Logging;

namespace AIM8.App.ViewModels;

public sealed class ConsoleLineViewModel
{
    public ConsoleLineViewModel(LogLine line)
    {
        Timestamp = line.Timestamp.ToString("HH:mm:ss");
        Source = line.Source;
        Message = line.Message;
        Brush = BrushFor(line.Level);
    }

    public string Timestamp { get; }

    public string Source { get; }

    public string Message { get; }

    public Brush Brush { get; }

    /// <summary>The line as it is copied to the clipboard.</summary>
    public string Text => $"{Timestamp} {Source,-8} {Message}";

    private static Brush BrushFor(LogLevel level) => level switch
    {
        LogLevel.Error => Brushes.Salmon,
        LogLevel.Warning => Brushes.Goldenrod,
        LogLevel.Success => Brushes.MediumSeaGreen,
        LogLevel.Debug => Brushes.SlateGray,
        _ => new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF)),
    };
}

/// <summary>
/// The console pane. Lines arrive from background threads (and syslog can be
/// very chatty), so they are queued and flushed to the UI ten times a second
/// instead of one dispatcher hop per line.
/// </summary>
public sealed partial class ConsoleViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 2000;

    /// <summary>Upper bound on lines rendered per 100ms tick.</summary>
    private const int MaxPerFlush = 250;

    private readonly ConcurrentQueue<LogLine> _pending = new();
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private bool _autoScroll = true;

    public ConsoleViewModel(ConsoleLog log)
    {
        foreach (var line in log.Snapshot()) Lines.Add(new ConsoleLineViewModel(line));
        log.LineAppended += Enqueue;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    public ObservableCollection<ConsoleLineViewModel> Lines { get; } = new();

    /// <summary>Raised after new lines are added, so the view can scroll to the end.</summary>
    public event Action? LinesAdded;

    private void Enqueue(LogLine line) => _pending.Enqueue(line);

    private void Flush()
    {
        // Live syslog arrives at a few thousand lines a second. Rendering all of
        // them would achieve nothing except stalling the UI thread, so each tick
        // takes a bounded batch and says how much it skipped.
        var pending = _pending.Count;
        var dropped = Math.Max(0, pending - MaxPerFlush);

        for (var i = 0; i < dropped; i++) _pending.TryDequeue(out _);

        if (dropped > 0)
        {
            Lines.Add(new ConsoleLineViewModel(
                new LogLine(DateTimeOffset.Now, LogLevel.Warning, "console", $"... {dropped} lines skipped")));
        }

        var added = dropped > 0;
        for (var i = 0; i < MaxPerFlush && _pending.TryDequeue(out var line); i++)
        {
            Lines.Add(new ConsoleLineViewModel(line));
            added = true;
        }

        if (!added) return;

        if (Lines.Count > MaxLines)
        {
            for (var i = Lines.Count - MaxLines; i > 0; i--) Lines.RemoveAt(0);
        }

        LinesAdded?.Invoke();
    }

    public void Clear() => Lines.Clear();

    /// <summary>The whole buffer as text, for "copy all".</summary>
    public string AllText() => string.Join(Environment.NewLine, Lines.Select(line => line.Text));

    public void Dispose() => _timer.Stop();
}
