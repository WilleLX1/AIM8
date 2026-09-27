using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIM8.Core.Configuration;

/// <summary>Settings persisted between runs.</summary>
public sealed record AppConfig
{
    /// <summary>Interpreter with pymobiledevice3 installed.</summary>
    public string PythonPath { get; init; } = "python";

    /// <summary>Pin to one device; empty means whichever is attached.</summary>
    public string Udid { get; init; } = "";

    /// <summary>Kept off iBridge's default port so both can run side by side.</summary>
    public int ScreenPort { get; init; } = 8181;

    public string ScreenBindAddress { get; init; } = "127.0.0.1";

    public bool AutoStartScreen { get; init; } = true;

    /// <summary>How often the mirror is sampled for analysis.</summary>
    public int CaptureIntervalMs { get; init; } = 120;

    /// <summary>Frames are scaled down so their long side is at most this.</summary>
    public int CaptureMaxSide { get; init; } = 1800;

    public bool ShowBallLabels { get; init; } = true;

    public AimSettings Aim { get; init; } = new();
}

/// <summary>Reads and writes %APPDATA%\AIM8\config.json.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConfigStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIM8", "config.json");
    }

    public string Path { get; }

    public string? LastLoadError { get; private set; }

    public AppConfig Load()
    {
        LastLoadError = null;
        try
        {
            if (!File.Exists(Path)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Options) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            LastLoadError = $"could not read {Path} ({ex.Message}); using defaults";
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        File.Move(temp, Path, overwrite: true);
    }
}
