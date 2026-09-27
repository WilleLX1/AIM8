using AIM8.Core.Geometry;

namespace AIM8.Core.Vision;

/// <summary>What the inside of one ball looks like.</summary>
/// <param name="White">Share of bright, unsaturated pixels (0..1).</param>
/// <param name="OuterWhite">Share of white in the outer ring - where a stripe's caps show.</param>
/// <param name="RimColored">Share of coloured pixels near the outline - a stripe seen cap-on still shows its band there.</param>
/// <param name="Black">Share of very dark pixels.</param>
/// <param name="Colored">Share of clearly coloured pixels.</param>
/// <param name="Hue">Mean hue of the coloured pixels.</param>
/// <param name="Visible">Share of the ball not hidden under the stick.</param>
public sealed record BallAppearance(
    double White,
    double OuterWhite,
    double RimColored,
    double Black,
    double Colored,
    float Hue,
    float Saturation,
    float Value,
    byte R,
    byte G,
    byte B,
    double Visible = 1)
{
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>
    /// How stripe-like the ball is. A solid's white is its compact number
    /// disc, mostly near the middle; a stripe's white is two caps at the edge
    /// of the disc, so white in the outer ring counts extra.
    /// </summary>
    public double StripeScore => White + 0.5 * OuterWhite;
}

public sealed record ClassifiedBall(Vec2 Position, BallKind Kind, int Number, BallAppearance Look, double Fit);

/// <summary>
/// Tells the balls apart from their pixels. The cue ball is white right out
/// to its outline and the 8 nearly all black; a stripe shows a lot of white at
/// the edges either side of its band, while a solid shows only the small
/// number disc. The colour then gives the number within the group.
/// </summary>
public static class BallClassifier
{
    public const double DefaultStripeThreshold = 0.38;

    public static IReadOnlyList<ClassifiedBall> Classify(
        Frame frame, IReadOnlyList<DetectedBall> balls, double radius, AimSettings settings,
        IReadOnlyList<Occluder>? occluders = null)
    {
        var looks = balls.Select(b => Measure(frame, b.Position, radius, occluders)).ToList();
        var n = balls.Count;
        var kinds = new BallKind[n];

        var cue = -1;
        var best = 0.0;
        for (var i = 0; i < n; i++)
        {
            var look = looks[i];
            var score = look.White - look.Colored - look.Black;
            if (look.White >= 0.55 && look.RimColored < 0.15 && score > best)
            {
                best = score;
                cue = i;
            }
        }

        if (cue >= 0) kinds[cue] = BallKind.Cue;

        var eight = -1;
        best = 0;
        for (var i = 0; i < n; i++)
        {
            if (i == cue) continue;
            var look = looks[i];
            if (look.Black >= 0.35 && look.Colored < 0.35 && look.Black > best)
            {
                best = look.Black;
                eight = i;
            }
        }

        if (eight >= 0) kinds[eight] = BallKind.Eight;

        var others = Enumerable.Range(0, n).Where(i => i != cue && i != eight).ToList();
        var threshold = settings.StripeThreshold > 0
            ? settings.StripeThreshold
            : AutoThreshold(others.Select(i => looks[i].StripeScore).ToList());

        foreach (var i in others)
        {
            kinds[i] = looks[i].StripeScore >= threshold ? BallKind.Stripe : BallKind.Solid;
        }

        var families = new int[n];
        foreach (var i in others) families[i] = ColorFamily(looks[i]);

        // Each colour appears once as a solid and once as a stripe. When two
        // balls of one colour landed in the same group, the whiter one is the stripe.
        foreach (var group in others.Where(i => families[i] > 0).GroupBy(i => families[i]))
        {
            var members = group.ToList();
            if (members.Count != 2 || kinds[members[0]] != kinds[members[1]]) continue;
            if (members.Any(i => looks[i].Visible < 0.7)) continue;

            var (lower, higher) = looks[members[0]].StripeScore <= looks[members[1]].StripeScore
                ? (members[0], members[1])
                : (members[1], members[0]);
            kinds[lower] = BallKind.Solid;
            kinds[higher] = BallKind.Stripe;
        }

        // Seven of each at most.
        Rebalance(others, kinds, looks, BallKind.Solid, BallKind.Stripe, mostStripeLikeFirst: true);
        Rebalance(others, kinds, looks, BallKind.Stripe, BallKind.Solid, mostStripeLikeFirst: false);

        var result = new List<ClassifiedBall>(n);
        for (var i = 0; i < n; i++)
        {
            var number = kinds[i] switch
            {
                BallKind.Cue => 0,
                BallKind.Eight => 8,
                BallKind.Solid when families[i] > 0 => families[i],
                BallKind.Stripe when families[i] > 0 => families[i] + 8,
                _ => -1,
            };

            result.Add(new ClassifiedBall(balls[i].Position, kinds[i], number, looks[i], balls[i].Fit));
        }

        return result;
    }

    private static void Rebalance(
        List<int> others, BallKind[] kinds, List<BallAppearance> looks, BallKind from, BallKind to, bool mostStripeLikeFirst)
    {
        var members = others.Where(i => kinds[i] == from).ToList();
        if (members.Count <= 7) return;

        var ordered = mostStripeLikeFirst
            ? members.OrderByDescending(i => looks[i].StripeScore)
            : members.OrderBy(i => looks[i].StripeScore);

        foreach (var i in ordered.Take(members.Count - 7)) kinds[i] = to;
    }

    /// <summary>
    /// Splits solids from stripes at the widest gap in stripe score, when there
    /// is a clear one in a sensible place; otherwise the default.
    /// </summary>
    internal static double AutoThreshold(List<double> scores)
    {
        if (scores.Count < 2) return DefaultStripeThreshold;

        scores.Sort();
        var bestGap = 0.0;
        var split = DefaultStripeThreshold;
        for (var i = 1; i < scores.Count; i++)
        {
            var gap = scores[i] - scores[i - 1];
            var mid = (scores[i] + scores[i - 1]) / 2;
            if (gap >= 0.12 && gap > bestGap && mid is >= 0.22 and <= 0.55)
            {
                bestGap = gap;
                split = mid;
            }
        }

        return split;
    }

    /// <summary>Pool colour order: 1 yellow, 2 blue, 3 red, 4 purple, 5 orange, 6 green, 7 maroon.</summary>
    internal static int ColorFamily(BallAppearance look)
    {
        if (look.Colored < 0.06) return 0;

        var h = look.Hue;
        var v = look.Value;
        if (h >= 345 || h < 12) return v < 0.55 ? 7 : 3;
        if (h < 38) return v < 0.5 ? 7 : 5;
        if (h < 70) return 1;
        if (h < 170) return 6;
        // Game purples run blue (8 Ball Pool's 4 and 12 sit near 245-260).
        if (h < 235) return 2;
        return 4;
    }

    public static BallAppearance Measure(Frame frame, Vec2 centre, double radius, IReadOnlyList<Occluder>? occluders = null)
    {
        // Only the stick lying over this ball matters.
        var covering = occluders?.Where(o => Geo.DistanceToSegment(centre, o.A, o.B) < radius + o.HalfWidth).ToList();
        if (covering is { Count: 0 }) covering = null;

        // Stop just inside the outline, which is anti-aliased against the felt.
        var sample = radius * 0.9;
        var sampleSquared = sample * sample;
        var outerSquared = radius * 0.55 * radius * 0.55;
        var rimSquared = radius * 0.72 * radius * 0.72;
        var x0 = Math.Max(0, (int)Math.Floor(centre.X - sample));
        var x1 = Math.Min(frame.Width - 1, (int)Math.Ceiling(centre.X + sample));
        var y0 = Math.Max(0, (int)Math.Floor(centre.Y - sample));
        var y1 = Math.Min(frame.Height - 1, (int)Math.Ceiling(centre.Y + sample));

        int total = 0, hidden = 0, white = 0, black = 0, colored = 0;
        int outer = 0, outerWhite = 0, rim = 0, rimColored = 0;
        double sin = 0, cos = 0, satSum = 0, valSum = 0;
        long cr = 0, cg = 0, cb = 0, ar = 0, ag = 0, ab = 0;
        var pixels = frame.Pixels;

        for (var y = y0; y <= y1; y++)
        {
            var dy = y + 0.5 - centre.Y;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x + 0.5 - centre.X;
                var d2 = dx * dx + dy * dy;
                if (d2 > sampleSquared) continue;

                if (covering is not null && covering.Any(c => c.Covers(new Vec2(x + 0.5, y + 0.5))))
                {
                    hidden++;
                    continue;
                }

                var o = frame.Offset(x, y);
                byte r = pixels[o], g = pixels[o + 1], b = pixels[o + 2];
                Hsv.FromRgb(r, g, b, out var h, out var s, out var v);
                total++;
                ar += r;
                ag += g;
                ab += b;

                var inOuter = d2 >= outerSquared;
                var inRim = d2 >= rimSquared;
                if (inOuter) outer++;
                if (inRim) rim++;

                // Shading darkens the edge of a sphere, so white is allowed a
                // little dimmer out there.
                var lowSaturation = s < 0.22f;
                if (lowSaturation && v >= (inOuter ? 0.52f : 0.6f))
                {
                    white++;
                    if (inOuter) outerWhite++;
                }
                else if (v < 0.12f || (v < 0.3f && s < 0.45f))
                {
                    // Dark but saturated is a shaded maroon or blue, not the 8.
                    black++;
                }
                else if (s >= 0.3f)
                {
                    colored++;
                    if (inRim) rimColored++;
                    var radians = h * Math.PI / 180;
                    sin += Math.Sin(radians) * s;
                    cos += Math.Cos(radians) * s;
                    satSum += s;
                    valSum += v;
                    cr += r;
                    cg += g;
                    cb += b;
                }
            }
        }

        if (total == 0) return new BallAppearance(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Visible: 0);

        var hue = (float)(Math.Atan2(sin, cos) * 180 / Math.PI);
        if (hue < 0) hue += 360;

        var (mr, mg, mb) = colored > 0
            ? ((byte)(cr / colored), (byte)(cg / colored), (byte)(cb / colored))
            : ((byte)(ar / total), (byte)(ag / total), (byte)(ab / total));

        return new BallAppearance(
            white / (double)total,
            outer > 0 ? outerWhite / (double)outer : 0,
            rim > 0 ? rimColored / (double)rim : 0,
            black / (double)total,
            colored / (double)total,
            hue,
            colored > 0 ? (float)(satSum / colored) : 0,
            colored > 0 ? (float)(valSum / colored) : 0,
            mr, mg, mb,
            total / (double)(total + hidden));
    }
}
