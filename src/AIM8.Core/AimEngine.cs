using System.Diagnostics;
using AIM8.Core.Geometry;
using AIM8.Core.Solver;
using AIM8.Core.Vision;

namespace AIM8.Core;

public enum AnalysisStatus
{
    /// <summary>No pool table on screen (menu, lobby, loading).</summary>
    NoTable,

    /// <summary>Table found, nothing on it recognised as a ball.</summary>
    NoBalls,

    /// <summary>Balls are rolling, or the view just changed; waiting for them to stop.</summary>
    Moving,

    /// <summary>Table still, shot planned.</summary>
    Ready,
}

public sealed record BallCounts(int Solids, int Stripes, bool Cue, bool Eight, int Unknown)
{
    public static readonly BallCounts None = new(0, 0, false, false, 0);

    public static BallCounts Of(IReadOnlyList<Ball> balls) => new(
        balls.Count(b => b.Kind == BallKind.Solid),
        balls.Count(b => b.Kind == BallKind.Stripe),
        balls.Any(b => b.Kind == BallKind.Cue),
        balls.Any(b => b.Kind == BallKind.Eight),
        balls.Count(b => b.Kind == BallKind.Unknown));
}

public sealed record AnalysisResult
{
    public required int FrameWidth { get; init; }

    public required int FrameHeight { get; init; }

    public required AnalysisStatus Status { get; init; }

    public string Message { get; init; } = "";

    public TableGeometry? Table { get; init; }

    public bool ManualTable { get; init; }

    public double BallRadius { get; init; }

    public IReadOnlyList<Ball> Balls { get; init; } = [];

    public ShotPlan Plan { get; init; } = ShotPlan.None;

    public Team Team { get; init; }

    public BallCounts Counts { get; init; } = BallCounts.None;

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Frame in, plan out: table, balls, what each ball is, whether the table has
/// settled, and the best shot for the chosen group. Keeps state between frames
/// (tracking, smoothed table and radius), so use one instance per stream and
/// call it from one thread at a time.
/// </summary>
public sealed class AimEngine
{
    private readonly TableDetector _tables = new();
    private readonly BallDetector _detector = new();
    private readonly BallTracker _tracker = new();
    private readonly ShotSolver _solver = new();

    private RectD? _table;
    private double? _radius;
    private int _radiusDisagreements;
    private int _tableMisses;
    private (int W, int H) _frameSize;

    public AimSettings Settings { get; set; } = new();

    public void Reset()
    {
        _tracker.Reset();
        _table = null;
        _radius = null;
        _tableMisses = 0;
    }

    public AnalysisResult Process(Frame frame)
    {
        var clock = Stopwatch.StartNew();
        var settings = Settings;

        if (_frameSize != (frame.Width, frame.Height))
        {
            // Rotation or a new capture scale: nothing from before lines up.
            Reset();
            _frameSize = (frame.Width, frame.Height);
        }

        var detection = _tables.Detect(frame, settings);
        if (detection is null)
        {
            if (++_tableMisses > 3) Reset();
            return new AnalysisResult
            {
                FrameWidth = frame.Width,
                FrameHeight = frame.Height,
                Status = AnalysisStatus.NoTable,
                Message = settings.ManualTable is null ? "Looking for the table…" : "Table colour not found inside the calibrated area",
                Team = settings.Team,
                Elapsed = clock.Elapsed,
            };
        }

        _tableMisses = 0;
        var geometry = detection.Manual ? detection.Geometry : TableGeometry.FromCushion(SmoothTable(detection.Geometry.Cushion));

        var found = _detector.Detect(frame, detection.Felt, geometry, _radius, settings);
        if (found.Radius > 0)
        {
            // Ball size does not change within a game, so one odd frame (a
            // rack, a menu sliding over) must not throw away a good estimate;
            // a new size has to hold for a few frames first.
            if (_radius is not { } previous)
            {
                _radius = found.Radius;
            }
            else if (Math.Abs(previous - found.Radius) < previous * 0.25)
            {
                _radius = previous * 0.8 + found.Radius * 0.2;
                _radiusDisagreements = 0;
            }
            else if (++_radiusDisagreements >= 4)
            {
                _radius = found.Radius;
                _radiusDisagreements = 0;
            }
        }

        var radius = _radius ?? 0;
        var classified = BallClassifier.Classify(frame, found.Balls, radius, settings, found.Occluders);
        var felt = detection.Felt;
        var balls = _tracker.Update(classified, Math.Max(radius, 1), p => IsCovered(frame, felt, p, radius));

        var counts = BallCounts.Of(balls);
        var still = _tracker.StillFrames + 1 >= Math.Max(1, settings.StableFrames);

        var result = new AnalysisResult
        {
            FrameWidth = frame.Width,
            FrameHeight = frame.Height,
            Status = AnalysisStatus.Moving,
            Table = geometry,
            ManualTable = detection.Manual,
            BallRadius = radius,
            Balls = balls,
            Team = settings.Team,
            Counts = counts,
        };

        if (balls.Count == 0)
        {
            return result with { Status = AnalysisStatus.NoBalls, Message = "Table found, no balls recognised", Elapsed = clock.Elapsed };
        }

        if (!still)
        {
            return result with { Message = "Waiting for the balls to stop…", Elapsed = clock.Elapsed };
        }

        var plan = _solver.Solve(geometry, balls, radius, settings, found.Clusters);
        return result with
        {
            Status = AnalysisStatus.Ready,
            Plan = plan,
            Message = plan.Summary,
            Elapsed = clock.Elapsed,
        };
    }

    /// <summary>Mostly not cloth within a ball's radius of <paramref name="centre"/>.</summary>
    private static bool IsCovered(Frame frame, FeltModel felt, Vec2 centre, double radius)
    {
        if (radius <= 0) return false;
        int total = 0, covered = 0;
        var r = radius * 0.7;
        for (var dy = -r; dy <= r; dy += 1.5)
        {
            for (var dx = -r; dx <= r; dx += 1.5)
            {
                if (dx * dx + dy * dy > r * r) continue;
                var x = (int)(centre.X + dx);
                var y = (int)(centre.Y + dy);
                if (!frame.Contains(x, y)) continue;
                total++;
                if (!felt.IsFelt(frame, x, y)) covered++;
            }
        }

        return total > 0 && covered >= 0.6 * total;
    }

    /// <summary>Small jitter in the detected edges is averaged out; a real change replaces the table.</summary>
    private RectD SmoothTable(RectD detected)
    {
        if (_table is not { } previous)
        {
            _table = detected;
            return detected;
        }

        var tolerance = previous.ShortSide * 0.02;
        var close = Math.Abs(previous.Left - detected.Left) < tolerance &&
                    Math.Abs(previous.Top - detected.Top) < tolerance &&
                    Math.Abs(previous.Right - detected.Right) < tolerance &&
                    Math.Abs(previous.Bottom - detected.Bottom) < tolerance;

        if (!close)
        {
            _tracker.Reset();
            _table = detected;
            return detected;
        }

        const double keep = 0.8;
        var smoothed = RectD.FromEdges(
            previous.Left * keep + detected.Left * (1 - keep),
            previous.Top * keep + detected.Top * (1 - keep),
            previous.Right * keep + detected.Right * (1 - keep),
            previous.Bottom * keep + detected.Bottom * (1 - keep));
        _table = smoothed;
        return smoothed;
    }
}
