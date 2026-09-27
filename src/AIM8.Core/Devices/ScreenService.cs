using System.Net.Sockets;
using AIM8.Core.Logging;

namespace AIM8.Core.Devices;

public enum ScreenState
{
    Stopped,
    Starting,
    Running,
    Failed,

    /// <summary>The device refuses the media stream - see <see cref="ScreenService.UnsupportedReason"/>.</summary>
    Unsupported,
}

/// <summary>
/// Runs `developer core-device display serve-web`, which streams the phone's
/// screen over HTTP and accepts touch, keyboard and button input back. The app
/// embeds that page in a WebView2, so a click in the window is a touch on the
/// phone.
///
/// The stream is deliberately independent of build and deploy: it is started
/// once the device appears and keeps running while apps are installed and
/// relaunched, so you watch the deploy happen.
/// </summary>
public sealed class ScreenService : IAsyncDisposable
{
    private const string Source = "screen";

    private readonly DeviceService _device;
    private readonly TunnelService _tunnel;
    private readonly Pmd3Runner _pmd3;
    private readonly ConsoleLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProcessHandle? _process;

    public ScreenService(DeviceService device, TunnelService tunnel, Pmd3Runner pmd3, ConsoleLog log)
    {
        _device = device;
        _tunnel = tunnel;
        _pmd3 = pmd3;
        _log = log;
    }

    public ScreenState State { get; private set; } = ScreenState.Stopped;

    public string BindAddress { get; private set; } = "127.0.0.1";

    public int Port { get; private set; } = 8080;

    public string? LastError { get; private set; }

    /// <summary>
    /// Why the device turned the stream down, when it did. Apple gates the
    /// CoreDevice media stream ("remote control") on the iOS version, and the
    /// HTTP viewer still starts and answers 200 when the stream is refused - so
    /// a listening port is not on its own evidence of a working mirror.
    /// </summary>
    public string? UnsupportedReason { get; private set; }

    /// <summary>A concrete start-up failure recognised in serve-web's output.</summary>
    public string? StartupProblem { get; private set; }

    /// <summary>Query string appended to the viewer URL; see IBridgeConfig.ScreenViewerQuery.</summary>
    public string ViewerQuery { get; set; } = "";

    /// <summary>URL for the WebView2 to load; null unless the server is up.</summary>
    public string? ViewerUrl => State == ScreenState.Running
        ? $"http://{ViewerHost}:{Port}/{(string.IsNullOrWhiteSpace(ViewerQuery) ? "" : "?" + ViewerQuery.TrimStart('?'))}"
        : null;

    private string ViewerHost => BindAddress is "0.0.0.0" or "::" ? "127.0.0.1" : BindAddress;

    /// <summary>
    /// True when the mirror holds the only tunnel we have. Other developer
    /// commands still work, but each builds its own userspace tunnel, so the
    /// caller should poll less aggressively while this is set.
    /// </summary>
    public bool SharesUserspaceTunnel => State == ScreenState.Running && _tunnel.Endpoint is null;

    public event Action? StateChanged;

    public async Task StartAsync(string bindAddress, int port, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is ScreenState.Running or ScreenState.Starting) return;

            if (!_device.Current.Connected)
            {
                SetState(ScreenState.Stopped, "no device");
                return;
            }

            BindAddress = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1" : bindAddress;
            Port = port;

            if (!string.Equals(BindAddress, "127.0.0.1", StringComparison.Ordinal) &&
                !string.Equals(BindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                // The viewer's /touch, /button and /key endpoints are unauthenticated.
                _log.Warn(Source, $"serving the screen on {BindAddress}:{Port} - anyone who can reach that port can control the phone");
            }

            SetState(ScreenState.Starting, null);
            UnsupportedReason = null;
            StartupProblem = null;

            if (_device.Current.RequiresTunnel)
            {
                await _tunnel.EnsureAsync(_device.Current.Udid, ct).ConfigureAwait(false);
            }

            var command = new[]
            {
                "developer", "core-device", "display", "serve-web",
                "--bind", BindAddress,
                "--http-port", Port.ToString(),
            };

            _log.Info(Source, $"starting screen stream on {BindAddress}:{Port}");

            var handle = _pmd3.Start(
                command,
                _device.Connection,
                developerCommand: true,
                onStdOut: line => Observe(line),
                onStdErr: line => Observe(line));

            _process = handle;
            handle.Exited += code =>
            {
                if (State == ScreenState.Stopped) return;
                SetState(ScreenState.Failed, $"serve-web exited with code {code}");
                _log.Warn(Source, $"screen stream stopped (exit code {code})");
            };

            var ready = await WaitForPortAsync(ViewerHost, Port, TimeSpan.FromSeconds(45), handle, ct)
                .ConfigureAwait(false);

            if (!ready)
            {
                handle.Dispose();
                _process = null;

                var problem = StartupProblem ?? "serve-web did not start listening";
                SetState(ScreenState.Failed, problem);
                _log.Error(Source, StartupProblem is null
                    ? "screen stream failed to start - check Developer Mode and the DDI mount"
                    : $"screen stream failed to start: {problem}");
                return;
            }

            // The server binds before it asks the device for the stream, so give
            // the refusal a moment to arrive rather than declaring victory on a
            // viewer that will never show a frame.
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

            if (UnsupportedReason is { } reason)
            {
                handle.Dispose();
                _process = null;
                SetState(ScreenState.Unsupported, reason);
                _log.Warn(Source, $"this device will not mirror: {reason}");
                return;
            }

            SetState(ScreenState.Running, null);
            _log.Success(Source, $"screen stream live at {ViewerUrl}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null && State == ScreenState.Stopped) return;

        SetState(ScreenState.Stopped, null);
        process?.Dispose();
        _log.Info(Source, "screen stream stopped");
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        Stop();
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        await StartAsync(BindAddress, Port, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Watches serve-web's output for the device refusing the media stream. The
    /// refusal is logged (and retried internally) rather than being fatal to the
    /// process, so it has to be recognised in the text.
    /// </summary>
    private void Observe(string line)
    {
        if (UnsupportedReason is null && TryReadUnsupportedReason(line) is { } reason)
        {
            UnsupportedReason = reason;
        }

        if (StartupProblem is null && IsPortInUse(line))
        {
            StartupProblem =
                $"port {Port} is already in use - another viewer, or a serve-web left behind by a previous run";
        }

        _log.Debug(Source, line);
    }

    /// <summary>
    /// A force-quit leaves the serve-web child alive holding the port, and the
    /// bind failure that follows says nothing about Developer Mode - so it must
    /// not be reported as if it did.
    /// </summary>
    internal static bool IsPortInUse(string line) =>
        line.Contains("10048", StringComparison.Ordinal) ||
        line.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recognises the CoreDevice refusals that mean "this device cannot mirror",
    /// as opposed to a transient error worth retrying.
    /// </summary>
    internal static string? TryReadUnsupportedReason(string line)
    {
        if (!line.Contains("startmediastream", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("Remote control requires", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // e.g. "...startmediastream: Remote control requires iOS 27.0 or later on this device. (code 9021)"
        var marker = line.IndexOf("Remote control requires", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var text = line[marker..].Trim();
            var code = text.IndexOf(" (code", StringComparison.OrdinalIgnoreCase);
            return code > 0 ? text[..code] : text;
        }

        var colon = line.LastIndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && colon + 2 < line.Length ? line[(colon + 2)..].Trim() : "the device refused the media stream";
    }

    /// <summary>Waits until the viewer's port accepts connections, giving up if the process dies first.</summary>
    private static async Task<bool> WaitForPortAsync(
        string host, int port, TimeSpan timeout, ProcessHandle handle, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!handle.IsRunning) return false;

            try
            {
                using var client = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    private void SetState(ScreenState state, string? error)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
