using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using AIM8.Core;
using AIM8.Core.Vision;
using Microsoft.Web.WebView2.Core;

namespace AIM8.App;

/// <summary>
/// The host end of overlay.js. Frames arrive through a WebView2 shared buffer
/// (no copying through JSON), are analysed on a worker thread, and the result
/// goes back to the page as one JSON message. The page waits for that answer
/// before it captures again, so exactly one frame is ever in flight.
/// </summary>
internal sealed class FrameBridge : IDisposable
{
    /// <summary>Room for a phone screen at the default capture size; grown on request.</summary>
    private const ulong InitialBufferBytes = 900UL * 1800 * 4;

    private readonly CoreWebView2Environment _environment;
    private readonly CoreWebView2 _web;
    private readonly Dispatcher _dispatcher;
    private readonly AimEngine _engine = new();

    private CoreWebView2SharedBuffer? _buffer;
    private byte[] _pixels = [];
    private (int W, int H) _lastSize;
    private bool _analyzing;
    private bool _resetPending;
    private string _config = "{}";

    public FrameBridge(CoreWebView2Environment environment, CoreWebView2 web, Dispatcher dispatcher)
    {
        _environment = environment;
        _web = web;
        _dispatcher = dispatcher;
        _web.WebMessageReceived += OnWebMessage;
    }

    /// <summary>Read by the worker at the start of each frame; replacing it is atomic.</summary>
    public AimSettings Settings
    {
        get => _engine.Settings;
        set => _engine.Settings = value;
    }

    public event Action<AnalysisResult>? Analyzed;

    /// <summary>A table rectangle drawn on the overlay, or null when the drag was cancelled.</summary>
    public event Action<NormalizedRect?>? Calibrated;

    public event Action<string>? Log;

    public void Configure(int intervalMs, int maxSide, bool labels, bool enabled)
    {
        _config = JsonSerializer.Serialize(new { type = "config", interval = intervalMs, maxSide, labels, enabled });
        Post(_config);
    }

    public void SetCalibrating(bool on) => Post(JsonSerializer.Serialize(new { type = "calibrate", on }));

    /// <summary>Swaps the image on the offline page (the screenshot feed).</summary>
    public void ShowImage(string relativeUrl, string label) =>
        Post(JsonSerializer.Serialize(new { type = "image", src = relativeUrl, label }));

    /// <summary>Forget tracked balls and the table, e.g. when the source changes.</summary>
    public void Reset() => _resetPending = true;

    /// <summary>The last frame handed to the analysis, as RGBA; null before the first.</summary>
    public (byte[] Pixels, int Width, int Height)? LastFrame()
    {
        if (_analyzing || _lastSize.W == 0) return null;
        return ((byte[])_pixels.Clone(), _lastSize.W, _lastSize.H);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return;

            switch (type.GetString())
            {
                case "hello":
                    // A new page: its buffer views and our tracking are both stale.
                    _resetPending = true;
                    Post(_config);
                    ShareBuffer(Math.Max(InitialBufferBytes, _buffer?.Size ?? 0));
                    break;

                case "needBuffer":
                    ShareBuffer(root.GetProperty("bytes").GetUInt64());
                    break;

                case "frame":
                    OnFrame(root.GetProperty("seq").GetInt64(), root.GetProperty("w").GetInt32(), root.GetProperty("h").GetInt32());
                    break;

                case "calibrated":
                    if (root.TryGetProperty("cancelled", out _))
                    {
                        Calibrated?.Invoke(null);
                    }
                    else
                    {
                        Calibrated?.Invoke(new NormalizedRect(
                            root.GetProperty("x0").GetDouble(), root.GetProperty("y0").GetDouble(),
                            root.GetProperty("x1").GetDouble(), root.GetProperty("y1").GetDouble()));
                    }

                    break;

                case "log":
                    Log?.Invoke(root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "");
                    break;
            }
        }
    }

    private void ShareBuffer(ulong bytes)
    {
        try
        {
            if (_buffer is null || _buffer.Size < bytes)
            {
                // Round up so a slightly larger frame does not need another one.
                var size = (bytes + (1UL << 20) - 1) & ~((1UL << 20) - 1);
                _buffer?.Dispose();
                _buffer = _environment.CreateSharedBuffer(size);
            }

            _web.PostSharedBufferToScript(_buffer, CoreWebView2SharedBufferAccess.ReadWrite, null);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"could not share the frame buffer with the page: {ex.Message}");
        }
    }

    private void OnFrame(long sequence, int width, int height)
    {
        var bytes = width * height * 4;
        if (_buffer is null || _analyzing || width <= 0 || height <= 0 || (ulong)bytes > _buffer.Size)
        {
            Post("""{"type":"skip"}""");
            return;
        }

        // Copy out at once: the page may reuse the buffer as soon as it has an answer.
        if (_pixels.Length != bytes) _pixels = new byte[bytes];
        Marshal.Copy(_buffer.Buffer, _pixels, 0, bytes);
        _lastSize = (width, height);

        var frame = new Frame(_pixels, width, height);
        var reset = _resetPending;
        _resetPending = false;
        _analyzing = true;

        Task.Run(() =>
            {
                if (reset) _engine.Reset();
                var result = _engine.Process(frame);
                return (result, json: OverlayJson.Serialize(result, sequence));
            })
            .ContinueWith(task => _dispatcher.BeginInvoke(() =>
            {
                _analyzing = false;
                if (task.IsFaulted)
                {
                    Log?.Invoke($"analysis failed: {task.Exception?.GetBaseException().Message}");
                    Post("""{"type":"skip"}""");
                    return;
                }

                Post(task.Result.json);
                Analyzed?.Invoke(task.Result.result);
            }), TaskScheduler.Default);
    }

    private void Post(string json)
    {
        try
        {
            _web.PostWebMessageAsJson(json);
        }
        catch (Exception)
        {
            // The WebView is closing or between pages; the next hello starts over.
        }
    }

    public void Dispose()
    {
        _web.WebMessageReceived -= OnWebMessage;
        _buffer?.Dispose();
        _buffer = null;
    }
}
