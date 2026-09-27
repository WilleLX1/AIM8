using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AIM8.Core.Devices;

/// <summary>
/// pymobiledevice3 colours its log output even under --no-color (that switch
/// only governs its own printing, not the logging handler), so escape codes are
/// stripped before any line reaches the console pane or an error message.
/// </summary>
public static partial class Ansi
{
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex EscapeCodes();

    public static string Strip(string text) =>
        string.IsNullOrEmpty(text) || !text.Contains('\x1B') ? text : EscapeCodes().Replace(text, "");
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Best available description of a failure, for logs and exceptions.</summary>
    public string FailureText
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
            text = text.Trim();
            if (text.Length == 0) return $"exit code {ExitCode}";
            return text.Length <= 400 ? text : text[..400] + "...";
        }
    }
}

/// <summary>A process kept alive in the background (tunnel, screen server, syslog).</summary>
public sealed class ProcessHandle : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal ProcessHandle(Process process, string description)
    {
        _process = process;
        Description = description;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            var code = SafeExitCode();
            _exited.TrySetResult(code);
            Exited?.Invoke(code);
        };
    }

    public string Description { get; }

    public int Id => _process.Id;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Completes with the exit code once the process ends.</summary>
    public Task<int> WaitForExitAsync() => _exited.Task;

    public event Action<int>? Exited;

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or we lost the right to signal it - nothing useful to do.
        }
    }

    private int SafeExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Kill();
        _process.Dispose();
    }
}

/// <summary>Runs external tools (the Python interpreter, in practice) and collects their output.</summary>
public sealed class ProcessRunner
{
    /// <summary>Runs to completion and captures stdout/stderr.</summary>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var startInfo = CreateStartInfo(fileName, arguments, workingDirectory);
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var outputDone = new SemaphoreSlim(0, 2);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) outputDone.Release();
            else stdout.AppendLine(Ansi.Strip(e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) outputDone.Release();
            else stderr.AppendLine(Ansi.Strip(e.Data));
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit) timeoutCts.CancelAfter(limit);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            // Give the async readers a moment to drain their final lines.
            await outputDone.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            await outputDone.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (ct.IsCancellationRequested) throw;

            throw new TimeoutException(
                $"{Path.GetFileName(fileName)} {string.Join(' ', arguments)} did not finish within {timeout}.");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed);
    }

    /// <summary>
    /// Starts a long-running process and streams its output line by line. The
    /// caller owns the returned handle and must dispose it.
    /// </summary>
    public ProcessHandle Start(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        string? workingDirectory = null)
    {
        var argumentList = arguments.ToList();
        var startInfo = CreateStartInfo(fileName, argumentList, workingDirectory);

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) onStdOut?.Invoke(Ansi.Strip(e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) onStdErr?.Invoke(Ansi.Strip(e.Data));
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new ProcessHandle(process, $"{Path.GetFileName(fileName)} {string.Join(' ', argumentList)}");
    }

    private static ProcessStartInfo CreateStartInfo(
        string fileName, IEnumerable<string> arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;

        // Keep child output machine-readable regardless of the user's console.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process ended on its own between the check and the kill.
        }
    }
}
