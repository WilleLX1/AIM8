using AIM8.Core.Geometry;

namespace AIM8.Core.Solver;

public enum ShotKind
{
    /// <summary>Cue ball straight onto the object ball, object ball straight into the pocket.</summary>
    Direct,

    /// <summary>Object ball off one cushion into the pocket.</summary>
    Bank,

    /// <summary>Cue ball off one cushion onto the object ball.</summary>
    Kick,

    /// <summary>Cue ball onto one of your balls, which knocks another of yours in.</summary>
    Combo,

    /// <summary>The rack is still intact.</summary>
    Break,

    /// <summary>Nothing pots cleanly: at least make a legal hit.</summary>
    Safety,

    /// <summary>The cue ball is off the table; where to put it for the easiest pot.</summary>
    BallInHand,
}

public sealed record Shot
{
    public required ShotKind Kind { get; init; }

    /// <summary>The ball that should drop (for a safety or break, the ball to hit).</summary>
    public required Ball Target { get; init; }

    /// <summary>For a combination, the ball the cue ball hits first.</summary>
    public Ball? First { get; init; }

    public Pocket? Pocket { get; init; }

    /// <summary>Cue ball centre at the moment of contact.</summary>
    public required Vec2 Ghost { get; init; }

    /// <summary>Cue ball centre from where it lies to the ghost position, through any cushion.</summary>
    public required IReadOnlyList<Vec2> CuePath { get; init; }

    /// <summary>Target ball centre to the pocket, through any cushion.</summary>
    public IReadOnlyList<Vec2> ObjectPath { get; init; } = [];

    /// <summary>For a combination, the first ball's path onto the target.</summary>
    public IReadOnlyList<Vec2> FirstPath { get; init; } = [];

    /// <summary>Where the cue ball heads after contact, played with no spin.</summary>
    public IReadOnlyList<Vec2> CueAfter { get; init; } = [];

    /// <summary>Degrees; 0 is a full, straight hit.</summary>
    public double CutAngle { get; init; }

    /// <summary>Rough chance of the shot going in (0..1). Only meaningful for ranking.</summary>
    public double Probability { get; init; }

    public bool ScratchRisk { get; init; }

    /// <summary>Ball in hand: where to place the cue ball.</summary>
    public Vec2? CuePlacement { get; init; }

    public string Description { get; init; } = "";
}

public sealed record ShotPlan(IReadOnlyList<Shot> Shots, IReadOnlyList<Ball> Targets, bool OnEight, string Summary)
{
    public static readonly ShotPlan None = new([], [], false, "");

    public Shot? Best => Shots.Count > 0 ? Shots[0] : null;
}

/// <summary>
/// Ghost-ball geometry: to send an object ball towards a point, the cue ball
/// must arrive touching it on the far side of that line. Each candidate is
/// scored by how much aiming error it tolerates - the pocket's width seen from
/// the object ball, shrunk by the cut (thin cuts magnify error) and by the
/// cue-ball distance - which ranks shots the way a player would.
/// </summary>
public sealed class ShotSolver
{
    /// <summary>Aiming error, in radians, that halves-ish the chance of a shot (see <see cref="Chance"/>).</summary>
    private const double AimPrecision = 0.0025;

    private const double BankFactor = 0.62;
    private const double KickFactor = 0.5;
    private const double ComboFactor = 0.8;

    /// <param name="clusters">Blobs of balls too tight to split; a big one is taken as the rack.</param>
    public ShotPlan Solve(
        TableGeometry table, IReadOnlyList<Ball> balls, double radius, AimSettings settings,
        IReadOnlyList<Vision.BallCluster>? clusters = null)
    {
        if (radius <= 0) return ShotPlan.None;

        var cue = balls.Where(b => b.Kind == BallKind.Cue).MaxBy(b => b.Confidence);
        var targets = LegalTargets(balls, settings.Team, out var onEight);
        var ctx = new Context(table, balls, cue, radius, settings);

        // The break comes first: a rack seen as one blob has no targets yet.
        var rack = clusters?.Where(k => k.Balls >= 10).MaxBy(k => k.Balls);
        if (cue is not null && (rack is not null || IsRack(balls, radius)) && Break(ctx, rack) is { } breakShot)
        {
            return new ShotPlan([breakShot], targets, onEight, "Break");
        }

        if (targets.Count == 0) return new ShotPlan([], [], onEight, "No target balls found");

        if (cue is null)
        {
            var placed = BallInHand(ctx, targets).OrderByDescending(s => s.Probability).Take(1 + settings.Alternatives).ToList();
            return new ShotPlan(placed, targets, onEight,
                placed.Count > 0 ? "Cue ball not on the table - place it here if you have ball in hand" : "Cue ball not found");
        }

        var shots = new List<Shot>();
        foreach (var target in targets)
        {
            foreach (var pocket in table.Pockets)
            {
                if (Direct(ctx, target, pocket) is { } direct) shots.Add(direct);
                if (settings.AllowBanks) shots.AddRange(Banks(ctx, target, pocket));
                if (settings.AllowKicks) shots.AddRange(Kicks(ctx, target, pocket));
            }
        }

        if (settings.AllowCombos && !onEight)
        {
            foreach (var first in targets)
            {
                foreach (var second in targets)
                {
                    if (second.Id == first.Id) continue;
                    foreach (var pocket in table.Pockets)
                    {
                        if (Combo(ctx, first, second, pocket) is { } combo) shots.Add(combo);
                    }
                }
            }
        }

        // One entry per ball and pocket: the best way of getting it there.
        var ranked = shots
            .GroupBy(s => (s.Target.Id, s.Pocket?.Index))
            .Select(g => g.MaxBy(s => s.Probability)!)
            .OrderByDescending(s => s.Probability)
            .Take(1 + Math.Max(0, settings.Alternatives))
            .ToList();

        if (ranked.Count == 0 || ranked[0].Probability < 0.02)
        {
            if (Safety(ctx, targets) is { } safety) ranked.Insert(0, safety);
        }

        var summary = ranked.Count == 0 ? "No legal shot found" : ranked[0].Description;
        return new ShotPlan(ranked, targets, onEight, summary);
    }

    public static IReadOnlyList<Ball> LegalTargets(IReadOnlyList<Ball> balls, Team team, out bool onEight)
    {
        var group = team switch
        {
            Team.Solids => balls.Where(b => b.Kind == BallKind.Solid),
            Team.Stripes => balls.Where(b => b.Kind == BallKind.Stripe),
            _ => balls.Where(b => b.Kind is BallKind.Solid or BallKind.Stripe),
        };

        var list = group.ToList();
        onEight = list.Count == 0;
        if (onEight) list = balls.Where(b => b.Kind == BallKind.Eight).ToList();
        return list;
    }

    private sealed class Context(TableGeometry table, IReadOnlyList<Ball> balls, Ball? cue, double radius, AimSettings settings)
    {
        public TableGeometry Table { get; } = table;

        public IReadOnlyList<Ball> Balls { get; } = balls;

        public Ball? Cue { get; } = cue;

        public double R { get; } = radius;

        public AimSettings Settings { get; } = settings;

        public RectD Bounds { get; } = table.CenterBounds(radius);

        public IReadOnlyList<CushionLine> Cushions { get; } = table.CushionLines(radius);

        /// <summary>Centre-to-centre distance below which two balls touch, with a hair of tolerance.</summary>
        public double Contact => 2 * R * 0.98;

        public bool Clear(Vec2 a, Vec2 b, params int[] ignore) =>
            Balls.All(o => ignore.Contains(o.Id) || Geo.DistanceToSegment(o.Position, a, b) >= Contact);

        /// <summary>A ghost position must be on the table and not inside another ball.</summary>
        public bool GhostFits(Vec2 ghost, params int[] ignore) =>
            Bounds.Inflate(0.15 * R).Contains(ghost) &&
            Balls.All(o => ignore.Contains(o.Id) || Vec2.Distance(o.Position, ghost) >= Contact);

        public int CueId => Cue?.Id ?? -1;
    }

    // ---- Shot types ------------------------------------------------------

    private static Shot? Direct(Context ctx, Ball target, Pocket pocket)
    {
        var cue = ctx.Cue!;
        var r = ctx.R;
        var b = target.Position;
        var toPocket = pocket.Position - b;
        var pocketDistance = toPocket.Length;
        if (pocketDistance < 1e-6) return null;

        var u = toPocket / pocketDistance;
        if (!PocketAccepts(pocket, u, out var pocketFactor)) return null;
        if (!ctx.Clear(b, pocket.Position, target.Id, ctx.CueId)) return null;

        var ghost = b - u * (2 * r);
        if (!ctx.GhostFits(ghost, target.Id, ctx.CueId)) return null;

        var aim = ghost - cue.Position;
        var cueDistance = aim.Length;
        if (cueDistance < 1e-6) return null;
        var v = aim / cueDistance;

        var cut = Geo.AngleBetween(v, u);
        if (cut > ctx.Settings.MaxCutAngle) return null;
        if (!ctx.Clear(cue.Position, ghost, target.Id, ctx.CueId)) return null;

        var chance = Chance(PocketTolerance(pocket, pocketFactor, pocketDistance, r), cut, cueDistance, r);
        var (after, scratch) = CueAfterContact(ctx, ghost, v, u, target.Id);
        if (scratch) chance *= 0.55;

        return new Shot
        {
            Kind = ShotKind.Direct,
            Target = target,
            Pocket = pocket,
            Ghost = ghost,
            CuePath = [cue.Position, ghost],
            ObjectPath = [b, pocket.Position],
            CueAfter = after,
            CutAngle = cut,
            Probability = chance,
            ScratchRisk = scratch,
            Description = $"{target.Label} → {pocket.Name}",
        };
    }

    private static IEnumerable<Shot> Banks(Context ctx, Ball target, Pocket pocket)
    {
        var cue = ctx.Cue!;
        var r = ctx.R;
        var b = target.Position;

        foreach (var cushion in ctx.Cushions)
        {
            // Banking along the cushion the pocket sits on is not a bank.
            if (Math.Abs(cushion.SignedDistance(pocket.Position)) < 2.5 * r) continue;

            var mirrored = cushion.Mirror(pocket.Position);
            var kiss = Intersect(cushion, b, mirrored);
            if (kiss is not { } k || !OnCushion(ctx, cushion, k)) continue;

            if (!ctx.Clear(b, k, target.Id, ctx.CueId) || !ctx.Clear(k, pocket.Position, target.Id, ctx.CueId)) continue;

            var outbound = (pocket.Position - k).Normalized();
            if (!PocketAccepts(pocket, outbound, out var pocketFactor)) continue;

            var u = (k - b).Normalized();
            var ghost = b - u * (2 * r);
            if (!ctx.GhostFits(ghost, target.Id, ctx.CueId)) continue;

            var aim = ghost - cue.Position;
            var cueDistance = aim.Length;
            if (cueDistance < 1e-6) continue;
            var v = aim / cueDistance;

            var cut = Geo.AngleBetween(v, u);
            if (cut > ctx.Settings.MaxCutAngle) continue;
            if (!ctx.Clear(cue.Position, ghost, target.Id, ctx.CueId)) continue;

            var travel = Vec2.Distance(b, k) + Vec2.Distance(k, pocket.Position);
            var chance = BankFactor * Chance(PocketTolerance(pocket, pocketFactor, travel, r), cut, cueDistance, r);
            var (after, scratch) = CueAfterContact(ctx, ghost, v, u, target.Id);
            if (scratch) chance *= 0.55;

            yield return new Shot
            {
                Kind = ShotKind.Bank,
                Target = target,
                Pocket = pocket,
                Ghost = ghost,
                CuePath = [cue.Position, ghost],
                ObjectPath = [b, k, pocket.Position],
                CueAfter = after,
                CutAngle = cut,
                Probability = chance,
                ScratchRisk = scratch,
                Description = $"Bank {target.Label} off {cushion.Name} → {pocket.Name}",
            };
        }
    }

    private static IEnumerable<Shot> Kicks(Context ctx, Ball target, Pocket pocket)
    {
        var cue = ctx.Cue!;
        var r = ctx.R;
        var b = target.Position;
        var toPocket = pocket.Position - b;
        var pocketDistance = toPocket.Length;
        if (pocketDistance < 1e-6) yield break;

        var u = toPocket / pocketDistance;
        if (!PocketAccepts(pocket, u, out var pocketFactor)) yield break;
        if (!ctx.Clear(b, pocket.Position, target.Id, ctx.CueId)) yield break;

        var ghost = b - u * (2 * r);
        if (!ctx.GhostFits(ghost, target.Id, ctx.CueId)) yield break;

        foreach (var cushion in ctx.Cushions)
        {
            var mirrored = cushion.Mirror(ghost);
            var kiss = Intersect(cushion, cue.Position, mirrored);
            if (kiss is not { } k || !OnCushion(ctx, cushion, k)) continue;

            if (!ctx.Clear(cue.Position, k, ctx.CueId) || !ctx.Clear(k, ghost, target.Id, ctx.CueId)) continue;

            var v = (ghost - k).Normalized();
            var cut = Geo.AngleBetween(v, u);
            if (cut > ctx.Settings.MaxCutAngle) continue;

            var cueDistance = Vec2.Distance(cue.Position, k) + Vec2.Distance(k, ghost);
            var chance = KickFactor * Chance(PocketTolerance(pocket, pocketFactor, pocketDistance, r), cut, cueDistance, r);
            var (after, scratch) = CueAfterContact(ctx, ghost, v, u, target.Id);
            if (scratch) chance *= 0.55;

            yield return new Shot
            {
                Kind = ShotKind.Kick,
                Target = target,
                Pocket = pocket,
                Ghost = ghost,
                CuePath = [cue.Position, k, ghost],
                ObjectPath = [b, pocket.Position],
                CueAfter = after,
                CutAngle = cut,
                Probability = chance,
                ScratchRisk = scratch,
                Description = $"Kick off {cushion.Name}: {target.Label} → {pocket.Name}",
            };
        }
    }

    private static Shot? Combo(Context ctx, Ball first, Ball second, Pocket pocket)
    {
        var cue = ctx.Cue!;
        var r = ctx.R;
        var b = second.Position;
        var toPocket = pocket.Position - b;
        var pocketDistance = toPocket.Length;
        if (pocketDistance < 1e-6) return null;

        var ub = toPocket / pocketDistance;
        if (!PocketAccepts(pocket, ub, out var pocketFactor)) return null;
        if (!ctx.Clear(b, pocket.Position, first.Id, second.Id, ctx.CueId)) return null;

        var ghostB = b - ub * (2 * r);
        if (!ctx.GhostFits(ghostB, first.Id, second.Id, ctx.CueId)) return null;

        var a = first.Position;
        var toGhostB = ghostB - a;
        var firstDistance = toGhostB.Length;
        if (firstDistance < 1e-6 || firstDistance > 14 * r) return null;

        var ua = toGhostB / firstDistance;
        var cutB = Geo.AngleBetween(ua, ub);
        if (cutB > 55) return null;
        if (!ctx.Clear(a, ghostB, first.Id, second.Id, ctx.CueId)) return null;

        var ghostA = a - ua * (2 * r);
        if (!ctx.GhostFits(ghostA, first.Id, ctx.CueId)) return null;

        var aim = ghostA - cue.Position;
        var cueDistance = aim.Length;
        if (cueDistance < 1e-6) return null;
        var v = aim / cueDistance;

        var cutA = Geo.AngleBetween(v, ua);
        if (cutA > ctx.Settings.MaxCutAngle) return null;
        if (!ctx.Clear(cue.Position, ghostA, first.Id, ctx.CueId)) return null;

        // Error allowed on the first ball's direction, then on the cue's.
        var allowedFirst = PocketTolerance(pocket, pocketFactor, pocketDistance, r) * 2 * r *
                           Math.Cos(Geo.ToRadians(cutB)) / Math.Max(firstDistance, 2 * r);
        var chance = ComboFactor * Chance(allowedFirst, cutA, cueDistance, r);
        var (after, scratch) = CueAfterContact(ctx, ghostA, v, ua, first.Id);
        if (scratch) chance *= 0.55;

        return new Shot
        {
            Kind = ShotKind.Combo,
            Target = second,
            First = first,
            Pocket = pocket,
            Ghost = ghostA,
            CuePath = [cue.Position, ghostA],
            FirstPath = [a, ghostB],
            ObjectPath = [b, pocket.Position],
            CueAfter = after,
            CutAngle = cutA,
            Probability = chance,
            ScratchRisk = scratch,
            Description = $"Combo {first.Label} → {second.Label} → {pocket.Name}",
        };
    }

    private static Shot? Break(Context ctx, Vision.BallCluster? rack)
    {
        var cue = ctx.Cue!;
        var head = ctx.Balls.Where(b => b.IsObjectBall).MinBy(b => Vec2.Distance(b.Position, cue.Position));

        // A rack seen only as one blob: its head ball sits one radius inside
        // the blob edge nearest the cue ball.
        if (rack is not null && rack.Outline.Count > 0)
        {
            var edge = rack.Outline.MinBy(p => Vec2.Distance(p, cue.Position));
            var centre = edge + (rack.Centre - edge).Normalized() * ctx.R;
            if (head is null || Vec2.Distance(centre, cue.Position) < Vec2.Distance(head.Position, cue.Position))
            {
                head = new Ball(-1, centre, BallKind.Unknown, -1, "#ffffff", 0);
            }
        }

        if (head is null) return null;

        var v = (head.Position - cue.Position).Normalized();
        var ghost = head.Position - v * (2 * ctx.R);
        return new Shot
        {
            Kind = ShotKind.Break,
            Target = head,
            Ghost = ghost,
            CuePath = [cue.Position, ghost],
            Probability = 1,
            Description = "Break: hit the head ball full, hard",
        };
    }

    /// <summary>Nothing pots: aim to at least touch one of your own balls first.</summary>
    private static Shot? Safety(Context ctx, IReadOnlyList<Ball> targets)
    {
        var cue = ctx.Cue!;
        Shot? best = null;
        foreach (var target in targets)
        {
            var v = (target.Position - cue.Position).Normalized();
            var ghost = target.Position - v * (2 * ctx.R);
            if (!ctx.Clear(cue.Position, ghost, target.Id, ctx.CueId)) continue;

            var distance = Vec2.Distance(cue.Position, ghost);
            if (best is not null && distance >= Vec2.Distance(cue.Position, best.Ghost)) continue;

            best = new Shot
            {
                Kind = ShotKind.Safety,
                Target = target,
                Ghost = ghost,
                CuePath = [cue.Position, ghost],
                ObjectPath = [target.Position, target.Position + v * (6 * ctx.R)],
                Probability = 0,
                Description = $"No clean pot - safety: hit {target.Label} full",
            };
        }

        return best;
    }

    private static IEnumerable<Shot> BallInHand(Context ctx, IReadOnlyList<Ball> targets)
    {
        var r = ctx.R;
        foreach (var target in targets)
        {
            foreach (var pocket in ctx.Table.Pockets)
            {
                var b = target.Position;
                var toPocket = pocket.Position - b;
                var pocketDistance = toPocket.Length;
                if (pocketDistance < 1e-6) continue;

                var u = toPocket / pocketDistance;
                if (!PocketAccepts(pocket, u, out var pocketFactor)) continue;
                if (!ctx.Clear(b, pocket.Position, target.Id)) continue;

                var ghost = b - u * (2 * r);
                if (!ctx.GhostFits(ghost, target.Id)) continue;

                // Straight in from a few balls back leaves room for the cue.
                foreach (var back in new[] { 5.0, 3.5, 7.0 })
                {
                    var place = ghost - u * (back * r);
                    if (!ctx.Bounds.Inflate(-0.2 * r).Contains(place)) continue;
                    if (ctx.Balls.Any(o => Vec2.Distance(o.Position, place) < 2.1 * r)) continue;
                    if (!ctx.Clear(place, ghost, target.Id)) continue;

                    yield return new Shot
                    {
                        Kind = ShotKind.BallInHand,
                        Target = target,
                        Pocket = pocket,
                        Ghost = ghost,
                        CuePath = [place, ghost],
                        ObjectPath = [b, pocket.Position],
                        CutAngle = 0,
                        CuePlacement = place,
                        Probability = Chance(PocketTolerance(pocket, pocketFactor, pocketDistance, r), 0, back * r, r),
                        Description = $"Ball in hand: {target.Label} → {pocket.Name}",
                    };
                    break;
                }
            }
        }
    }

    // ---- Geometry helpers -------------------------------------------------

    /// <summary>
    /// Whether a ball travelling along <paramref name="travel"/> can drop, and
    /// how much of the pocket's width is left at that angle (1 = head on).
    /// </summary>
    internal static bool PocketAccepts(Pocket pocket, Vec2 travel, out double factor)
    {
        var approach = Geo.AngleBetween(-travel, pocket.Inward);
        var limit = pocket.IsSide ? 55.0 : 52.0;
        if (approach > limit)
        {
            factor = 0;
            return false;
        }

        var x = approach / limit;
        factor = 1 - 0.6 * x * x;
        return true;
    }

    /// <summary>Direction error (radians) the object ball can have and still drop.</summary>
    private static double PocketTolerance(Pocket pocket, double factor, double distance, double r)
    {
        var slack = (pocket.IsSide ? 0.75 : 0.9) * r * factor;
        return slack / Math.Max(distance, r);
    }

    /// <summary>
    /// Chance of making a shot: the object ball's allowed error, converted to
    /// cue-direction error (a cut multiplies error by 1/cos, distance by d/2r),
    /// against a fixed aiming precision.
    /// </summary>
    internal static double Chance(double objectTolerance, double cutDegrees, double cueDistance, double r)
    {
        var allowed = objectTolerance * 2 * r * Math.Cos(Geo.ToRadians(cutDegrees)) / Math.Max(cueDistance, 2 * r);
        return 1 - Math.Exp(-allowed / AimPrecision);
    }

    /// <summary>Where the segment from <paramref name="from"/> to a mirrored point crosses the cushion line.</summary>
    private static Vec2? Intersect(CushionLine cushion, Vec2 from, Vec2 mirrored)
    {
        var s0 = cushion.SignedDistance(from);
        var s1 = cushion.SignedDistance(mirrored);
        if (s0 <= 0.5 || s1 >= 0) return null;

        var t = s0 / (s0 - s1);
        return from + (mirrored - from) * t;
    }

    /// <summary>A bounce point must be on a real stretch of cushion, not in a pocket mouth.</summary>
    private static bool OnCushion(Context ctx, CushionLine cushion, Vec2 point)
    {
        var along = (cushion.B - cushion.A).Normalized();
        var t = (point - cushion.A).Dot(along);
        var length = Vec2.Distance(cushion.A, cushion.B);
        if (t < ctx.R || t > length - ctx.R) return false;
        return ctx.Table.Pockets.All(p => Vec2.Distance(p.Position, point) > 2.4 * ctx.R);
    }

    /// <summary>
    /// With no spin the cue ball leaves along the tangent line, at right
    /// angles to the object ball's path; on a full hit it stops. Traced to the
    /// first cushion or ball, and checked for running into a pocket.
    /// </summary>
    private static (IReadOnlyList<Vec2> Path, bool Scratch) CueAfterContact(
        Context ctx, Vec2 ghost, Vec2 cueDirection, Vec2 objectDirection, int hitId)
    {
        var tangent = cueDirection - objectDirection * cueDirection.Dot(objectDirection);
        if (tangent.Length < 0.05) return ([ghost], false);

        var dir = tangent.Normalized();
        var bounds = ctx.Bounds;

        // Distance to the first cushion along dir.
        var t = double.PositiveInfinity;
        if (dir.X > 1e-9) t = Math.Min(t, (bounds.Right - ghost.X) / dir.X);
        if (dir.X < -1e-9) t = Math.Min(t, (bounds.Left - ghost.X) / dir.X);
        if (dir.Y > 1e-9) t = Math.Min(t, (bounds.Bottom - ghost.Y) / dir.Y);
        if (dir.Y < -1e-9) t = Math.Min(t, (bounds.Top - ghost.Y) / dir.Y);
        if (double.IsInfinity(t) || t < 0) t = 0;

        var end = ghost + dir * t;

        // Stop at the first ball in the way.
        foreach (var ball in ctx.Balls)
        {
            if (ball.Id == hitId || ball.Id == ctx.CueId) continue;
            var along = (ball.Position - ghost).Dot(dir);
            if (along <= 0 || along > t) continue;
            var lateral = Math.Abs((ball.Position - ghost).Cross(dir));
            if (lateral >= 2 * ctx.R) continue;

            var back = Math.Sqrt(4 * ctx.R * ctx.R - lateral * lateral);
            var stop = along - back;
            if (stop < t)
            {
                t = Math.Max(0, stop);
                end = ghost + dir * t;
            }
        }

        var scratch = ctx.Table.Pockets.Any(p => Geo.DistanceToSegment(p.Position, ghost, end) < 1.6 * ctx.R);
        return ([ghost, end], scratch);
    }

    /// <summary>An intact rack: nearly all object balls packed into a small triangle.</summary>
    internal static bool IsRack(IReadOnlyList<Ball> balls, double r)
    {
        var objects = balls.Where(b => b.IsObjectBall).ToList();
        if (objects.Count < 14) return false;

        var centre = new Vec2(objects.Average(b => b.Position.X), objects.Average(b => b.Position.Y));
        return objects.All(b => Vec2.Distance(b.Position, centre) < 5.4 * r);
    }
}
