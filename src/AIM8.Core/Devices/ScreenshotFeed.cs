using AIM8.Core.Logging;

namespace AIM8.Core.Devices;

/// <summary>
/// For phones that will not mirror (Apple gates the live stream on iOS 27 -
/// see iBridge's notes), takes `developer dvt screenshot` in a loop instead.
/// Much slower than the mirror, but pool is turn-based: the table only has to
/// be read once the balls stop.
/// </summary>
public sealed class ScreenshotFeed : IAsyncDisposable
{
    private const string Source = "feed";

    private readonly DeviceService _device;
    private readonly ConsoleLog _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ScreenshotFeed(DeviceService device, ConsoleLog log)
    {
        _device = device;
        _log = log;
    }

    public bool Running => _cts is not null;

    /// <summary>Raised on a worker thread with the path of each new screenshot.</summary>
    public event Action<string>? Captured;

    public void Start(string directory, TimeSpan interval)
    {
        Stop();
        Directory.CreateDirectory(directory);

        var cts = new CancellationTokenSource();
        _cts = cts;
        _loop = Task.Run(() => LoopAsync(directory, interval, cts.Token), CancellationToken.None);
        _log.Info(Source, "taking screenshots in a loop (the phone does not mirror)");
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
        _log.Info(Source, "screenshot feed stopped");
    }

    private async Task LoopAsync(string directory, TimeSpan interval, CancellationToken ct)
    {
        var count = 0;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;

            // A fresh name every time: the page would otherwise show its cached copy.
            var path = Path.Combine(directory, $"feed-{count++}.png");
            try
            {
                await _device
                    .RunDeveloperAsync(["developer", "dvt", "screenshot", path], TimeSpan.FromSeconds(45), ct)
                    .ConfigureAwait(false);

                failures = 0;
                if (File.Exists(path)) Captured?.Invoke(path);
                DeleteOld(directory, count - 3);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (++failures is 1 or 10) _log.Warn(Source, $"screenshot failed: {ex.Message}");
            }

            var wait = interval - (DateTimeOffset.UtcNow - started);
            try
            {
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void DeleteOld(string directory, int below)
    {
        for (var i = below; i >= Math.Max(0, below - 5); i--)
        {
            try
            {
                File.Delete(Path.Combine(directory, $"feed-{i}.png"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still being read by the page; the next pass gets it.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutting down.
            }
        }
    }
}
