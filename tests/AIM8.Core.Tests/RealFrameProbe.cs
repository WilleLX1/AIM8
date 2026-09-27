using AIM8.Core.Vision;
using Xunit.Abstractions;

namespace AIM8.Core.Tests;

/// <summary>
/// Runs the pipeline on a real capture and prints what it saw. Point
/// AIM8_FRAME at a raw file (int32 width, int32 height, RGBA bytes); without it
/// this does nothing. Frames saved with the app's "Save frame" button can be
/// converted with the one-liner in README.md.
/// </summary>
public sealed class RealFrameProbe(ITestOutputHelper output)
{
    [Fact]
    public void Probe()
    {
        var path = Environment.GetEnvironmentVariable("AIM8_FRAME");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        var w = BitConverter.ToInt32(bytes, 0);
        var h = BitConverter.ToInt32(bytes, 4);
        var frame = new Frame(bytes[8..], w, h);
        var settings = new AimSettings();

        var table = new TableDetector().Detect(frame, settings);
        output.WriteLine($"frame {w}x{h}; table {table?.Geometry.Cushion} {table?.Felt}");
        if (table is null) return;

        var detection = new BallDetector().Detect(frame, table.Felt, table.Geometry, null, settings);
        output.WriteLine($"radius {detection.Radius:0.0}, {detection.Balls.Count} balls, stick {(detection.Occluders.Count > 0 ? "yes" : "no")}, " +
                         $"clusters {string.Join(", ", detection.Clusters.Select(k => $"{k.Balls:0.0} balls at {k.Centre}"))}");

        var engine = new AimEngine { Settings = settings };
        engine.Process(frame);
        var result = engine.Process(frame);
        output.WriteLine($"plan: {result.Status} - {result.Plan.Best?.Kind} {result.Plan.Best?.Description} {result.Plan.Best?.Probability:P0}");
        foreach (var b in BallClassifier.Classify(frame, detection.Balls, detection.Radius, settings, detection.Occluders).OrderBy(b => b.Position.Y))
        {
            var l = b.Look;
            output.WriteLine($"  {b.Position} {b.Kind,-7} #{b.Number,-3} white {l.White:0.00} outer {l.OuterWhite:0.00} " +
                             $"rim {l.RimColored:0.00} black {l.Black:0.00} col {l.Colored:0.00} hue {l.Hue:0} v {l.Value:0.00} " +
                             $"score {l.StripeScore:0.00} fit {b.Fit:0.00}");
        }
    }
}
