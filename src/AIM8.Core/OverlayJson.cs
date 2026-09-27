using System.Text;
using System.Text.Json;
using AIM8.Core.Geometry;
using AIM8.Core.Solver;

namespace AIM8.Core;

/// <summary>
/// The message the in-page overlay draws from. Coordinates are frame pixels of
/// the captured image; the page scales them onto the mirror.
/// </summary>
public static class OverlayJson
{
    public static string Serialize(AnalysisResult result, long sequence)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("type", "result");
            w.WriteNumber("seq", sequence);
            w.WriteNumber("w", result.FrameWidth);
            w.WriteNumber("h", result.FrameHeight);
            w.WriteString("status", result.Status switch
            {
                AnalysisStatus.NoTable => "noTable",
                AnalysisStatus.NoBalls => "noBalls",
                AnalysisStatus.Moving => "moving",
                _ => "ready",
            });
            w.WriteString("message", result.Message);
            w.WriteString("team", result.Team.ToString().ToLowerInvariant());
            w.WriteBoolean("onEight", result.Plan.OnEight);
            w.WriteNumber("r", Round(result.BallRadius));
            w.WriteNumber("ms", Round(result.Elapsed.TotalMilliseconds));

            if (result.Table is { } table)
            {
                w.WriteStartObject("table");
                w.WriteNumber("x", Round(table.Cushion.X));
                w.WriteNumber("y", Round(table.Cushion.Y));
                w.WriteNumber("w", Round(table.Cushion.Width));
                w.WriteNumber("h", Round(table.Cushion.Height));
                w.WriteBoolean("manual", result.ManualTable);
                w.WriteStartArray("pockets");
                foreach (var pocket in table.Pockets) Point(w, pocket.Position);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("table");
            }

            var targets = result.Plan.Targets.Select(b => b.Id).ToHashSet();
            w.WriteStartArray("balls");
            foreach (var ball in result.Balls)
            {
                w.WriteStartObject();
                w.WriteNumber("id", ball.Id);
                w.WriteNumber("x", Round(ball.Position.X));
                w.WriteNumber("y", Round(ball.Position.Y));
                w.WriteString("kind", ball.Kind.ToString().ToLowerInvariant());
                w.WriteNumber("n", ball.Number);
                w.WriteString("label", ball.Label);
                w.WriteString("color", ball.Color);
                w.WriteBoolean("target", targets.Contains(ball.Id));
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("shots");
            foreach (var shot in result.Plan.Shots) WriteShot(w, shot);
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteShot(Utf8JsonWriter w, Shot shot)
    {
        w.WriteStartObject();
        w.WriteString("kind", shot.Kind.ToString().ToLowerInvariant());
        w.WriteString("desc", shot.Description);
        w.WriteNumber("p", Math.Round(shot.Probability, 3));
        w.WriteNumber("cut", Math.Round(shot.CutAngle));
        w.WriteNumber("target", shot.Target.Id);
        w.WriteNumber("pocket", shot.Pocket?.Index ?? -1);
        w.WriteBoolean("scratch", shot.ScratchRisk);
        w.WritePropertyName("ghost");
        Point(w, shot.Ghost);
        Path(w, "cue", shot.CuePath);
        Path(w, "obj", shot.ObjectPath);
        Path(w, "first", shot.FirstPath);
        Path(w, "after", shot.CueAfter);

        if (shot.CuePlacement is { } place)
        {
            w.WritePropertyName("place");
            Point(w, place);
        }
        else
        {
            w.WriteNull("place");
        }

        w.WriteEndObject();
    }

    private static void Path(Utf8JsonWriter w, string name, IReadOnlyList<Vec2> points)
    {
        w.WriteStartArray(name);
        foreach (var p in points) Point(w, p);
        w.WriteEndArray();
    }

    private static void Point(Utf8JsonWriter w, Vec2 p)
    {
        w.WriteStartArray();
        w.WriteNumberValue(Round(p.X));
        w.WriteNumberValue(Round(p.Y));
        w.WriteEndArray();
    }

    private static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 1) : 0;
}
