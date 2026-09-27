using AIM8.Core.Geometry;

namespace AIM8.Core.Vision;

/// <summary>
/// A conservative fallback for a cue ball whose circular edge was joined to
/// the cue stick or the game's aiming line. Runs only when the normal ball
/// detector did not find a cue ball.
/// </summary>
internal static class CueBallFinder
{
    public static ClassifiedBall? Find(
        Frame frame, TableGeometry table, double radius,
        IReadOnlyList<DetectedBall> detected, IReadOnlyList<Occluder> occluders)
    {
        if (radius < 3) return null;

        var bounds = table.Cushion.Inflate(-0.7 * radius);
        var x0 = Math.Clamp((int)Math.Ceiling(bounds.Left), 0, frame.Width - 1);
        var y0 = Math.Clamp((int)Math.Ceiling(bounds.Top), 0, frame.Height - 1);
        var x1 = Math.Clamp((int)Math.Floor(bounds.Right), x0, frame.Width - 1);
        var y1 = Math.Clamp((int)Math.Floor(bounds.Bottom), y0, frame.Height - 1);
        var width = x1 - x0 + 1;
        var height = y1 - y0 + 1;
        if (width < 3 * radius || height < 3 * radius) return null;

        // Integral image: bright, neutral pixels in a square around each
        // candidate can be counted in constant time. A stripe's white cap or
        // a thin aiming ring can pass this prefilter, but not the full-disc
        // colour check below.
        var stride = width + 1;
        var white = new int[stride * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = 0;
            for (var x = 0; x < width; x++)
            {
                var pixel = frame.Offset(x0 + x, y0 + y);
                var red = frame.Pixels[pixel];
                var green = frame.Pixels[pixel + 1];
                var blue = frame.Pixels[pixel + 2];
                var max = Math.Max(red, Math.Max(green, blue));
                var min = Math.Min(red, Math.Min(green, blue));
                if (max >= 145 && max - min <= 0.22 * max) row++;
                white[(y + 1) * stride + x + 1] = white[y * stride + x + 1] + row;
            }
        }

        var step = Math.Max(2, (int)Math.Floor(radius / 3));
        var half = Math.Max(2, (int)Math.Round(radius * 0.45));
        ClassifiedBall? best = null;
        var bestScore = double.NegativeInfinity;

        for (var y = y0 + half; y <= y1 - half; y += step)
        {
            for (var x = x0 + half; x <= x1 - half; x += step)
            {
                var position = new Vec2(x + 0.5, y + 0.5);
                if (detected.Any(ball => Vec2.Distance(ball.Position, position) < 1.35 * radius)) continue;

                var lx = x - half - x0;
                var rx = x + half - x0 + 1;
                var ty = y - half - y0;
                var by = y + half - y0 + 1;
                var bright = white[by * stride + rx] - white[ty * stride + rx] -
                             white[by * stride + lx] + white[ty * stride + lx];
                if (bright < 0.58 * (rx - lx) * (by - ty)) continue;

                var look = BallClassifier.Measure(frame, position, radius, occluders);
                if (look.White < 0.60 || look.OuterWhite < 0.45 ||
                    look.Colored > 0.16 || look.Black > 0.18 ||
                    look.RimColored > 0.14 || look.Visible < 0.5) continue;

                var score = look.White + 0.3 * look.OuterWhite - look.Colored - look.Black;
                if (score <= bestScore) continue;
                bestScore = score;
                best = new ClassifiedBall(position, BallKind.Cue, 0, look, 0.8);
            }
        }

        return best;
    }
}
