using AIM8.Core.Geometry;

namespace AIM8.Core.Vision;

/// <param name="Fit">Share of probe rays that found a clean circular edge (0..1).</param>
public sealed record DetectedBall(Vec2 Position, double Fit);

/// <summary>Something long and straight lying over the table - the cue stick.</summary>
public sealed record Occluder(Vec2 A, Vec2 B, double HalfWidth)
{
    public bool Covers(Vec2 p) => Geo.DistanceToSegment(p, A, B) <= HalfWidth;
}

public sealed record BallDetection(IReadOnlyList<DetectedBall> Balls, double Radius, int Candidates)
{
    public static readonly BallDetection Empty = new([], 0, 0);

    /// <summary>Where the stick lies; its pixels say nothing about the ball underneath.</summary>
    public IReadOnlyList<Occluder> Occluders { get; init; } = [];

    /// <summary>Groups of touching balls too tight to split - in practice, the rack.</summary>
    public IReadOnlyList<BallCluster> Clusters { get; init; } = [];
}

/// <param name="Balls">Area in units of one ball.</param>
/// <param name="Outline">A sample of the blob's edge pixels.</param>
public sealed record BallCluster(Vec2 Centre, double Balls, IReadOnlyList<Vec2> Outline);

/// <summary>
/// Finds balls as round non-felt blobs on the table.
///
/// Everything that is not felt is "object". The Euclidean distance transform
/// of that mask peaks at the centre of each ball with a value of about one
/// radius, and keeps separate peaks for touching balls because the outlines
/// pinch in where they meet. The cue stick, the game's aim line and ghost
/// circle are thin, straight or hollow, and fail the shape tests that follow.
/// </summary>
public sealed class BallDetector
{
    private const int Rays = 24;

    private double[] _field = [];
    private double[] _line = [];
    private double[] _out = [];
    private int[] _v = [];
    private double[] _z = [];
    private bool[] _object = [];
    private int[] _label = [];
    private int[] _queue = [];

    public BallDetection Detect(Frame frame, FeltModel felt, TableGeometry table, double? radiusHint, AimSettings settings)
    {
        var cushion = table.Cushion;
        var x0 = Math.Clamp((int)Math.Floor(cushion.Left), 0, frame.Width - 1);
        var y0 = Math.Clamp((int)Math.Floor(cushion.Top), 0, frame.Height - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(cushion.Right), x0 + 1, frame.Width);
        var y1 = Math.Clamp((int)Math.Ceiling(cushion.Bottom), y0 + 1, frame.Height);
        var w = x1 - x0;
        var h = y1 - y0;
        if (w < 24 || h < 24) return BallDetection.Empty;

        // One pixel of background around the region: whatever lies beyond the
        // cushion nose counts as felt, so a ball against the cushion still
        // measures one radius.
        var pw = w + 2;
        var ph = h + 2;
        var shortSide = cushion.ShortSide;
        var expectedRadius = radiusHint ?? (settings.BallSize > 0 ? settings.BallSize * shortSide : shortSide / 32);
        var dt = BuildDistanceField(frame, felt, x0, y0, w, h, pw, ph,
            maxHole: (int)(0.05 * expectedRadius * expectedRadius),
            closeRadius: Math.Max(1.0, 0.075 * expectedRadius));

        var minPeak = Math.Max(2.5, shortSide / 110);

        var peaks = new List<Peak>();
        for (var y = 1; y < ph - 1; y++)
        {
            var row = y * pw;
            for (var x = 1; x < pw - 1; x++)
            {
                var v = dt[row + x];
                if (v < minPeak) continue;
                if (dt[row + x - 1] > v || dt[row + x + 1] > v ||
                    dt[row - pw + x - 1] > v || dt[row - pw + x] > v || dt[row - pw + x + 1] > v ||
                    dt[row + pw + x - 1] > v || dt[row + pw + x] > v || dt[row + pw + x + 1] > v)
                {
                    continue;
                }

                // Pixel centres sit at +0.5; region coordinates are offset by the padding.
                peaks.Add(new Peak(new Vec2(x0 + x - 1 + 0.5, y0 + y - 1 + 0.5), v));
            }
        }

        peaks.Sort((a, b) => b.Value.CompareTo(a.Value));

        var ctx = new Field(dt, pw, ph, x0, y0, _object);

        var radius = EstimateRadius(peaks, ctx, table, radiusHint, settings);
        if (radius <= 0) return new BallDetection([], 0, peaks.Count);

        var accepted = new List<Peak>();
        foreach (var peak in peaks)
        {
            if (peak.Value < 0.7 * radius || peak.Value > 1.5 * radius) continue;
            if (accepted.Any(a => Vec2.Distance(a.Position, peak.Position) < 1.6 * radius)) continue;
            accepted.Add(peak);
        }

        accepted = RejectRidges(accepted, ctx, radius);

        var balls = new List<DetectedBall>();
        var bounds = cushion.Inflate(-0.6 * radius);
        foreach (var peak in accepted)
        {
            var result = ProbeShape(peak.Position, accepted, ctx, radius);
            if (result is null) continue;

            var (centre, fit) = result.Value;
            if (!bounds.Contains(centre)) continue;
            balls.Add(new DetectedBall(centre, fit));
        }

        if (balls.Count > 16)
        {
            balls = balls.OrderByDescending(b => b.Fit).Take(16).ToList();
        }

        var stick = FindStick(dt, pw, ph, x0, y0, ctx, radius, balls);
        return new BallDetection(balls, radius, peaks.Count)
        {
            Occluders = stick is null ? [] : [stick],
            Clusters = FindClusters(pw, ph, x0, y0, radius),
        };
    }

    /// <summary>
    /// Compact blobs far bigger than a ball. At phone resolution the gaps in a
    /// fresh rack are a pixel wide or less, so the fifteen balls read as one
    /// blob that cannot be split - but it is still plainly the rack.
    /// </summary>
    private List<BallCluster> FindClusters(int pw, int ph, int x0, int y0, double radius)
    {
        var size = pw * ph;
        if (_label.Length < size)
        {
            _label = new int[size];
            _queue = new int[size];
        }

        Array.Clear(_label, 0, size);
        var clusters = new List<BallCluster>();
        var ballArea = Math.PI * radius * radius;
        var next = 0;

        for (var start = 0; start < size; start++)
        {
            if (!_object[start] || _label[start] != 0) continue;

            next++;
            var head = 0;
            var tail = 0;
            _queue[tail++] = start;
            _label[start] = next;
            int minX = pw, minY = ph, maxX = 0, maxY = 0;

            while (head < tail)
            {
                var i = _queue[head++];
                var x = i % pw;
                var y = i / pw;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);

                if (x > 0) Visit(i - 1);
                if (x < pw - 1) Visit(i + 1);
                if (y > 0) Visit(i - pw);
                if (y < ph - 1) Visit(i + pw);
            }

            if (tail < 8 * ballArea) continue;

            // A rack is compact; the cue stick is as big but long and thin.
            var boxW = maxX - minX + 1;
            var boxH = maxY - minY + 1;
            var fill = tail / (double)(boxW * boxH);
            var aspect = Math.Max(boxW, boxH) / (double)Math.Min(boxW, boxH);
            if (fill < 0.35 || aspect > 2.5) continue;

            double sx = 0, sy = 0;
            var outline = new List<Vec2>();
            for (var k = 0; k < tail; k++)
            {
                var i = _queue[k];
                var x = i % pw;
                var y = i / pw;
                sx += x;
                sy += y;
                var edge = !_object[i - 1] || !_object[i + 1] || !_object[i - pw] || !_object[i + pw];
                if (edge && k % 3 == 0) outline.Add(new Vec2(x0 + x - 1 + 0.5, y0 + y - 1 + 0.5));
            }

            clusters.Add(new BallCluster(
                new Vec2(x0 + sx / tail - 1 + 0.5, y0 + sy / tail - 1 + 0.5), tail / ballArea, outline));

            void Visit(int j)
            {
                if (!_object[j] || _label[j] != 0) return;
                _label[j] = next;
                _queue[tail++] = j;
            }
        }

        return clusters;
    }

    /// <summary>
    /// The cue stick is a long straight crest in the distance field: along it
    /// the field falls away steeply on both sides, which a ball's cone-shaped
    /// field does not do anywhere but its centre. The stick tapers, so the crest
    /// climbs steadily and has hardly any local maxima; crest points are used
    /// instead, and the dominant straight chain of them (RANSAC) is the stick.
    /// </summary>
    private static Occluder? FindStick(double[] dt, int pw, int ph, int x0, int y0, Field field, double radius, List<DetectedBall> balls)
    {
        var low = Math.Max(2, 0.18 * radius);
        var high = 0.75 * radius;
        var spacing = 0.4 * radius;
        var cell = Math.Max(1, spacing);
        var grid = new Dictionary<(int, int), Peak>();

        for (var y = 1; y < ph - 1; y++)
        {
            var row = y * pw;
            for (var x = 1; x < pw - 1; x++)
            {
                var v = dt[row + x];
                if (v < low || v >= high) continue;

                const double drop = 0.4;
                var crest =
                    (v - dt[row + x - 1] >= drop && v - dt[row + x + 1] >= drop) ||
                    (v - dt[row - pw + x] >= drop && v - dt[row + pw + x] >= drop) ||
                    (v - dt[row - pw + x - 1] >= drop && v - dt[row + pw + x + 1] >= drop) ||
                    (v - dt[row - pw + x + 1] >= drop && v - dt[row + pw + x - 1] >= drop);
                if (!crest) continue;

                // One crest point per cell keeps the RANSAC input small.
                var key = ((int)(x / cell), (int)(y / cell));
                if (!grid.TryGetValue(key, out var existing) || existing.Value < v)
                {
                    grid[key] = new Peak(new Vec2(x0 + x - 1 + 0.5, y0 + y - 1 + 0.5), v);
                }
            }
        }

        // The discrete field inside a ball is bumpy enough to pass the crest
        // test here and there; those points only produce false lines.
        var points = grid.Values
            .Where(p => balls.All(b => Vec2.Distance(b.Position, p.Position) > 1.1 * radius))
            .ToList();
        if (points.Count < 6) return null;

        // Deterministic, so a still frame always gives the same answer.
        var random = new Random(8);
        var tolerance = 0.2 * radius;
        List<Peak>? best = null;

        for (var iteration = 0; iteration < 250; iteration++)
        {
            var a = points[random.Next(points.Count)].Position;
            var b = points[random.Next(points.Count)].Position;
            if (Vec2.Distance(a, b) < 3 * radius) continue;

            var dir = (b - a).Normalized();
            var inliers = points.Where(p => Math.Abs((p.Position - a).Cross(dir)) <= tolerance).ToList();
            // A ball under the stick leaves a gap of about one diameter in the chain.
            var chain = DenseChain(inliers, a, dir, maxGap: 3.6 * radius);
            if (best is null || chain.Count > best.Count) best = chain;
        }
        if (best is null || best.Count < 6) return null;

        // Least-squares direction of the inliers, then their extent along it.
        var centre = new Vec2(best.Average(p => p.Position.X), best.Average(p => p.Position.Y));
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in best)
        {
            var d = p.Position - centre;
            sxx += d.X * d.X;
            sxy += d.X * d.Y;
            syy += d.Y * d.Y;
        }

        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var axis = Vec2.FromAngle(angle);
        var along = best.Select(p => (p.Position - centre).Dot(axis)).ToList();
        var length = along.Max() - along.Min();
        if (length < 8 * radius) return null;

        var start = centre + axis * (along.Min() - 0.3 * radius);
        var end = centre + axis * (along.Max() + 0.3 * radius);

        // A stick is solid all the way along; a line through scattered specks is not.
        var samples = (int)(Vec2.Distance(start, end) / 2);
        var solid = Enumerable.Range(0, samples).Count(i => field.IsObject(start + (end - start) * (i / (double)samples)));
        if (solid < 0.85 * samples) return null;

        var widths = best.Select(p => p.Value).OrderBy(v => v).ToList();
        return new Occluder(start, end, widths[(int)(widths.Count * 0.9)] + 1.5);
    }

    /// <summary>The longest run of points along a line with no gap wider than <paramref name="maxGap"/>.</summary>
    private static List<Peak> DenseChain(List<Peak> inliers, Vec2 origin, Vec2 dir, double maxGap)
    {
        var sorted = inliers.OrderBy(p => (p.Position - origin).Dot(dir)).ToList();
        List<Peak> best = [];
        var current = new List<Peak>();
        double? previous = null;
        foreach (var peak in sorted)
        {
            var t = (peak.Position - origin).Dot(dir);
            if (previous is { } last && t - last > maxGap)
            {
                if (current.Count > best.Count) best = current;
                current = [];
            }

            current.Add(peak);
            previous = t;
        }

        return current.Count > best.Count ? current : best;
    }

    internal readonly record struct Peak(Vec2 Position, double Value);

    /// <summary>Distance field over the padded region, in pixels.</summary>
    private double[] BuildDistanceField(Frame frame, FeltModel felt, int x0, int y0, int w, int h, int pw, int ph, int maxHole, double closeRadius)
    {
        var size = pw * ph;
        if (_field.Length < size)
        {
            _field = new double[size];
            _object = new bool[size];
        }

        var longest = Math.Max(pw, ph);
        if (_line.Length < longest)
        {
            _line = new double[longest];
            _out = new double[longest];
            _v = new int[longest];
            _z = new double[longest + 1];
        }

        const double far = 1e20;
        Array.Clear(_field, 0, size);
        Array.Clear(_object, 0, size);

        var pixels = frame.Pixels;
        for (var y = 0; y < h; y++)
        {
            var o = frame.Offset(x0, y0 + y);
            var row = (y + 1) * pw + 1;
            for (var x = 0; x < w; x++, o += 4)
            {
                if (felt.IsFelt(pixels[o], pixels[o + 1], pixels[o + 2])) continue;
                _object[row + x] = true;
                _field[row + x] = far;
            }
        }

        FillPinholes(pw, ph, maxHole, far);

        if (closeRadius <= 0)
        {
            Distance(pw, ph);
            return _field;
        }

        // Morphological closing: grow the object by closeRadius, then measure
        // distance to what is left of the felt and take closeRadius off again.
        // Thin felt-coloured rings inside a ball (the halo of a highlight on a
        // ball the colour of the cloth) close up; the gap between three racked
        // balls is wider than the closing and stays open.
        for (var i = 0; i < size; i++) _field[i] = _object[i] ? 0 : far;
        Distance(pw, ph);

        for (var y = 0; y < ph; y++)
        {
            for (var x = 0; x < pw; x++)
            {
                var i = y * pw + x;
                var border = x == 0 || y == 0 || x == pw - 1 || y == ph - 1;
                _field[i] = !border && _field[i] <= closeRadius ? far : 0;
            }
        }

        Distance(pw, ph);

        for (var i = 0; i < size; i++)
        {
            var d = Math.Max(0, _field[i] - closeRadius);
            _field[i] = d;
            _object[i] = d > 0;
        }

        return _field;
    }

    /// <summary>
    /// Felzenszwalb &amp; Huttenlocher: exact Euclidean distance transform as two
    /// 1-D passes, in place. Input: 0 on background, huge on object.
    /// </summary>
    private void Distance(int pw, int ph)
    {
        for (var y = 0; y < ph; y++)
        {
            var row = y * pw;
            for (var x = 0; x < pw; x++) _line[x] = _field[row + x];
            Transform(_line, pw);
            for (var x = 0; x < pw; x++) _field[row + x] = _out[x];
        }

        for (var x = 0; x < pw; x++)
        {
            for (var y = 0; y < ph; y++) _line[y] = _field[y * pw + x];
            Transform(_line, ph);
            for (var y = 0; y < ph; y++) _field[y * pw + x] = Math.Sqrt(_out[y]);
        }
    }

    /// <summary>
    /// Felt-coloured specks enclosed by a ball - the anti-aliased rim of the
    /// number disc on a ball the colour of the cloth - would make the distance
    /// field collapse inside it. Tiny enclosed felt regions become object. The
    /// gap between three racked balls is several times larger and survives.
    /// </summary>
    private void FillPinholes(int pw, int ph, int maxHole, double far)
    {
        if (maxHole < 1) return;

        var size = pw * ph;
        if (_label.Length < size)
        {
            _label = new int[size];
            _queue = new int[size];
        }

        Array.Clear(_label, 0, size);
        var next = 0;

        for (var start = 0; start < size; start++)
        {
            if (_object[start] || _label[start] != 0) continue;

            next++;
            var head = 0;
            var tail = 0;
            var touchesBorder = false;
            _queue[tail++] = start;
            _label[start] = next;

            while (head < tail)
            {
                var i = _queue[head++];
                var x = i % pw;
                var y = i / pw;
                if (x == 0 || y == 0 || x == pw - 1 || y == ph - 1) touchesBorder = true;

                if (x > 0) Visit(i - 1);
                if (x < pw - 1) Visit(i + 1);
                if (y > 0) Visit(i - pw);
                if (y < ph - 1) Visit(i + pw);
            }

            if (touchesBorder || tail > maxHole) continue;

            for (var k = 0; k < tail; k++)
            {
                _object[_queue[k]] = true;
                _field[_queue[k]] = far;
            }

            void Visit(int j)
            {
                if (_object[j] || _label[j] != 0) return;
                _label[j] = next;
                _queue[tail++] = j;
            }
        }
    }

    private void Transform(double[] f, int n)
    {
        var k = 0;
        _v[0] = 0;
        _z[0] = double.NegativeInfinity;
        _z[1] = double.PositiveInfinity;

        for (var q = 1; q < n; q++)
        {
            var s = Intersection(f, q, _v[k]);

            // z[0] is -infinity, so this always stops at k == 0.
            while (s <= _z[k])
            {
                k--;
                s = Intersection(f, q, _v[k]);
            }

            k++;
            _v[k] = q;
            _z[k] = s;
            _z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (_z[k + 1] < q) k++;
            var d = q - _v[k];
            _out[q] = (double)d * d + f[_v[k]];
        }
    }

    private static double Intersection(double[] f, int q, int p) =>
        (f[q] + (double)q * q - (f[p] + (double)p * p)) / (2.0 * q - 2.0 * p);

    /// <summary>
    /// Ball radius in pixels: the most common height among peaks that look
    /// round at their own scale, preferring the larger cluster when two are
    /// about as common (partly hidden balls give low peaks, so the true size is
    /// the tall one). The roundness check keeps the cue stick - a long ridge of
    /// peaks - from outvoting the balls.
    /// </summary>
    private static double EstimateRadius(List<Peak> peaks, Field field, TableGeometry table, double? hint, AimSettings settings)
    {
        var shortSide = table.Cushion.ShortSide;
        var min = shortSide / 70;
        var max = shortSide / 9;

        if (settings.BallSize > 0) return Math.Clamp(settings.BallSize * shortSide, min, max);

        var values = new List<double>();
        var kept = new List<Peak>();
        foreach (var peak in peaks)
        {
            if (peak.Value < min * 0.7 || peak.Value > max * 1.5) continue;
            if (kept.Any(k => Vec2.Distance(k.Position, peak.Position) < 1.2 * k.Value)) continue;
            kept.Add(peak);

            // A pocket mouth cut into the corner of the region is a small round
            // blob of its own; balls near pockets simply do not get a vote.
            if (table.Pockets.Any(p => Vec2.Distance(p.Position, peak.Position) < 3.5 * peak.Value)) continue;
            if (Roundness(peak, field) >= 0.55) values.Add(peak.Value);
        }

        if (values.Count == 0) return hint ?? 0;

        // Votes are weighed by how plausible the size is: near the size seen in
        // earlier frames when there is one, otherwise near a typical mobile
        // table (ball radius about 1/32 of the short side). Without this a full
        // rack - one merged blob at phone resolution - leaves the few round
        // peaks to specks, and they outvote the lone cue ball.
        var prior = hint ?? shortSide / 32;
        var spread = hint is null ? 0.3 : 0.15;
        double Score(double v)
        {
            var count = values.Count(u => u >= 0.85 * v && u <= 1.08 * v);
            var log = Math.Log(v / prior);
            return count * Math.Exp(-log * log / (2 * spread * spread));
        }

        var scores = values.Select(Score).ToArray();
        var best = scores.Max();

        // Of the well-supported sizes, the largest: partly hidden balls give low peaks.
        var mode = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            if (scores[i] >= 0.6 * best && values[i] > mode) mode = values[i];
        }

        var cluster = values.Where(u => u >= 0.85 * mode && u <= 1.08 * mode).ToList();
        if (hint is { } previous && cluster.Count < 3) return previous;

        // The transform measures to the nearest felt pixel centre, half a pixel
        // short of the anti-aliased outline.
        return Math.Clamp(TableDetector.Median(cluster) + 0.5, min, max);
    }

    /// <summary>Share of 16 rays that leave the blob at roughly the peak's own height.</summary>
    private static double Roundness(Peak peak, Field field)
    {
        const int rays = 16;
        var scale = peak.Value;
        var edges = 0;
        for (var k = 0; k < rays; k++)
        {
            var dir = Vec2.FromAngle(2 * Math.PI * k / rays);
            // A straight ridge of half-width v is left within 1.3v by under half the rays.
            for (var t = 0.6 * scale; t <= 1.3 * scale + 0.5; t += 0.5)
            {
                if (field.IsObject(peak.Position + dir * t)) continue;
                edges++;
                break;
            }
        }

        return edges / (double)rays;
    }

    /// <summary>Read access to the distance field and object mask in frame coordinates.</summary>
    private readonly struct Field(double[] dt, int pw, int ph, int x0, int y0, bool[] mask)
    {
        public double DistanceAt(Vec2 p)
        {
            var x = (int)Math.Floor(p.X) - x0 + 1;
            var y = (int)Math.Floor(p.Y) - y0 + 1;
            return (uint)x < (uint)pw && (uint)y < (uint)ph ? dt[y * pw + x] : 0;
        }

        public bool IsObject(Vec2 p)
        {
            var x = (int)Math.Floor(p.X) - x0 + 1;
            var y = (int)Math.Floor(p.Y) - y0 + 1;
            return (uint)x < (uint)pw && (uint)y < (uint)ph && mask[y * pw + x];
        }
    }

    /// <summary>
    /// Two touching balls pinch to nothing where they meet, so the distance
    /// field between their centres drops close to zero. Along a cue stick it
    /// stays high: peaks joined by a high saddle are a ridge, not balls.
    /// </summary>
    private static List<Peak> RejectRidges(List<Peak> peaks, Field field, double radius)
    {
        var ridge = new bool[peaks.Count];
        for (var i = 0; i < peaks.Count; i++)
        {
            for (var j = i + 1; j < peaks.Count; j++)
            {
                var a = peaks[i].Position;
                var b = peaks[j].Position;
                if (Vec2.Distance(a, b) >= 2.5 * radius) continue;

                var saddle = Math.Max(
                    field.DistanceAt(a + (b - a) * 0.5),
                    Math.Max(field.DistanceAt(a + (b - a) * 0.4), field.DistanceAt(a + (b - a) * 0.6)));

                if (saddle >= 0.62 * radius)
                {
                    ridge[i] = true;
                    ridge[j] = true;
                }
            }
        }

        return peaks.Where((_, i) => !ridge[i]).ToList();
    }

    /// <summary>
    /// Casts rays from the candidate centre. A ball's outline is met at about
    /// one radius in every direction, except towards a ball it touches.
    /// Returns the refined centre and the fit, or null when it is not round.
    /// </summary>
    private static (Vec2 Centre, double Fit)? ProbeShape(Vec2 centre, List<Peak> neighbours, Field field, double radius)
    {
        var edges = new List<Vec2>();
        var bad = 0.0;
        var touching = 0;

        for (var k = 0; k < Rays; k++)
        {
            var dir = Vec2.FromAngle(2 * Math.PI * k / Rays);
            var hit = -1.0;
            for (var t = 0.5 * radius; t <= 1.8 * radius; t += 0.5)
            {
                if (field.IsObject(centre + dir * t)) continue;
                hit = t;
                break;
            }

            if (hit < 0)
            {
                var explained = neighbours.Any(n =>
                {
                    var offset = n.Position - centre;
                    var distance = offset.Length;
                    return distance > 1e-6 && distance < 2.7 * radius && Geo.AngleBetween(dir, offset) < 32;
                });
                if (explained) touching++;
                else bad += 1;
            }
            else if (hit < 0.7 * radius)
            {
                // Part of the ball matched the felt (a green ball on green cloth):
                // suspicious, but not proof of anything.
                bad += 0.5;
            }
            else if (hit <= 1.35 * radius)
            {
                edges.Add(centre + dir * (hit - 0.25));
            }
            else
            {
                bad += 1;
            }
        }

        // Rays towards touching balls cannot see an edge, so a ball in the
        // middle of a cluster needs a clean outline only where it is free.
        if (bad > 0.3 * Rays || edges.Count < Math.Max(4, 0.45 * (Rays - touching))) return null;

        var fit = edges.Count / (double)Rays;
        var refined = FitCircleCentre(edges, centre);
        if (refined is { } r && Vec2.Distance(r, centre) <= 0.4 * radius) return (r, fit);
        return (centre, fit);
    }

    /// <summary>Algebraic (Kåsa) circle fit; returns the centre.</summary>
    internal static Vec2? FitCircleCentre(IReadOnlyList<Vec2> points, Vec2 origin)
    {
        if (points.Count < 6) return null;

        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var p in points)
        {
            var x = p.X - origin.X;
            var y = p.Y - origin.Y;
            var z = x * x + y * y;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sx += x;
            sy += y;
            sxz += x * z;
            syz += y * z;
            sz += z;
        }

        double n = points.Count;

        // [sxx sxy sx][D]   [-sxz]
        // [sxy syy sy][E] = [-syz]
        // [sx  sy  n ][F]   [-sz ]
        var det = sxx * (syy * n - sy * sy) - sxy * (sxy * n - sy * sx) + sx * (sxy * sy - syy * sx);
        if (Math.Abs(det) < 1e-9) return null;

        double b1 = -sxz, b2 = -syz, b3 = -sz;
        var dD = b1 * (syy * n - sy * sy) - sxy * (b2 * n - sy * b3) + sx * (b2 * sy - syy * b3);
        var dE = sxx * (b2 * n - sy * b3) - b1 * (sxy * n - sy * sx) + sx * (sxy * b3 - b2 * sx);

        var d = dD / det;
        var e = dE / det;
        return new Vec2(origin.X - d / 2, origin.Y - e / 2);
    }
}
