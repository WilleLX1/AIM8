using System.Text.Json;

namespace AIM8.Core.Devices;

/// <summary>Host/port of a RemoteServiceDiscovery tunnel, as printed by `start-tunnel --script-mode`.</summary>
public sealed record RsdEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// How to reach one device from the command line: which UDID, and - for the
/// developer services on iOS 17+ - through which tunnel.
/// </summary>
public sealed record DeviceConnection(string? Udid = null, RsdEndpoint? Rsd = null, bool TunnelRequired = false)
{
    /// <summary>
    /// Arguments that select the device. Developer commands (DVT, CoreDevice)
    /// need an RSD tunnel from iOS 17 on; when none has been established we fall
    /// back to `--userspace`, which builds one in-process without admin rights.
    ///
    /// <paramref name="supportsUdid"/> is false for the `core-device` commands,
    /// which take only the tunnel options - passing --udid to those is an error.
    /// </summary>
    public IEnumerable<string> SelectorArgs(bool developerCommand, bool supportsUdid = true)
    {
        if (developerCommand && TunnelRequired)
        {
            if (Rsd is not null)
            {
                yield return "--rsd";
                yield return Rsd.Host;
                yield return Rsd.Port.ToString();
                yield break;
            }

            yield return "--userspace";
        }

        if (supportsUdid && !string.IsNullOrWhiteSpace(Udid))
        {
            yield return "--udid";
            yield return Udid!;
        }
    }
}

public sealed class Pmd3Exception : Exception
{
    public Pmd3Exception(string command, ProcessResult result)
        : base($"pymobiledevice3 {command} failed: {result.FailureText}")
    {
        Command = command;
        Result = result;
    }

    public Pmd3Exception(string command, string message) : base($"pymobiledevice3 {command}: {message}")
    {
        Command = command;
    }

    public string Command { get; }

    public ProcessResult? Result { get; }
}

/// <summary>
/// Runs pymobiledevice3 as a child process. Every device capability in the app
/// funnels through here, so the interpreter, the device selector and the JSON
/// handling live in exactly one place.
/// </summary>
public sealed class Pmd3Runner
{
    private readonly ProcessRunner _processes;

    public Pmd3Runner(string pythonPath, ProcessRunner? processes = null)
    {
        PythonPath = string.IsNullOrWhiteSpace(pythonPath) ? "python" : pythonPath;
        _processes = processes ?? new ProcessRunner();
    }

    public string PythonPath { get; set; }

    /// <summary>Full argument vector for `python -m pymobiledevice3 ...`.</summary>
    public static IReadOnlyList<string> BuildArguments(
        IEnumerable<string> command, DeviceConnection connection, bool developerCommand)
    {
        var commandList = command as IReadOnlyList<string> ?? command.ToList();
        var selector = connection.SelectorArgs(developerCommand, SupportsUdid(commandList)).ToList();

        var args = new List<string> { "-m", "pymobiledevice3", "--no-color" };

        // Everything after a "--" is positional (simulate-location uses it so a
        // negative longitude is not read as an option), so device options have
        // to go in front of it rather than at the end.
        var separator = IndexOfSeparator(commandList);
        if (separator >= 0)
        {
            for (var i = 0; i < separator; i++) args.Add(commandList[i]);
            args.AddRange(selector);
            for (var i = separator; i < commandList.Count; i++) args.Add(commandList[i]);
        }
        else
        {
            args.AddRange(commandList);
            args.AddRange(selector);
        }

        return args;
    }

    private static int IndexOfSeparator(IReadOnlyList<string> command)
    {
        for (var i = 0; i < command.Count; i++)
        {
            if (string.Equals(command[i], "--", StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    /// <summary>
    /// The `developer core-device ...` family reaches the device purely through
    /// the tunnel and rejects --udid outright ("No such option"), unlike every
    /// other command group. Selecting between two attached devices there means
    /// giving the tunnel for the one you want.
    /// </summary>
    internal static bool SupportsUdid(IReadOnlyList<string> command) =>
        command.Count < 2 ||
        !(string.Equals(command[0], "developer", StringComparison.Ordinal) &&
          string.Equals(command[1], "core-device", StringComparison.Ordinal));

    public Task<ProcessResult> RunRawAsync(
        IEnumerable<string> command,
        DeviceConnection connection,
        bool developerCommand = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var args = BuildArguments(command, connection, developerCommand);
        return _processes.RunAsync(PythonPath, args, timeout ?? TimeSpan.FromSeconds(60), ct: ct);
    }

    /// <summary>Runs a command and throws unless it exits cleanly.</summary>
    public async Task<ProcessResult> RunAsync(
        IEnumerable<string> command,
        DeviceConnection connection,
        bool developerCommand = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var commandList = command as IReadOnlyList<string> ?? command.ToList();
        var result = await RunRawAsync(commandList, connection, developerCommand, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded) throw new Pmd3Exception(string.Join(' ', commandList), result);
        return result;
    }

    /// <summary>Runs a command whose output is JSON and returns the parsed document.</summary>
    public async Task<JsonElement> RunJsonAsync(
        IEnumerable<string> command,
        DeviceConnection connection,
        bool developerCommand = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var commandList = command as IReadOnlyList<string> ?? command.ToList();
        var result = await RunAsync(commandList, connection, developerCommand, timeout, ct).ConfigureAwait(false);
        var text = ExtractJson(result.StdOut);
        if (text is null)
        {
            throw new Pmd3Exception(string.Join(' ', commandList), "no JSON in output");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new Pmd3Exception(string.Join(' ', commandList), $"unparseable JSON ({ex.Message})");
        }
    }

    public ProcessHandle Start(
        IEnumerable<string> command,
        DeviceConnection connection,
        bool developerCommand = false,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null)
    {
        var args = BuildArguments(command, connection, developerCommand);
        return _processes.Start(PythonPath, args, onStdOut, onStdErr);
    }

    /// <summary>Version string of the installed pymobiledevice3, or null when it cannot be run.</summary>
    public async Task<string?> TryGetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _processes
                .RunAsync(PythonPath, new[] { "-m", "pymobiledevice3", "--no-color", "version" },
                    TimeSpan.FromSeconds(30), ct: ct)
                .ConfigureAwait(false);
            return result.Succeeded ? result.StdOut.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the JSON value out of a command's stdout, tolerating banners or
    /// warnings printed before it.
    /// </summary>
    internal static string? ExtractJson(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        var trimmed = stdout.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return trimmed;

        var start = trimmed.IndexOfAny(new[] { '{', '[' });
        if (start >= 0)
        {
            var candidate = trimmed[start..].TrimEnd();
            if (candidate.Length > 0) return candidate;
        }

        // Several commands answer with a bare JSON scalar rather than an object:
        // `amfi developer-mode-status` prints true, `springboard orientation`
        // prints 1. Those are valid JSON documents and must not be discarded.
        if (IsJson(trimmed)) return trimmed;

        var lastLine = trimmed
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return lastLine is not null && IsJson(lastLine) ? lastLine : null;
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
