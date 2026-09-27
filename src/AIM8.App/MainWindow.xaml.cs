using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using AIM8.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace AIM8.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private FrameBridge? _bridge;
    private bool _webViewReady;
    private string? _pendingUrl;

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

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _vm.Console.LinesAdded -= OnConsoleLinesAdded;
        _bridge?.Dispose();
        await _vm.DisposeAsync();
    }
}
