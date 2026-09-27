using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AIM8.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace AIM8.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private FrameBridge? _bridge;
    private bool _webViewReady;
    private string? _pendingUrl;
    private bool _overlayMode;
    private bool _autoOverlayPending = true;
    private GridLength _savedSideWidth;
    private GridLength _savedConsoleHeight;
    private WindowState _savedWindowState;

    /// <summary>
    /// From iBridge: WebCodecs lets a VideoDecoderConfig omit codedWidth and
    /// codedHeight, and the Edge engine behind WebView2 then assumes 1280x720,
    /// so a portrait phone arrives as a stretched crop of its top third. The
    /// real size is in the SPS inside the hvcC the viewer passes as
    /// `description`; fill it in on the way through. A wrong frame here would
    /// also be a wrong table for the analysis.
    /// </summary>
    private const string WebCodecsSizeFix = """
        (function () {
            if (typeof VideoDecoder === 'undefined' || VideoDecoder.__aim8SizeFix) return;
            VideoDecoder.__aim8SizeFix = true;

            function spsSize(description) {
                try {
                    const d = new Uint8Array(description.buffer || description);
                    let i = 22;
                    const arrays = d[i]; i += 1;
                    let sps = null;
                    for (let a = 0; a < arrays; a++) {
                        const nalType = d[i] & 0x3f; i += 1;
                        const count = (d[i] << 8) | d[i + 1]; i += 2;
                        for (let n = 0; n < count; n++) {
                            const len = (d[i] << 8) | d[i + 1]; i += 2;
                            if (nalType === 33 && !sps) sps = d.subarray(i, i + len);
                            i += len;
                        }
                    }
                    if (!sps) return null;

                    const rbsp = [];
                    for (let k = 2; k < sps.length; k++) {
                        if (k >= 4 && sps[k] === 3 && sps[k - 1] === 0 && sps[k - 2] === 0) continue;
                        rbsp.push(sps[k]);
                    }

                    let pos = 0;
                    const u = (n) => { let v = 0; for (let b = 0; b < n; b++) { v = (v << 1) | ((rbsp[pos >> 3] >> (7 - (pos & 7))) & 1); pos++; } return v; };
                    const ue = () => { let z = 0; while (u(1) === 0) z++; return (1 << z) - 1 + (z ? u(z) : 0); };

                    u(4); const maxSub = u(3); u(1);
                    u(8); u(32); u(4); u(43); u(1); u(8);
                    const subP = [], subL = [];
                    for (let k = 0; k < maxSub; k++) { subP.push(u(1)); subL.push(u(1)); }
                    if (maxSub) for (let k = maxSub; k < 8; k++) u(2);
                    for (let k = 0; k < maxSub; k++) {
                        if (subP[k]) { u(8); u(32); u(4); u(43); u(1); }
                        if (subL[k]) u(8);
                    }
                    ue();
                    if (ue() === 3) u(1);
                    const w = ue(), h = ue();
                    return (w > 0 && h > 0 && w < 16384 && h < 16384) ? { w, h } : null;
                } catch (e) {
                    return null;
                }
            }

            const configure = VideoDecoder.prototype.configure;
            VideoDecoder.prototype.configure = function (config) {
                try {
                    if (config && config.description && !config.codedWidth) {
                        const size = spsSize(config.description);
                        if (size) config = Object.assign({}, config, { codedWidth: size.w, codedHeight: size.h });
                    }
                } catch (e) {
                    /* fall through with the original config */
                }
                return configure.call(this, config);
            };
        })();
        """;

    /// <summary>
    /// The viewer keeps its side trays' state in localStorage. Collapse them the
    /// first time so the phone gets the whole pane; the chevrons still work.
    /// </summary>
    private const string CollapseViewerTrays = """
        (function () {
            try {
                for (const key of ['tray-left', 'tray-right']) {
                    if (localStorage.getItem(key) === null) localStorage.setItem(key, 'collapsed');
                }
            } catch (e) {
                /* blocked storage: the viewer copes, so do we */
            }
        })();
        """;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.NavigationRequested += Navigate;
        _vm.Console.LinesAdded += OnConsoleLinesAdded;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public void ReportUnhandled(Exception exception) => _vm.Log.Error("app", $"unhandled: {exception.Message}");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkTitleBar.Apply(this);
        await InitializeWebViewAsync();
        await _vm.InitializeAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIM8", "webview2");
            Directory.CreateDirectory(dataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Mirror.EnsureCoreWebView2Async(environment);

            var web = Mirror.CoreWebView2;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.AreDevToolsEnabled = true;

            // Screenshots and demo tables are served from a local folder under a
            // virtual host, so the canvas they are drawn into stays same-origin
            // and readable.
            Directory.CreateDirectory(_vm.WebFolder);
            File.WriteAllText(Path.Combine(_vm.WebFolder, "offline.html"), ReadResource("AIM8.offline.html"));
            web.SetVirtualHostNameToFolderMapping(
                MainViewModel.OfflineHost, _vm.WebFolder, CoreWebView2HostResourceAccessKind.DenyCors);

            await web.AddScriptToExecuteOnDocumentCreatedAsync(WebCodecsSizeFix);
            await web.AddScriptToExecuteOnDocumentCreatedAsync(CollapseViewerTrays);
            await web.AddScriptToExecuteOnDocumentCreatedAsync(ReadResource("AIM8.overlay.js"));

            _bridge = new FrameBridge(environment, web, Dispatcher);
            _bridge.ExitOverlayRequested += ExitOverlayMode;
            _bridge.Analyzed += OnFrameAnalyzed;
            _vm.AttachBridge(_bridge);

            _webViewReady = true;
            if (_pendingUrl is not null) Navigate(_pendingUrl);
        }
        catch (Exception ex)
        {
            _vm.Log.Error("screen", $"WebView2 is unavailable ({ex.Message}). Install the WebView2 runtime to see the phone.");
        }
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"missing resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Navigate(string url)
    {
        if (!_webViewReady)
        {
            _pendingUrl = url;
            return;
        }

        _pendingUrl = null;
        Mirror.CoreWebView2.Navigate(url);
    }

    private void OnConsoleLinesAdded()
    {
        if (!_vm.Console.AutoScroll) return;
        var last = _vm.Console.Lines.LastOrDefault();
        if (last is not null) ConsoleList.ScrollIntoView(last);
    }

    private void ToggleOverlayMode(object sender, RoutedEventArgs e)
    {
        if (_overlayMode) ExitOverlayMode();
        else if (!_vm.IsLive || _vm.ScreenStatus != "running")
        {
            _autoOverlayPending = true;
            _vm.BackToLiveCommand.Execute(null);
        }
        else EnterOverlayMode();
    }

    private void RotatePhoneLeft(object sender, RoutedEventArgs e) => _bridge?.RotatePhone("left");

    private void RotatePhoneRight(object sender, RoutedEventArgs e) => _bridge?.RotatePhone("right");

    private void OnFrameAnalyzed(AIM8.Core.AnalysisResult result)
    {
        if (!_autoOverlayPending || _overlayMode || !_vm.IsLive || _vm.ScreenStatus != "running") return;
        EnterOverlayMode();
        if (_overlayMode) _autoOverlayPending = false;
    }

    private void EnterOverlayMode()
    {
        if (_overlayMode || !_webViewReady || !_vm.IsLive || _vm.ScreenStatus != "running" ||
            !string.IsNullOrWhiteSpace(_vm.ScreenMessage)) return;

        _savedSideWidth = SideColumn.Width;
        _savedConsoleHeight = ConsoleRow.Height;
        _savedWindowState = WindowState;
        _overlayMode = true;
        _autoOverlayPending = false;

        HeaderBar.Visibility = Visibility.Collapsed;
        PhoneToolbar.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        SideSplitter.Visibility = Visibility.Collapsed;
        ConsoleSplitter.Visibility = Visibility.Collapsed;
        ConsolePanel.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;

        SideColumn.Width = new GridLength(0);
        SideSplitterColumn.Width = new GridLength(0);
        ConsoleRow.Height = new GridLength(0);
        WindowLayout.Margin = new Thickness(0);
        PhonePanel.Padding = new Thickness(0);
        PhonePanel.BorderThickness = new Thickness(0);
        WindowState = WindowState.Maximized;
        _bridge?.SetOverlayMode(true);
    }

    private void ExitOverlayMode()
    {
        if (!_overlayMode) return;
        _overlayMode = false;

        SideColumn.Width = _savedSideWidth;
        SideSplitterColumn.Width = new GridLength(12);
        ConsoleRow.Height = _savedConsoleHeight;
        WindowLayout.Margin = new Thickness(12);
        PhonePanel.Padding = new Thickness(12);
        PhonePanel.BorderThickness = new Thickness(1);
        WindowState = _savedWindowState;

        HeaderBar.Visibility = Visibility.Visible;
        PhoneToolbar.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Visible;
        SideSplitter.Visibility = Visibility.Visible;
        ConsoleSplitter.Visibility = Visibility.Visible;
        ConsolePanel.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;
        _bridge?.SetOverlayMode(false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ScreenMessage) &&
            !string.IsNullOrWhiteSpace(_vm.ScreenMessage))
        {
            ExitOverlayMode();
            _autoOverlayPending = true;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _vm.Console.LinesAdded -= OnConsoleLinesAdded;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        if (_bridge is not null)
        {
            _bridge.ExitOverlayRequested -= ExitOverlayMode;
            _bridge.Analyzed -= OnFrameAnalyzed;
        }
        _bridge?.Dispose();
        await _vm.DisposeAsync();
    }
}
