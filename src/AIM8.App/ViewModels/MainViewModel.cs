using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AIM8.Core;
using AIM8.Core.Configuration;
using AIM8.Core.Demo;
using AIM8.Core.Devices;
using AIM8.Core.Logging;
using AIM8.Core.Solver;
using AIM8.Core.Vision;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AIM8.App.ViewModels;

/// <summary>
/// Composition root and state behind the window: the phone (ported from
/// iBridge), the aim settings the user picks, and the latest analysis.
/// Services raise events on background threads; everything bindable is
/// updated on the dispatcher.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const string Source = "aim8";

    /// <summary>Host name the offline pages (screenshots, demo tables) are served under.</summary>
    public const string OfflineHost = "aim8.local";

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly ConfigStore _configStore = new();
    private readonly ProcessRunner _processes = new();
    private readonly Pmd3Runner _pmd3;
    private readonly TunnelService _tunnel;
    private readonly DeviceService _device;
    private readonly ScreenService _screen;
    private readonly ScreenshotFeed _feed;
    private readonly DispatcherTimer _saveTimer;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

    private AppConfig _config;
    private FrameBridge? _bridge;
    private bool _loading = true;
    private int _framesThisSecond;
    private int _demoSeed = Environment.TickCount;

    [ObservableProperty] private string _screenStatus = "stopped";
    [ObservableProperty] private string? _screenUrl;
    [ObservableProperty] private string _screenMessage = "Waiting for an iPhone - or open a screenshot / demo table";
    [ObservableProperty] private string _tunnelMode = "";
    [ObservableProperty] private string _toolchainProblem = "";

    /// <summary>True while the mirror shows the phone rather than a screenshot or demo.</summary>
    [ObservableProperty] private bool _isLive = true;
    [ObservableProperty] private bool _feedRunning;
    [ObservableProperty] private string _sourceTitle = "LIVE iPHONE";

    // ---- Aim settings ------------------------------------------------------

    [ObservableProperty] private Team _team = Team.Solids;
    [ObservableProperty] private bool _allowBanks = true;
    [ObservableProperty] private bool _allowKicks = true;
    [ObservableProperty] private bool _allowCombos = true;
    [ObservableProperty] private int _alternatives = 2;
    [ObservableProperty] private bool _showLabels = true;
    [ObservableProperty] private bool _analysisEnabled = true;
    [ObservableProperty] private double _feltTolerance = 1.0;
    [ObservableProperty] private bool _autoStripe = true;
    [ObservableProperty] private double _stripeThreshold = BallClassifier.DefaultStripeThreshold;
    [ObservableProperty] private bool _autoBallSize = true;
    [ObservableProperty] private double _ballSizePercent = 3.3;
    [ObservableProperty] private bool _detectCushionEdge = true;
    [ObservableProperty] private NormalizedRect? _manualTable;
    [ObservableProperty] private bool _isCalibrating;

    // ---- Results -----------------------------------------------------------

    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private string _statusKey = "pending";
    [ObservableProperty] private string _bestShot = "—";
    [ObservableProperty] private string _bestShotDetail = "";
    [ObservableProperty] private double _bestChance;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private string _tableText = "";
    [ObservableProperty] private string _timingText = "";

    public MainViewModel()
    {
        Log = new ConsoleLog();
        Console = new ConsoleViewModel(Log);

        _config = _configStore.Load();

        _pmd3 = new Pmd3Runner(_config.PythonPath, _processes);
        _tunnel = new TunnelService(_pmd3, Log);
        _device = new DeviceService(_pmd3, _tunnel, Log) { PreferredUdid = NullIfEmpty(_config.Udid) };
        _screen = new ScreenService(_device, _tunnel, _pmd3, Log);
        _feed = new ScreenshotFeed(_device, Log);
        _feed.Captured += OnFeedCaptured;

        _device.SnapshotChanged += OnDeviceSnapshot;
        _screen.StateChanged += OnScreenStateChanged;
        _tunnel.StateChanged += () => OnUi(UpdateTunnelMode);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveConfig();
        };

        LoadAimSettings(_config);
        _loading = false;
    }

    public ConsoleLog Log { get; }

    public ConsoleViewModel Console { get; }

    public DeviceViewModel Device { get; } = new();

    public ObservableCollection<string> Alternates { get; } = [];

    /// <summary>
    /// Where offline pages and their images are written for the WebView to
    /// load. Outside AppData on purpose: a Microsoft Store Python has its
    /// AppData writes redirected, so screenshots it saved there would never
    /// show up for us (the same trap iBridge documents for IPAs).
    /// </summary>
    public string WebFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aim8", "web");

    /// <summary>The window navigates the mirror when this fires.</summary>
    public event Action<string>? NavigationRequested;

    public bool IsSolids
    {
        get => Team == Team.Solids;
        set { if (value) Team = Team.Solids; }
    }

    public bool IsStripes
    {
        get => Team == Team.Stripes;
        set { if (value) Team = Team.Stripes; }
    }

    public bool IsOpen
    {
        get => Team == Team.Open;
        set { if (value) Team = Team.Open; }
    }

    public string CalibrationText => ManualTable is null
        ? "Detected automatically"
        : $"Set by hand ({(ManualTable.X1 - ManualTable.X0) * 100:0}% × {(ManualTable.Y1 - ManualTable.Y0) * 100:0}% of the screen)";

    public string StripeText => AutoStripe ? "auto" : $"{StripeThreshold:0.00}";

    public string BallSizeText => AutoBallSize ? "auto" : $"{BallSizePercent:0.0}%";

    public string FeltText => $"{FeltTolerance:0.00}×";

    // ---- Lifetime ----------------------------------------------------------

    public async Task InitializeAsync()
    {
        Log.Info(Source, "AIM8 starting");
        if (_configStore.LastLoadError is { } error) Log.Error(Source, error);

        var version = await _pmd3.TryGetVersionAsync().ConfigureAwait(true);
        if (version is null)
        {
            ToolchainProblem = $"pymobiledevice3 not found for '{_config.PythonPath}' - pip install -U pymobiledevice3 (screenshots and the demo still work)";
            Log.Error(Source, ToolchainProblem);
        }
        else
        {
            Log.Info(Source, $"pymobiledevice3 {version}");
        }

        _device.StartPolling(TimeSpan.FromSeconds(1));
    }

    /// <summary>Called once the WebView exists; from then on frames flow.</summary>
    internal void AttachBridge(FrameBridge bridge)
    {
        _bridge = bridge;
        bridge.Settings = BuildAimSettings();
        bridge.Analyzed += OnAnalyzed;
        bridge.Calibrated += OnCalibrated;
        bridge.Log += message => Log.Warn("overlay", message);
        PushOverlayConfig();
    }

    // ---- Source commands ---------------------------------------------------

    [RelayCommand]
    private void BackToLive()
    {
        StopFeed();
        IsLive = true;
        SourceTitle = "LIVE iPHONE";
        _bridge?.Reset();
        if (ScreenUrl is { } url) NavigationRequested?.Invoke(url);
        else NavigationRequested?.Invoke("about:blank");
        UpdateScreenMessage();
        if (Device.Connected && _screen.State is not (ScreenState.Running or ScreenState.Starting))
        {
            _ = StartScreenAsync();
        }
    }

    [RelayCommand]
    private void OpenScreenshot()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a screenshot of the pool table",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var name = $"shot-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(dialog.FileName).ToLowerInvariant()}";
            Directory.CreateDirectory(Path.Combine(WebFolder, "shots"));
            File.Copy(dialog.FileName, Path.Combine(WebFolder, "shots", name), overwrite: true);
            ShowOffline($"shots/{name}", Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            Log.Error(Source, "could not open the screenshot", ex);
        }
    }

    /// <summary>Renders a random mid-game table, so the assist can be tried without a phone.</summary>
    [RelayCommand]
    private void DemoTable()
    {
        try
        {
            var scene = TableRenderer.RandomScene(_demoSeed++);
            var frame = TableRenderer.Render(scene);
            var name = $"demo-{DateTime.Now:HHmmss-fff}.png";
            Directory.CreateDirectory(Path.Combine(WebFolder, "shots"));
            PngFile.Save(frame.Pixels, frame.Width, frame.Height, Path.Combine(WebFolder, "shots", name));
            ShowOffline($"shots/{name}", "demo table");
        }
        catch (Exception ex)
        {
            Log.Error(Source, "could not render the demo table", ex);
        }
    }

    /// <summary>Saves what the analysis last saw - the thing to send along when detection is off.</summary>
    [RelayCommand]
    private void SaveFrame()
    {
        var last = _bridge?.LastFrame();
        if (last is not { } frame)
        {
            Log.Warn(Source, "no frame captured yet");
            return;
        }

        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AIM8");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"frame-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            PngFile.Save(frame.Pixels, frame.Width, frame.Height, path);
            Log.Success(Source, $"frame saved to {path}");
        }
        catch (Exception ex)
        {
            Log.Error(Source, "could not save the frame", ex);
        }
    }

    private void ShowOffline(string relativePath, string label)
    {
        StopFeed();
        IsLive = false;
        SourceTitle = $"SCREENSHOT · {label}";
        ScreenMessage = "";
        _bridge?.Reset();
        NavigationRequested?.Invoke($"https://{OfflineHost}/offline.html?img={Uri.EscapeDataString(relativePath)}");
        Log.Info(Source, $"analysing {label}");
    }

    // ---- Screenshot feed ---------------------------------------------------

    /// <summary>Screenshots instead of the mirror - for iOS versions that refuse to stream.</summary>
    [RelayCommand]
    private void ToggleFeed()
    {
        if (FeedRunning)
        {
            BackToLive();
            return;
        }

        StartFeed();
    }

    private void StartFeed()
    {
        if (!Device.Connected)
        {
            Log.Warn(Source, "connect the iPhone first");
            return;
        }

        IsLive = false;
        FeedRunning = true;
        if (_screen.State is ScreenState.Running or ScreenState.Starting) _screen.Stop();
        SourceTitle = "iPHONE · SCREENSHOTS";
        ScreenMessage = "";
        _bridge?.Reset();
        NavigationRequested?.Invoke($"https://{OfflineHost}/offline.html");

        // Every command builds its own tunnel without admin rights, which is
        // what bounds the rate; asking for more only queues them up.
        var perCommandTunnel = _device.Current.RequiresTunnel && _tunnel.Endpoint is null;
        _feed.Start(Path.Combine(WebFolder, "feed"), TimeSpan.FromSeconds(perCommandTunnel ? 2 : 0.5));
    }

    private void StopFeed()
    {
        if (!FeedRunning) return;
        _feed.Stop();
        FeedRunning = false;
    }

    private void OnFeedCaptured(string path) => OnUi(() =>
    {
        if (!FeedRunning) return;
        _bridge?.ShowImage($"feed/{Path.GetFileName(path)}", $"Screenshot {DateTime.Now:HH:mm:ss} · interact on iPhone");
    });
    // ---- Calibration -------------------------------------------------------

    [RelayCommand]
    private void Calibrate()
    {
        IsCalibrating = true;
        _bridge?.SetCalibrating(true);
        Log.Info(Source, "calibrating: drag across the playing surface on the phone image, from cushion to cushion");
    }

    [RelayCommand]
    private void ClearCalibration()
    {
        ManualTable = null;
        _bridge?.Reset();
        Log.Info(Source, "table detection back to automatic");
    }

    private void OnCalibrated(NormalizedRect? rect)
    {
        IsCalibrating = false;
        if (rect is null)
        {
            Log.Info(Source, "calibration cancelled");
            return;
        }

        ManualTable = rect;
        _bridge?.Reset();
        Log.Success(Source, "table set by hand");
    }

    // ---- Settings plumbing -------------------------------------------------

    partial void OnTeamChanged(Team value)
    {
        OnPropertyChanged(nameof(IsSolids));
        OnPropertyChanged(nameof(IsStripes));
        OnPropertyChanged(nameof(IsOpen));
        SettingsChanged();
        if (!_loading) Log.Info(Source, $"playing {value switch { Team.Solids => "whole balls (1-7)", Team.Stripes => "half balls (9-15)", _ => "an open table" }}");
    }

    partial void OnAllowBanksChanged(bool value) => SettingsChanged();

    partial void OnAllowKicksChanged(bool value) => SettingsChanged();

    partial void OnAllowCombosChanged(bool value) => SettingsChanged();

    partial void OnAlternativesChanged(int value) => SettingsChanged();

    partial void OnFeltToleranceChanged(double value)
    {
        OnPropertyChanged(nameof(FeltText));
        SettingsChanged();
    }

    partial void OnAutoStripeChanged(bool value)
    {
        OnPropertyChanged(nameof(StripeText));
        SettingsChanged();
    }

    partial void OnStripeThresholdChanged(double value)
    {
        OnPropertyChanged(nameof(StripeText));
        SettingsChanged();
    }

    partial void OnAutoBallSizeChanged(bool value)
    {
        OnPropertyChanged(nameof(BallSizeText));
        SettingsChanged();
    }

    partial void OnBallSizePercentChanged(double value)
    {
        OnPropertyChanged(nameof(BallSizeText));
        SettingsChanged();
    }

    partial void OnDetectCushionEdgeChanged(bool value) => SettingsChanged();

    partial void OnManualTableChanged(NormalizedRect? value)
    {
        OnPropertyChanged(nameof(CalibrationText));
        SettingsChanged();
    }

    partial void OnShowLabelsChanged(bool value)
    {
        PushOverlayConfig();
        ScheduleSave();
    }

    partial void OnAnalysisEnabledChanged(bool value)
    {
        PushOverlayConfig();
        if (!value) StatusText = "Paused";
    }

    private void SettingsChanged()
    {
        if (_loading) return;
        if (_bridge is not null) _bridge.Settings = BuildAimSettings();
        ScheduleSave();
    }

    private AimSettings BuildAimSettings() => _config.Aim with
    {
        Team = Team,
        AllowBanks = AllowBanks,
        AllowKicks = AllowKicks,
        AllowCombos = AllowCombos,
        Alternatives = Alternatives,
        FeltTolerance = FeltTolerance,
        StripeThreshold = AutoStripe ? 0 : StripeThreshold,
        BallSize = AutoBallSize ? 0 : BallSizePercent / 100,
        DetectCushionEdge = DetectCushionEdge,
        ManualTable = ManualTable,
    };

    private void LoadAimSettings(AppConfig config)
    {
        var aim = config.Aim;
        Team = aim.Team;
        AllowBanks = aim.AllowBanks;
        AllowKicks = aim.AllowKicks;
        AllowCombos = aim.AllowCombos;
        Alternatives = Math.Clamp(aim.Alternatives, 0, 4);
        FeltTolerance = aim.FeltTolerance;
        AutoStripe = aim.StripeThreshold <= 0;
        if (aim.StripeThreshold > 0) StripeThreshold = aim.StripeThreshold;
        AutoBallSize = aim.BallSize <= 0;
        if (aim.BallSize > 0) BallSizePercent = aim.BallSize * 100;
        DetectCushionEdge = aim.DetectCushionEdge;
        ManualTable = aim.ManualTable;
        ShowLabels = config.ShowBallLabels;
    }

    private void PushOverlayConfig() =>
        _bridge?.Configure(_config.CaptureIntervalMs, _config.CaptureMaxSide, ShowLabels, AnalysisEnabled);

    private void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveConfig()
    {
        _config = _config with { Aim = BuildAimSettings(), ShowBallLabels = ShowLabels };
        try
        {
            _configStore.Save(_config);
        }
        catch (Exception ex)
        {
            Log.Error(Source, $"could not save settings to {_configStore.Path}: {ex.Message}");
        }
    }

    // ---- Analysis results --------------------------------------------------

    private void OnAnalyzed(AnalysisResult result)
    {
        _framesThisSecond++;
        if (_fpsClock.Elapsed >= TimeSpan.FromSeconds(1))
        {
            TimingText = $"{_framesThisSecond / _fpsClock.Elapsed.TotalSeconds:0.0} fps · {result.Elapsed.TotalMilliseconds:0} ms · {result.FrameWidth}×{result.FrameHeight}";
            _framesThisSecond = 0;
            _fpsClock.Restart();
        }

        if (!AnalysisEnabled) return;

        (StatusText, StatusKey) = result.Status switch
        {
            AnalysisStatus.Ready => ("Table still - shot planned", "success"),
            AnalysisStatus.Moving => ("Waiting for the balls to stop", "running"),
            AnalysisStatus.NoBalls => ("Table found, no balls recognised", "cancelled"),
            _ => (result.Message, "pending"),
        };

        if (result.Status == AnalysisStatus.NoTable)
        {
            BestShot = "—";
            BestShotDetail = "";
            BestChance = 0;
            Alternates.Clear();
        }

        var c = result.Counts;
        CountsText = result.Table is null
            ? ""
            : $"whole {c.Solids} · half {c.Stripes} · cue {(c.Cue ? "✓" : "✗")} · 8 {(c.Eight ? "✓" : "✗")}";
        TableText = result.Table is { } table
            ? $"{table.Cushion.Width:0} × {table.Cushion.Height:0} px{(result.ManualTable ? " (manual)" : "")} · ball r {result.BallRadius:0.0} px"
            : "";

        if (result.Status != AnalysisStatus.Ready) return;

        var best = result.Plan.Best;
        BestShot = best?.Description ?? result.Plan.Summary;
        BestShotDetail = best is null ? "" : Describe(best);
        BestChance = best is null || best.Kind is ShotKind.Break or ShotKind.Safety ? 0 : best.Probability * 100;

        Alternates.Clear();
        foreach (var shot in result.Plan.Shots.Skip(1))
        {
            Alternates.Add($"{shot.Description}  ·  {shot.Probability * 100:0}%");
        }
    }

    private static string Describe(Shot shot)
    {
        var kind = shot.Kind switch
        {
            ShotKind.Direct => "direct",
            ShotKind.Bank => "bank",
            ShotKind.Kick => "kick",
            ShotKind.Combo => "combination",
            ShotKind.Break => "break",
            ShotKind.Safety => "safety",
            ShotKind.BallInHand => "ball in hand",
            _ => "",
        };

        if (shot.Kind is ShotKind.Break or ShotKind.Safety) return kind;
        return $"{kind} · cut {shot.CutAngle:0}° · {shot.Probability * 100:0}%{(shot.ScratchRisk ? " · scratch risk" : "")}";
    }

    // ---- Device ------------------------------------------------------------

    [RelayCommand]
    private async Task ToggleScreenAsync()
    {
        if (_screen.State is ScreenState.Running or ScreenState.Starting)
        {
            _screen.Stop();
            return;
        }

        await StartScreenAsync().ConfigureAwait(true);
    }

    private async Task StartScreenAsync()
    {
        try
        {
            await _screen.StartAsync(_config.ScreenBindAddress, FreePort(_config.ScreenPort)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(Source, "screen stream failed", ex);
        }
    }

    [RelayCommand]
    private async Task MountDeveloperImageAsync()
    {
        try
        {
            await _device.MountDeveloperImageAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(Source, "could not mount the DeveloperDiskImage", ex);
        }
    }

    [RelayCommand]
    private async Task EnableDeveloperModeAsync()
    {
        try
        {
            await _device.EnableDeveloperModeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(Source, "could not enable Developer Mode", ex);
        }
    }

    [RelayCommand]
    private void ClearConsole() => Console.Clear();

    private void OnDeviceSnapshot(DeviceSnapshot snapshot) => OnUi(() =>
    {
        var wasConnected = Device.Connected;
        Device.Update(snapshot);

        if (!wasConnected && snapshot.Connected && _config.AutoStartScreen && _screen.State != ScreenState.Unsupported)
        {
            _ = StartScreenAsync();
        }
        else if (wasConnected && !snapshot.Connected)
        {
            _screen.Stop();
            if (FeedRunning) BackToLive();
        }

        UpdateScreenMessage();
    });

    private void OnScreenStateChanged() => OnUi(() =>
    {
        ScreenStatus = _screen.State.ToString().ToLowerInvariant();
        var url = _screen.ViewerUrl;
        if (url != ScreenUrl)
        {
            ScreenUrl = url;
            if (IsLive)
            {
                _bridge?.Reset();
                NavigationRequested?.Invoke(url ?? "about:blank");
            }
        }

        UpdateScreenMessage();
        UpdateTunnelMode();
    });

    private void UpdateScreenMessage()
    {
        if (!IsLive)
        {
            ScreenMessage = "";
            return;
        }

        ScreenMessage = !Device.Connected
            ? "Waiting for an iPhone - or open a screenshot / demo table"
            : _screen.State switch
            {
                ScreenState.Running => "",
                ScreenState.Starting => "Starting the screen stream…",
                ScreenState.Unsupported => $"This device will not mirror - {_screen.UnsupportedReason?.TrimEnd('.')}.",
                ScreenState.Failed => _screen.LastError ?? "Screen stream unavailable - check Developer Mode and the DDI mount",
                _ => "Screen stream stopped",
            };
    }

    private void UpdateTunnelMode()
    {
        TunnelMode = _tunnel.Endpoint is { } endpoint
            ? $"tunnel {endpoint}"
            : _tunnel.UserspaceFallback ? "userspace tunnels" : "";
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// The configured port, or the next free one: a viewer left behind by a
    /// crashed run keeps its port, and waiting for it helps nobody.
    /// </summary>
    private int FreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                if (port != preferred) Log.Warn(Source, $"port {preferred} is taken (an old viewer still running?); using {port}");
                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Taken; try the next.
            }
        }

        return preferred;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public async ValueTask DisposeAsync()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            SaveConfig();
        }

        Console.Dispose();
        await _feed.DisposeAsync().ConfigureAwait(false);
        await _screen.DisposeAsync().ConfigureAwait(false);
        await _device.DisposeAsync().ConfigureAwait(false);
        await _tunnel.DisposeAsync().ConfigureAwait(false);
    }
}

