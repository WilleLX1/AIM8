using AIM8.Core.Geometry;
using AIM8.Core.Solver;

namespace AIM8.Core.Tests;

public sealed class SolverTests
{
    private const double R = 10;

    // A 2:1 table, 600 x 300, long side horizontal.
    private static readonly TableGeometry Table = TableGeometry.FromCushion(new RectD(0, 0, 600, 300));

    private static int _id;

    private static Ball Make(BallKind kind, double x, double y, int number = -1) =>
        new(++_id, new Vec2(x, y), kind, number, "#ffffff", 1);

    private static ShotPlan Solve(IReadOnlyList<Ball> balls, AimSettings? settings = null) =>
        new ShotSolver().Solve(Table, balls, R, settings ?? new AimSettings());

    [Fact]
    public void StraightInShotIsBest()
    {
        // Solid on the line from the cue ball to the top-left corner.
        var cue = Make(BallKind.Cue, 300, 150, 0);
        var target = Make(BallKind.Solid, 150, 75, 3);
        var plan = Solve([cue, target, Make(BallKind.Stripe, 500, 250, 11), Make(BallKind.Eight, 450, 60, 8)]);

        var best = plan.Best!;
        Assert.Equal(ShotKind.Direct, best.Kind);
        Assert.Equal(target.Id, best.Target.Id);
        Assert.Equal("top-left", best.Pocket!.Name);
        Assert.True(best.CutAngle < 1, $"cut {best.CutAngle}");

        // The ghost ball sits one diameter behind the object ball, away from the pocket.
        var expectedGhost = target.Position + (target.Position - best.Pocket.Position).Normalized() * (2 * R);
        Assert.True(Vec2.Distance(expectedGhost, best.Ghost) < 1e-6);
    }

    [Fact]
    public void OnlyShootsTheChosenGroup()
    {
        var cue = Make(BallKind.Cue, 300, 150, 0);
        var solid = Make(BallKind.Solid, 150, 75, 3);
        var stripe = Make(BallKind.Stripe, 450, 75, 11);

        var solids = Solve([cue, solid, stripe], new AimSettings { Team = Team.Solids });
        Assert.All(solids.Shots, s => Assert.Equal(solid.Id, s.Target.Id));

        var stripes = Solve([cue, solid, stripe], new AimSettings { Team = Team.Stripes });
        Assert.All(stripes.Shots, s => Assert.Equal(stripe.Id, s.Target.Id));
    }

    [Fact]
    public void GoesForTheEightWhenTheGroupIsCleared()
    {
        var cue = Make(BallKind.Cue, 300, 150, 0);
        var eight = Make(BallKind.Eight, 450, 225, 8);
        var plan = Solve([cue, eight, Make(BallKind.Stripe, 150, 75, 9)], new AimSettings { Team = Team.Solids });

        Assert.True(plan.OnEight);
        Assert.Equal(eight.Id, plan.Best!.Target.Id);
    }

    [Fact]
    public void AvoidsABlockedPath()
    {
        // Straight line cue -> target -> top-left is blocked by a stripe in front of the pocket.
        var cue = Make(BallKind.Cue, 300, 150, 0);
        var target = Make(BallKind.Solid, 150, 75, 3);
        var blocker = Make(BallKind.Stripe, 80, 40, 12);
        var plan = Solve([cue, target, blocker], new AimSettings { AllowBanks = false, AllowKicks = false, AllowCombos = false });

        // Top-left was the only direct pot from here, so what is left is a safety.
        Assert.All(plan.Shots, s => Assert.NotEqual("top-left", s.Pocket?.Name));
        Assert.Equal(ShotKind.Safety, plan.Best!.Kind);
        Assert.All(plan.Shots.Where(s => s.Pocket is not null), s =>
        {
            for (var i = 0; i + 1 < s.ObjectPath.Count; i++)
            {
                Assert.True(Geo.DistanceToSegment(blocker.Position, s.ObjectPath[i], s.ObjectPath[i + 1]) >= 2 * R * 0.98);
            }
        });
    }

    [Fact]
    public void KicksAroundABallInTheWay()
    {
        // Every direct line from the cue ball to the target is covered by a wall of stripes.
        var cue = Make(BallKind.Cue, 300, 250, 0);
        var target = Make(BallKind.Solid, 300, 60, 2);
        var wall = new List<Ball>();
        for (var x = 250; x <= 350; x += 21) wall.Add(Make(BallKind.Stripe, x, 160, 9 + wall.Count % 7));

        var plan = Solve([cue, target, .. wall], new AimSettings { AllowCombos = false });

        Assert.NotNull(plan.Best);
        Assert.Contains(plan.Shots, s => s.Kind == ShotKind.Kick || s.Kind == ShotKind.Bank || s.Kind == ShotKind.Safety);
        Assert.DoesNotContain(plan.Shots, s => s.Kind == ShotKind.Direct);
    }

    [Fact]
    public void BanksWhenTheOnlyWayIsOffACushion()
    {
        // Cue ball straight above the target: the top pockets can only be reached off a cushion.
        var cue = Make(BallKind.Cue, 300, 60, 0);
        var target = Make(BallKind.Solid, 300, 120, 4);
        var shots = Solve([cue, target], new AimSettings { AllowKicks = false, AllowCombos = false, Alternatives = 20 }).Shots;

        Assert.Contains(shots, s => s.Kind == ShotKind.Bank);
        var bank = shots.First(s => s.Kind == ShotKind.Bank);
        Assert.Equal(3, bank.ObjectPath.Count);

        // The bounce point is on a cushion line: one radius in from the cushion nose.
        var kiss = bank.ObjectPath[1];
        var bounds = Table.CenterBounds(R);
        var onLine = Math.Abs(kiss.X - bounds.Left) < 1e-6 || Math.Abs(kiss.X - bounds.Right) < 1e-6 ||
                     Math.Abs(kiss.Y - bounds.Top) < 1e-6 || Math.Abs(kiss.Y - bounds.Bottom) < 1e-6;
        Assert.True(onLine, $"bounce at {kiss}");
    }

    [Fact]
    public void FindsACombination()
    {
        // Solid A lined up behind solid B, which is lined up with the top-right pocket.
        var cue = Make(BallKind.Cue, 300, 250, 0);
        var b = Make(BallKind.Solid, 520, 50, 1);
        var direction = (b.Position - Table.Pockets.First(p => p.Name == "top-right").Position).Normalized();
        var a = Make(BallKind.Solid, b.Position.X + direction.X * 60, b.Position.Y + direction.Y * 60, 5);
        var shots = Solve([cue, a, b], new AimSettings { Alternatives = 30 }).Shots;

        Assert.Contains(shots, s => s.Kind == ShotKind.Combo && s.First!.Id == a.Id && s.Target.Id == b.Id);
    }

    [Fact]
    public void SuggestsPlacementWithBallInHand()
    {
        var target = Make(BallKind.Solid, 150, 75, 3);
        var plan = Solve([target, Make(BallKind.Eight, 450, 225, 8)]);

        var best = plan.Best!;
        Assert.Equal(ShotKind.BallInHand, best.Kind);
        Assert.NotNull(best.CuePlacement);
        Assert.True(Table.CenterBounds(R).Contains(best.CuePlacement!.Value));
    }

    [Fact]
    public void RecognisesTheBreak()
    {
        var balls = new List<Ball> { Make(BallKind.Cue, 450, 150, 0) };
        var number = 1;
        for (var row = 0; row < 5; row++)
        {
            for (var i = 0; i <= row; i++)
            {
                var kind = number == 8 ? BallKind.Eight : number < 8 ? BallKind.Solid : BallKind.Stripe;
                balls.Add(Make(kind, 150 - row * 17.4, 150 + (i - row / 2.0) * 20.2, number++));
            }
        }

        var plan = Solve(balls);
        Assert.Equal(ShotKind.Break, plan.Best!.Kind);
        Assert.Equal(150, plan.Best.Target.Position.X, 3);
    }

    [Fact]
    public void ThinCutsScoreLowerThanFullHits()
    {
        var full = ShotSolver.Chance(0.05, 0, 200, R);
        var thin = ShotSolver.Chance(0.05, 60, 200, R);
        var far = ShotSolver.Chance(0.05, 0, 400, R);

        Assert.True(full > thin);
        Assert.True(full > far);
    }

    [Fact]
    public void SidePocketsRefuseSteepAngles()
    {
        var side = Table.Pockets.First(p => p.IsSide && p.Name == "top side");

        Assert.True(ShotSolver.PocketAccepts(side, new Vec2(0, -1), out var straight));
        Assert.Equal(1, straight, 3);

        // Travelling almost along the top cushion.
        Assert.False(ShotSolver.PocketAccepts(side, new Vec2(1, -0.2).Normalized(), out _));
    }
}
