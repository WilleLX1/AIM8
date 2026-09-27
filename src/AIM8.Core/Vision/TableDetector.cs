using AIM8.Core.Geometry;

namespace AIM8.Core.Vision;

public sealed record TableDetection(FeltModel Felt, TableGeometry Geometry, bool Manual);

/// <summary>
/// Finds the playing surface: the dominant saturated colour in the middle of
/// the screen is taken as the felt, the largest connected patch of it is the
/// table, and its edges are then refined at full resolution.
/// </summary>
public sealed class TableDetector
{
    public TableDetection? Detect(Frame frame, AimSettings settings)
    {
        if (settings.ManualTable is { IsUsable: true } manual)
        {
            var rect = manual.ToPixels(frame.Width, frame.Height);
            var manualFelt = EstimateFelt(frame, rect.Inflate(-rect.ShortSide * 0.06), settings.FeltTolerance);
            return manualFelt is null
                ? null
                : new TableDetection(MeasureCeiling(frame, manualFelt, rect), TableGeometry.FromCushion(rect), Manual: true);
        }

        var middle = RectD.FromEdges(frame.Width * 0.2, frame.Height * 0.2, frame.Width * 0.8, frame.Height * 0.8);
        var felt = EstimateFelt(frame, middle, settings.FeltTolerance);
        if (felt is null) return null;

        var bounds = FindFeltBounds(frame, felt);
        if (bounds is null) return null;

        felt = MeasureCeiling(frame, felt, bounds.Value);

        var cushion = RefineEdges(frame, felt, bounds.Value);
        if (settings.DetectCushionEdge) cushion = FindCushionNose(frame, cushion);

        return IsPlausibleTable(cushion, frame)
            ? new TableDetection(felt, TableGeometry.FromCushion(cushion), Manual: false)
            : null;
    }

    internal static bool IsPlausibleTable(RectD rect, Frame frame)
    {
        if (rect.IsEmpty) return false;
        var aspect = rect.LongSide / rect.ShortSide;
        var share = rect.Width * rect.Height / (frame.Width * (double)frame.Height);
        return aspect is >= 1.2 and <= 3.2 && share >= 0.08;
    }

    /// <summary>
    /// The felt is the most common saturated hue in <paramref name="region"/>.
    /// Returns null when no hue dominates, i.e. there is no table on screen.
    /// </summary>
    internal static FeltModel? EstimateFelt(Frame frame, RectD region, double tolerance)
    {
        var left = Math.Clamp((int)region.Left, 0, frame.Width - 1);
        var right = Math.Clamp((int)region.Right, left + 1, frame.Width);
        var top = Math.Clamp((int)region.Top, 0, frame.Height - 1);
        var bottom = Math.Clamp((int)region.Bottom, top + 1, frame.Height);
        var step = Math.Max(1, Math.Min(right - left, bottom - top) / 120);

        var histogram = new double[72];
        var samples = new List<(float H, float S, float V)>();
        var total = 0;
        var pixels = frame.Pixels;

        for (var y = top; y < bottom; y += step)
        {
            for (var x = left; x < right; x += step)
            {
                total++;
                var o = frame.Offset(x, y);
                Hsv.FromRgb(pixels[o], pixels[o + 1], pixels[o + 2], out var h, out var s, out var v);
                if (s < 0.2f || v < 0.15f) continue;

                histogram[(int)(h / 5) % 72]++;
                samples.Add((h, s, v));
            }
        }

        if (samples.Count < 30) return null;

        var peak = 0;
        var peakMass = 0.0;
        for (var i = 0; i < 72; i++)
        {
            var mass = histogram[(i + 71) % 72] + histogram[i] + histogram[(i + 1) % 72];
            if (mass > peakMass)
            {
                peakMass = mass;
                peak = i;
            }
        }

        if (peakMass < 0.25 * total) return null;

        var centre = peak * 5 + 2.5f;
        double sin = 0, cos = 0;
        var saturations = new List<float>();
        var values = new List<float>();
        foreach (var (h, s, v) in samples)
        {
            if (Hsv.HueDistance(h, centre) > 10) continue;
            var radians = h * Math.PI / 180;
            sin += Math.Sin(radians);
            cos += Math.Cos(radians);
            saturations.Add(s);
            values.Add(v);
        }

        var hue = (float)(Math.Atan2(sin, cos) * 180 / Math.PI);
        if (hue < 0) hue += 360;

        // How far the cloth's own hue wanders sets how close a ball's hue may
        // come before it is mistaken for cloth: flat game felt allows a blue
        // ball on blue cloth, textured felt needs more room.
        var spread = samples
            .Select(s => Hsv.HueDistance(s.H, hue))
            .Where(d => d <= 20)
            .OrderBy(d => d)
            .ToList();
        var p90 = spread.Count > 0 ? spread[(int)(spread.Count * 0.9)] : 10;

        return new FeltModel(hue, Median(saturations), Median(values), tolerance, hueSpread: p90);
    }

    /// <summary>
    /// Sets the felt's saturation ceiling from the cloth across the whole
    /// table. Game cloth is often lit brighter in the middle and darker, more
    /// saturated towards the cushions; a fixed ceiling cut holes there, while
    /// flat cloth keeps a tight one that tells a green ball from green cloth.
    /// </summary>
    internal static FeltModel MeasureCeiling(Frame frame, FeltModel felt, RectD region)
    {
        var step = Math.Max(1, (int)(region.ShortSide / 120));
        var saturations = new List<float>();
        for (var y = Math.Max(0, (int)region.Top); y < Math.Min(frame.Height, (int)region.Bottom); y += step)
        {
            for (var x = Math.Max(0, (int)region.Left); x < Math.Min(frame.Width, (int)region.Right); x += step)
            {
                if (!felt.IsFelt(frame, x, y)) continue;
                var o = frame.Offset(x, y);
                Hsv.FromRgb(frame.Pixels[o], frame.Pixels[o + 1], frame.Pixels[o + 2], out _, out var s, out _);
                saturations.Add(s);
            }
        }

        if (saturations.Count < 50) return felt;
        saturations.Sort();
        return felt.WithSaturationCeiling(saturations[(int)(saturations.Count * 0.95)]);
    }

    /// <summary>Bounding rectangle of the largest felt region, measured on a coarse grid.</summary>
    internal static RectD? FindFeltBounds(Frame frame, FeltModel felt)
    {
        var g = Math.Max(2, Math.Min(frame.Width, frame.Height) / 260);
        var gw = frame.Width / g;
        var gh = frame.Height / g;
        if (gw < 8 || gh < 8) return null;

        var mask = new bool[gw * gh];
        for (var gy = 0; gy < gh; gy++)
        {
            for (var gx = 0; gx < gw; gx++)
            {
                mask[gy * gw + gx] = felt.IsFelt(frame, gx * g + g / 2, gy * g + g / 2);
            }
        }

        var labels = new int[gw * gh];
        var queue = new int[gw * gh];
        var bestLabel = 0;
        var bestSize = 0;
        var next = 0;
        var regions = new List<Region>();

        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0) continue;

            next++;
            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            labels[start] = next;

            while (head < tail)
            {
                var i = queue[head++];
                var x = i % gw;
                var y = i / gw;

                if (x > 0) Visit(i - 1);
                if (x < gw - 1) Visit(i + 1);
                if (y > 0) Visit(i - gw);
                if (y < gh - 1) Visit(i + gw);
            }

            var box = (MinX: gw, MinY: gh, MaxX: -1, MaxY: -1);
            for (var k = 0; k < tail; k++)
            {
                var x = queue[k] % gw;
                var y = queue[k] / gw;
                box = (Math.Min(box.MinX, x), Math.Min(box.MinY, y), Math.Max(box.MaxX, x), Math.Max(box.MaxY, y));
            }

            regions.Add(new Region(next, tail, box.MinX, box.MinY, box.MaxX, box.MaxY));
            if (tail > bestSize)
            {
                bestSize = tail;
                bestLabel = next;
            }

            void Visit(int j)
            {
                if (!mask[j] || labels[j] != 0) return;
                labels[j] = next;
                queue[tail++] = j;
            }
        }

        if (bestSize < mask.Length * 0.05) return null;

        var members = MergeSplitTable(regions, bestLabel, bestSize);
        var member = new bool[next + 1];
        foreach (var label in members) member[label] = true;

        // Balls, pockets and the cue punch holes in the felt, so the edges come
        // from rows and columns that are mostly felt rather than from the
        // extreme cells of the region.
        var rowCounts = new int[gh];
        for (var y = 0; y < gh; y++)
        {
            for (var x = 0; x < gw; x++)
            {
                if (member[labels[y * gw + x]]) rowCounts[y]++;
            }
        }

        var (y0, y1) = LongestRun(rowCounts, 0.5 * rowCounts.Max(), maxGap: gh / 12);

        var colCounts = new int[gw];
        for (var y = y0; y <= y1; y++)
        {
            for (var x = 0; x < gw; x++)
            {
                if (member[labels[y * gw + x]]) colCounts[x]++;
            }
        }

        var (x0, x1) = LongestRun(colCounts, 0.5 * colCounts.Max(), maxGap: gw / 12);

        return RectD.FromEdges(x0 * g, y0 * g, (x1 + 1) * g, (y1 + 1) * g);
    }

    private sealed record Region(int Label, int Size, int MinX, int MinY, int MaxX, int MaxY);

    /// <summary>
    /// A cue stick or aim line lying right across the table cuts the felt in
    /// two. Pieces that line up with the largest one across a narrow gap are
    /// the same table.
    /// </summary>
    private static List<int> MergeSplitTable(List<Region> regions, int bestLabel, int bestSize)
    {
        var best = regions.First(r => r.Label == bestLabel);
        var box = (best.MinX, best.MinY, best.MaxX, best.MaxY);
        var members = new List<int> { bestLabel };

        var added = true;
        while (added)
        {
            added = false;
            foreach (var region in regions)
            {
                if (members.Contains(region.Label) || region.Size < bestSize * 0.08) continue;

                var width = box.MaxX - box.MinX + 1;
                var height = box.MaxY - box.MinY + 1;
                var xOverlap = Math.Min(box.MaxX, region.MaxX) - Math.Max(box.MinX, region.MinX) + 1;
                var yOverlap = Math.Min(box.MaxY, region.MaxY) - Math.Max(box.MinY, region.MinY) + 1;
                var xGap = Math.Max(region.MinX - box.MaxX, box.MinX - region.MaxX);
                var yGap = Math.Max(region.MinY - box.MaxY, box.MinY - region.MaxY);

                var stackedVertically = xOverlap >= 0.7 * Math.Min(width, region.MaxX - region.MinX + 1) && yGap <= height * 0.12;
                var sideBySide = yOverlap >= 0.7 * Math.Min(height, region.MaxY - region.MinY + 1) && xGap <= width * 0.12;
                if (!stackedVertically && !sideBySide) continue;

                members.Add(region.Label);
                box = (Math.Min(box.MinX, region.MinX), Math.Min(box.MinY, region.MinY),
                    Math.Max(box.MaxX, region.MaxX), Math.Max(box.MaxY, region.MaxY));
                added = true;
            }
        }

        return members;
    }

    /// <summary>Longest run of entries at or above the threshold, bridging dips of up to <paramref name="maxGap"/>.</summary>
    private static (int Start, int End) LongestRun(int[] counts, double threshold, int maxGap)
    {
        var runs = new List<(int Start, int End)>();
        var (start, end) = SingleRuns(counts, threshold).FirstOrDefault((-1, -1));
        foreach (var run in SingleRuns(counts, threshold).Skip(1))
        {
            if (run.Start - end - 1 <= maxGap)
            {
                end = run.End;
                continue;
            }

            runs.Add((start, end));
            (start, end) = run;
        }

        if (start < 0) return (0, counts.Length - 1);
        runs.Add((start, end));
        return runs.MaxBy(r => r.End - r.Start);
    }

    private static IEnumerable<(int Start, int End)> SingleRuns(int[] counts, double threshold)
    {
        var start = -1;
        for (var i = 0; i <= counts.Length; i++)
        {
            var inside = i < counts.Length && counts[i] >= threshold;
            if (inside)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;
            yield return (start, i - 1);
            start = -1;
        }
    }

    /// <summary>Fractions along an edge to probe: clear of the corner and side pockets.</summary>
    private static readonly double[] ProbePositions =
        [0.14, 0.18, 0.22, 0.26, 0.30, 0.34, 0.38, 0.62, 0.66, 0.70, 0.74, 0.78, 0.82, 0.86];

    /// <summary>
    /// Moves each edge of the coarse rectangle onto the felt boundary at full
    /// resolution. Each probe starts on the bed and walks outwards to the first
    /// solid line of non-felt: that stops at the dark nose line many games draw
    /// along the cushion, rather than running on over a cushion top that is
    /// itself felt-coloured.
    /// </summary>
    internal static RectD RefineEdges(Frame frame, FeltModel felt, RectD rect)
    {
        var window = (int)Math.Max(6, rect.ShortSide * 0.05);

        bool Felt(int x, int y) => frame.Contains(x, y) && felt.IsFelt(frame, x, y);

        // Walks from (x, y) in steps of (dx, dy); returns the index of the last
        // felt pixel before two non-felt ones, or null when the probe starts off
        // the felt (a ball on the line) or never leaves it.
        int? Walk(int x, int y, int dx, int dy)
        {
            if (!Felt(x, y)) return null;
            for (var i = 0; i <= 3 * window; i++)
            {
                var nx = x + dx * (i + 1);
                var ny = y + dy * (i + 1);
                if (!Felt(nx, ny) && !Felt(nx + dx, ny + dy)) return dx != 0 ? nx - dx : ny - dy;
            }

            return null;
        }

        var lefts = new List<double>();
        var rights = new List<double>();
        var tops = new List<double>();
        var bottoms = new List<double>();
        foreach (var f in ProbePositions)
        {
            var y = (int)(rect.Top + rect.Height * f);
            if (Walk((int)rect.Left + window, y, -1, 0) is { } l) lefts.Add(l);
            if (Walk((int)rect.Right - 1 - window, y, 1, 0) is { } r) rights.Add(r + 1);

            var x = (int)(rect.Left + rect.Width * f);
            if (Walk(x, (int)rect.Top + window, 0, -1) is { } t) tops.Add(t);
            if (Walk(x, (int)rect.Bottom - 1 - window, 0, 1) is { } b) bottoms.Add(b + 1);
        }

        const int enough = 5;
        return RectD.FromEdges(
            lefts.Count >= enough ? Median(lefts) : rect.Left,
            tops.Count >= enough ? Median(tops) : rect.Top,
            rights.Count >= enough ? Median(rights) : rect.Right,
            bottoms.Count >= enough ? Median(bottoms) : rect.Bottom);
    }

    /// <summary>
    /// Many games draw the cushions in the felt's own hue, only darker or
    /// lighter, so the felt region runs up to the rail rather than to the
    /// cushion nose. A brightness step close inside an edge is taken as the nose.
    /// </summary>
    internal static RectD FindCushionNose(Frame frame, RectD rect)
    {
        var depth = (int)Math.Round(rect.ShortSide * 0.07);
        if (depth < 10) return rect;

        var left = rect.Left + Step(y => ((int)rect.Left, y), 1, 0, vertical: true);
        var right = rect.Right - Step(y => ((int)rect.Right - 1, y), -1, 0, vertical: true);
        var top = rect.Top + Step(x => (x, (int)rect.Top), 0, 1, vertical: false);
        var bottom = rect.Bottom - Step(x => (x, (int)rect.Bottom - 1), 0, -1, vertical: false);

        var refined = RectD.FromEdges(left, top, right, bottom);
        return refined.Width > rect.Width * 0.8 && refined.Height > rect.Height * 0.8 ? refined : rect;

        // Median brightness profile going inwards from one edge; returns how far
        // in the step sits, or 0 when there is none.
        int Step(Func<int, (int X, int Y)> origin, int dx, int dy, bool vertical)
        {
            var profiles = new List<float[]>();
            foreach (var f in ProbePositions)
            {
                var along = vertical ? (int)(rect.Top + rect.Height * f) : (int)(rect.Left + rect.Width * f);
                var (ox, oy) = origin(along);
                var profile = new float[depth];
                var ok = true;
                for (var d = 0; d < depth && ok; d++)
                {
                    var x = ox + dx * d;
                    var y = oy + dy * d;
                    if (!frame.Contains(x, y))
                    {
                        ok = false;
                        break;
                    }

                    var o = frame.Offset(x, y);
                    Hsv.FromRgb(frame.Pixels[o], frame.Pixels[o + 1], frame.Pixels[o + 2], out _, out _, out var v);
                    profile[d] = v;
                }

                if (ok) profiles.Add(profile);
            }

            if (profiles.Count < 5) return 0;

            var median = new float[depth];
            for (var d = 0; d < depth; d++)
            {
                median[d] = Median(profiles.Select(p => p[d]).ToList());
            }

            // Only a sharp step counts: the soft shadow a cushion casts on the bed
            // is part of the bed, and the ball rolls over it.
            var bestD = 0;
            var bestJump = 0.0;
            for (var d = 3; d <= depth - 4; d++)
            {
                var before = (median[d - 1] + median[d - 2]) / 2.0;
                var after = (median[d + 1] + median[d + 2]) / 2.0;
                var jump = Math.Abs(after - before);
                if (jump > bestJump)
                {
                    bestJump = jump;
                    bestD = d;
                }
            }

            if (bestJump < 0.09) return 0;

            // The bed beyond the step should be even; a step followed by more
            // steps is texture or a ball, not a cushion.
            var tail = median.Skip(Math.Min(depth - 1, bestD + 2)).ToArray();
            if (tail.Length < 3) return 0;
            var mean = tail.Average();
            var spread = Math.Sqrt(tail.Select(v => (v - mean) * (v - mean)).Average());
            return spread <= 0.035 ? bestD : 0;
        }
    }

    internal static float Median(List<float> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    internal static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
