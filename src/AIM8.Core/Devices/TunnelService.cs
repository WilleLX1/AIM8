using System.Globalization;
using AIM8.Core.Logging;

namespace AIM8.Core.Devices;

/// <summary>
/// Keeps an RSD tunnel to the device for the iOS 17+ developer services.
///
/// `lockdown start-tunnel` builds a kernel tunnel, which Windows only allows an
/// elevated process to create. When that is refused we fall back to per-command
/// `--userspace` tunnels: no admin rights needed, but each command pays to
/// build its own, so they are serialised by <see cref="DeveloperGate"/>.
/// </summary>
public sealed class TunnelService : IAsyncDisposable
{
    private const string Source = "tunnel";

    private readonly Pmd3Runner _pmd3;
    private readonly ConsoleLog _log;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private ProcessHandle? _process;

    public TunnelService(Pmd3Runner pmd3, ConsoleLog log)
    {
        _pmd3 = pmd3;
        _log = log;
    }

    /// <summary>The live tunnel, or null when none is up.</summary>
    public RsdEndpoint? Endpoint { get; private set; }

    /// <summary>Set once a tunnel attempt was refused for lack of admin rights.</summary>
    public bool UserspaceFallback { get; private set; }

    /// <summary>
    /// Serialises developer commands while running in userspace mode, where two
    /// concurrent tunnels to the same device fight over it.
    /// </summary>
    public SemaphoreSlim DeveloperGate { get; } = new(1, 1);

    public event Action? StateChanged;

    public bool IsRunning => _process is { IsRunning: true } && Endpoint is not null;

    /// <summary>
    /// Ensures a shared tunnel exists for <paramref name="udid"/>. Returns the
    /// endpoint, or null when the app must use per-command userspace tunnels.
    /// </summary>
    public async Task<RsdEndpoint?> EnsureAsync(string? udid, CancellationToken ct = default)
    {
        if (IsRunning) return Endpoint;
        if (UserspaceFallback) return null;

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning) return Endpoint;
            if (UserspaceFallback) return null;

            return await StartAsync(udid, ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<RsdEndpoint?> StartAsync(string? udid, CancellationToken ct)
    {
        Stop();

        var ready = new TaskCompletionSource<RsdEndpoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new List<string>();

        var command = new List<string> { "lockdown", "start-tunnel", "--script-mode" };
        var connection = new DeviceConnection(udid);

        _log.Info(Source, "starting RSD tunnel");

        var handle = _pmd3.Start(
            command,
            connection,
            developerCommand: false,
            onStdOut: line =>
            {
                if (TryParseEndpoint(line, out var endpoint)) ready.TrySetResult(endpoint);
            },
            onStdErr: line =>
            {
                failure.Add(line);
                if (LooksLikePermissionProblem(line)) ready.TrySetResult(null);
            });

        _process = handle;
        handle.Exited += _ =>
        {
            ready.TrySetResult(null);
            if (Endpoint is not null)
            {
                _log.Warn(Source, "tunnel closed");
                Endpoint = null;
                StateChanged?.Invoke();
            }
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        RsdEndpoint? result;
        try
        {
            result = await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = null;
        }

        if (result is null)
        {
            Stop();
            UserspaceFallback = true;

            var reason = failure.FirstOrDefault(LooksLikePermissionProblem)
                         ?? failure.LastOrDefault()
                         ?? "no endpoint was reported";
            _log.Warn(Source, $"shared tunnel unavailable ({reason.Trim()}); using per-command userspace tunnels");
            StateChanged?.Invoke();
            return null;
        }

        Endpoint = result;
        _log.Success(Source, $"RSD tunnel up on {result}");
        StateChanged?.Invoke();
        return result;
    }

    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        process?.Dispose();
        if (Endpoint is not null)
        {
            Endpoint = null;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Allows a later retry of the shared tunnel, e.g. after a restart as admin.</summary>
    public void ResetFallback() => UserspaceFallback = false;

    /// <summary>`--script-mode` prints exactly "ADDRESS PORT" once the tunnel is up.</summary>
    internal static bool TryParseEndpoint(string line, out RsdEndpoint endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) return false;
        if (port is <= 0 or > 65535) return false;
        if (!parts[0].Contains(':') && !parts[0].Contains('.')) return false;

        endpoint = new RsdEndpoint(parts[0], port);
        return true;
    }

    private static bool LooksLikePermissionProblem(string line) =>
        line.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("root", StringComparison.OrdinalIgnoreCase);

    public ValueTask DisposeAsync()
    {
        Stop();
        DeveloperGate.Dispose();
        _startGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
