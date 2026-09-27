using System.Text.Json;
using AIM8.Core.Logging;

namespace AIM8.Core.Devices;

/// <summary>
/// Owns the connection to the iPhone: discovery, the periodic snapshot behind
/// the Device panel, and the gate every developer-service command goes through.
/// </summary>
public sealed class DeviceService : IAsyncDisposable
{
    private const string Source = "device";

    /// <summary>Ticks between refreshes of the values that rarely change and cost a service round-trip.</summary>
    private const int SlowFactor = 10;

    private readonly Pmd3Runner _pmd3;
    private readonly TunnelService _tunnel;
    private readonly ConsoleLog _log;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _tick;
    private bool? _developerMode;
    private bool? _imageMounted;
    private string? _displayResolution;
    private string? _orientation;
    private bool _mountAttempted;

    public DeviceService(Pmd3Runner pmd3, TunnelService tunnel, ConsoleLog log)
    {
        _pmd3 = pmd3;
        _tunnel = tunnel;
        _log = log;
    }

    /// <summary>Pin to one device; null or empty means "whichever device is attached".</summary>
    public string? PreferredUdid { get; set; }

    public DeviceSnapshot Current { get; private set; } = DeviceSnapshot.Disconnected;

    public event Action<DeviceSnapshot>? SnapshotChanged;

    /// <summary>Device selector for the current snapshot, including the tunnel in use.</summary>
    public DeviceConnection Connection => new(
        string.IsNullOrWhiteSpace(Current.Udid) ? PreferredUdid : Current.Udid,
        _tunnel.Endpoint,
        Current.RequiresTunnel);

    // ---- Discovery -------------------------------------------------------

    public async Task<IReadOnlyList<DeviceSummary>> ListDevicesAsync(CancellationToken ct = default)
    {
        var json = await _pmd3
            .RunJsonAsync(new[] { "usbmux", "list" }, new DeviceConnection(), timeout: TimeSpan.FromSeconds(20), ct: ct)
            .ConfigureAwait(false);

        if (json.ValueKind != JsonValueKind.Array) return Array.Empty<DeviceSummary>();

        var devices = new List<DeviceSummary>();
        foreach (var element in json.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            devices.Add(new DeviceSummary(
                Udid: Str(element, "Identifier", "UniqueDeviceID"),
                Name: Str(element, "DeviceName"),
                ProductType: Str(element, "ProductType"),
                ProductVersion: Str(element, "ProductVersion"),
                BuildVersion: Str(element, "BuildVersion"),
                Connection: Str(element, "ConnectionType").ToUpperInvariant() switch
                {
                    "USB" => ConnectionKind.Usb,
                    "NETWORK" => ConnectionKind.Network,
                    _ => ConnectionKind.Unknown,
                }));
        }

        return devices;
    }

    // ---- Snapshot --------------------------------------------------------

    public async Task<DeviceSnapshot> RefreshAsync(bool full = false, CancellationToken ct = default)
    {
        DeviceSnapshot snapshot;
        try
        {
            snapshot = await ProbeAsync(full, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            snapshot = DeviceSnapshot.Disconnected with { Problem = ex.Message };
        }

        Publish(snapshot);
        return snapshot;
    }

    private async Task<DeviceSnapshot> ProbeAsync(bool full, CancellationToken ct)
    {
        var devices = await ListDevicesAsync(ct).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            return DeviceSnapshot.Disconnected with { Problem = "no device attached" };
        }

        var device = SelectDevice(devices);
        if (device is null)
        {
            return DeviceSnapshot.Disconnected with
            {
                Problem = $"device {PreferredUdid} is not attached ({devices.Count} other device(s) are)",
            };
        }

        var snapshot = new DeviceSnapshot
        {
            Connected = true,
            Trusted = true, // usbmux list only reports values for a paired device
            Udid = device.Udid,
            Name = device.Name,
            ProductType = device.ProductType,
            IosVersion = device.ProductVersion,
            BuildVersion = device.BuildVersion,
            Connection = device.Connection,
            DisplayResolution = _displayResolution,
            DeveloperModeEnabled = _developerMode,
            DeveloperImageMounted = _imageMounted,
        };

        var connection = new DeviceConnection(device.Udid, _tunnel.Endpoint, device.MajorVersion >= 17);

        var (battery, charging) = await TryReadBatteryAsync(connection, ct).ConfigureAwait(false);
        snapshot = snapshot with { BatteryPercent = battery, Charging = charging };

        if (full)
        {
            _developerMode = await TryReadDeveloperModeAsync(connection, ct).ConfigureAwait(false);
            _imageMounted = await TryReadImageMountedAsync(connection, ct).ConfigureAwait(false);
            _displayResolution ??= await TryReadDisplayAsync(connection, ct).ConfigureAwait(false);
            _orientation = await TryReadOrientationAsync(connection, ct).ConfigureAwait(false);

            if (_imageMounted == false && !_mountAttempted)
            {
                _log.Warn(Source, "no DeveloperDiskImage is mounted for this iOS version");
            }
            snapshot = snapshot with
            {
                DeveloperModeEnabled = _developerMode,
                DeveloperImageMounted = _imageMounted,
                DisplayResolution = _displayResolution,
                Orientation = _orientation,
            };
        }
        else
        {
            snapshot = snapshot with { Orientation = _orientation };
        }

        return snapshot;
    }

    private DeviceSummary? SelectDevice(IReadOnlyList<DeviceSummary> devices)
    {
        if (!string.IsNullOrWhiteSpace(PreferredUdid))
        {
            return devices.FirstOrDefault(d => string.Equals(d.Udid, PreferredUdid, StringComparison.OrdinalIgnoreCase));
        }

        // USB first: a Wi-Fi entry for the same phone is the slower path.
        return devices.FirstOrDefault(d => d.Connection == ConnectionKind.Usb) ?? devices[0];
    }

    private async Task<(int?, bool?)> TryReadBatteryAsync(DeviceConnection connection, CancellationToken ct)
    {
        try
        {
            var json = await _pmd3
                .RunJsonAsync(new[] { "diagnostics", "battery", "single" }, connection,
                    timeout: TimeSpan.FromSeconds(20), ct: ct)
                .ConfigureAwait(false);

            int? percent = json.TryGetProperty("CurrentCapacity", out var capacity) &&
                           capacity.ValueKind == JsonValueKind.Number
                ? capacity.GetInt32()
                : null;

            bool? charging = json.TryGetProperty("IsCharging", out var isCharging) &&
                             isCharging.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? isCharging.GetBoolean()
                : json.TryGetProperty("ExternalConnected", out var external) &&
                  external.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? external.GetBoolean()
                    : null;

            return (percent, charging);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private async Task<bool?> TryReadDeveloperModeAsync(DeviceConnection connection, CancellationToken ct)
    {
        try
        {
            var json = await _pmd3
                .RunJsonAsync(new[] { "amfi", "developer-mode-status" }, connection,
                    timeout: TimeSpan.FromSeconds(20), ct: ct)
                .ConfigureAwait(false);
            return json.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<bool?> TryReadImageMountedAsync(DeviceConnection connection, CancellationToken ct)
    {
        try
        {
            var json = await _pmd3
                .RunJsonAsync(new[] { "mounter", "list" }, connection, timeout: TimeSpan.FromSeconds(20), ct: ct)
                .ConfigureAwait(false);

            return json.ValueKind switch
            {
                JsonValueKind.Array => json.GetArrayLength() > 0,
                JsonValueKind.Object => json.EnumerateObject().Any(),
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Current orientation from SpringBoard. This is a lockdown service, so it
    /// costs nothing like the developer ones do.
    /// </summary>
    private async Task<string?> TryReadOrientationAsync(DeviceConnection connection, CancellationToken ct)
    {
        try
        {
            var json = await _pmd3
                .RunJsonAsync(new[] { "springboard", "orientation" }, connection,
                    timeout: TimeSpan.FromSeconds(20), ct: ct)
                .ConfigureAwait(false);

            return json.ValueKind switch
            {
                JsonValueKind.Number => DescribeOrientation(json.GetInt32()),
                JsonValueKind.String => json.GetString(),
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>SpringBoard's UIDeviceOrientation values.</summary>
    internal static string DescribeOrientation(int value) => value switch
    {
        1 => "Portrait",
        2 => "Portrait (upside down)",
        3 => "Landscape (left)",
        4 => "Landscape (right)",
        5 => "Face up",
        6 => "Face down",
        _ => $"Unknown ({value})",
    };

    /// <summary>
    /// Screen size from CoreDevice. It needs a developer tunnel, so it is read
    /// once and then cached - the panel size does not change under us.
    /// </summary>
    private async Task<string?> TryReadDisplayAsync(DeviceConnection connection, CancellationToken ct)
    {
        try
        {
            var json = await _pmd3
                .RunJsonAsync(new[] { "developer", "core-device", "get-display-info" }, connection,
                    developerCommand: true, TimeSpan.FromSeconds(45), ct)
                .ConfigureAwait(false);

            return ReadDisplaySize(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// CoreDevice reports displays as a list, each with a currentMode whose
    /// "size" is a [width, height] pair. The built-in screen is the one that is
    /// not external.
    /// </summary>
    internal static string? ReadDisplaySize(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;
        if (!json.TryGetProperty("displays", out var displays) || displays.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? chosen = null;
        foreach (var display in displays.EnumerateArray())
        {
            if (display.ValueKind != JsonValueKind.Object) continue;

            var external = display.TryGetProperty("external", out var ext) && ext.ValueKind == JsonValueKind.True;
            if (external) continue;

            chosen = display;
            break;
        }

        chosen ??= displays.GetArrayLength() > 0 ? displays[0] : null;
        if (chosen is null) return null;

        if (!chosen.Value.TryGetProperty("currentMode", out var mode) ||
            !mode.TryGetProperty("size", out var size) ||
            size.ValueKind != JsonValueKind.Array ||
            size.GetArrayLength() < 2)
        {
            return null;
        }

        var width = (long)size[0].GetDouble();
        var height = (long)size[1].GetDouble();

        var refresh = mode.TryGetProperty("refreshRate", out var rate) && rate.ValueKind == JsonValueKind.Number
            ? $" @{rate.GetDouble():0}Hz"
            : "";

        return $"{width} x {height}{refresh}";
    }

    // ---- Actions ---------------------------------------------------------

    /// <summary>Asks the device to enable Developer Mode. The user still has to confirm on the phone.</summary>
    public async Task EnableDeveloperModeAsync(CancellationToken ct = default)
    {
        _log.Info(Source, "requesting Developer Mode (confirm on the device, it will reboot)");
        await _pmd3.RunAsync(new[] { "amfi", "enable-developer-mode" }, Connection,
            timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
        _developerMode = null;
    }

    /// <summary>Mounts the matching DeveloperDiskImage, which the DVT services need.</summary>
    public async Task MountDeveloperImageAsync(CancellationToken ct = default)
    {
        _log.Info(Source, "mounting DeveloperDiskImage (downloading it the first time takes a minute)");
        await _pmd3.RunAsync(new[] { "mounter", "auto-mount" }, Connection,
            timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);
        _imageMounted = true;
        _mountAttempted = true;
        _log.Success(Source, "DeveloperDiskImage mounted");
    }

    /// <summary>
    /// Mounts the developer image on demand, once per attached device.
    ///
    /// The image is tied to the OS version, so a major iOS upgrade silently
    /// leaves the device without one - and every developer service then fails
    /// with something that looks unrelated ("No such service ...", a screen
    /// stream that connects but never delivers a frame). Mounting before the
    /// first developer command saves chasing that.
    /// </summary>
    public async Task EnsureDeveloperImageAsync(CancellationToken ct = default)
    {
        // Only act on a definite "not mounted"; null means we have not looked yet.
        if (_imageMounted != false || _mountAttempted) return;

        _mountAttempted = true;
        try
        {
            await MountDeveloperImageAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(Source,
                $"could not mount the DeveloperDiskImage automatically ({ex.Message}). " +
                "Developer services - launch, stats, the screen mirror - will not work until it is mounted.");
        }
    }

    // ---- Command helpers -------------------------------------------------

    /// <summary>Runs a lockdown-level command (no tunnel, no gate).</summary>
    public Task<JsonElement> RunJsonAsync(
        IEnumerable<string> command, TimeSpan? timeout = null, CancellationToken ct = default) =>
        _pmd3.RunJsonAsync(command, Connection, developerCommand: false, timeout, ct);

    public Task<ProcessResult> RunAsync(
        IEnumerable<string> command, TimeSpan? timeout = null, CancellationToken ct = default) =>
        _pmd3.RunAsync(command, Connection, developerCommand: false, timeout, ct);

    /// <summary>
    /// Runs a developer-service command, making sure a tunnel exists first. In
    /// userspace mode these are serialised, because concurrent in-process
    /// tunnels to one device interfere with each other.
    /// </summary>
    public async Task<ProcessResult> RunDeveloperAsync(
        IEnumerable<string> command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var commandList = command as IReadOnlyList<string> ?? command.ToList();
        return await WithDeveloperAccessAsync(
            connection => _pmd3.RunAsync(commandList, connection, developerCommand: true, timeout, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> RunDeveloperJsonAsync(
        IEnumerable<string> command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var commandList = command as IReadOnlyList<string> ?? command.ToList();
        return await WithDeveloperAccessAsync(
            connection => _pmd3.RunJsonAsync(commandList, connection, developerCommand: true, timeout, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prepares tunnel access and runs <paramref name="action"/> with the right
    /// connection, holding the gate when one is needed.
    /// </summary>
    public async Task<T> WithDeveloperAccessAsync<T>(
        Func<DeviceConnection, Task<T>> action, CancellationToken ct = default)
    {
        await EnsureDeveloperImageAsync(ct).ConfigureAwait(false);

        var needsTunnel = Current.RequiresTunnel;
        if (needsTunnel)
        {
            await _tunnel.EnsureAsync(Current.Udid, ct).ConfigureAwait(false);
        }

        var gated = needsTunnel && _tunnel.Endpoint is null;
        if (gated) await _tunnel.DeveloperGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await action(Connection).ConfigureAwait(false);
        }
        finally
        {
            if (gated) _tunnel.DeveloperGate.Release();
        }
    }

    // ---- Polling ---------------------------------------------------------

    public void StartPolling(TimeSpan interval)
    {
        StopPolling();

        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _pollTask = Task.Run(() => PollLoopAsync(interval, cts.Token), CancellationToken.None);
    }

    public void StopPolling()
    {
        var cts = Interlocked.Exchange(ref _pollCts, null);
        if (cts is null) return;

        cts.Cancel();
        cts.Dispose();
    }

    private async Task PollLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var full = _tick % SlowFactor == 0;
                _tick++;
                await RefreshAsync(full, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error(Source, "poll failed", ex);
            }
        }
        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Publish(DeviceSnapshot snapshot)
    {
        var previous = Current;
        Current = snapshot;

        if (previous.Connected != snapshot.Connected || !string.Equals(previous.Udid, snapshot.Udid, StringComparison.Ordinal))
        {
            if (snapshot.Connected)
            {
                _log.Success(Source, $"{snapshot.Name} connected - {snapshot.MarketingName}, iOS {snapshot.IosVersion}");
            }
            else
            {
                _log.Warn(Source, $"device disconnected ({snapshot.Problem})");
                _tunnel.Stop();
            }

            _mountAttempted = false;
            _imageMounted = null;
            _displayResolution = null;
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private static string Str(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }

        return "";
    }

    public async ValueTask DisposeAsync()
    {
        StopPolling();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop is shutting down; its failures are no longer interesting.
            }
        }
    }
}
