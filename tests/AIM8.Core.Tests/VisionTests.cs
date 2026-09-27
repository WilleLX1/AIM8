using AIM8.Core.Demo;
using AIM8.Core.Geometry;
using AIM8.Core.Vision;
using Xunit.Abstractions;

namespace AIM8.Core.Tests;

public sealed class VisionTests(ITestOutputHelper output)
{
    private static readonly AimSettings Defaults = new();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FindsTheCushionRectangle(int seed)
    {
        var scene = TableRenderer.RandomScene(seed);
        var detection = new TableDetector().Detect(TableRenderer.Render(scene), Defaults);

        Assert.NotNull(detection);
        var found = detection!.Geometry.Cushion;
        output.WriteLine($"expected {scene.Cushion}, found {found}");

        var tolerance = scene.BallRadius * 0.25;
        Assert.InRange(found.Left, scene.Cushion.Left - tolerance, scene.Cushion.Left + tolerance);
        Assert.InRange(found.Right, scene.Cushion.Right - tolerance, scene.Cushion.Right + tolerance);
        Assert.InRange(found.Top, scene.Cushion.Top - tolerance, scene.Cushion.Top + tolerance);
        Assert.InRange(found.Bottom, scene.Cushion.Bottom - tolerance, scene.Cushion.Bottom + tolerance);
    }

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (var seed = 1; seed <= 24; seed++) data.Add(seed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void FindsAndNamesEveryBall(int seed)
    {
        var scene = TableRenderer.RandomScene(seed);
        AssertScene(scene);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(11)]
    public void WorksOnBlueCloth(int seed)
    {
        var scene = TableRenderer.RandomScene(seed) with { Felt = new Rgb(30, 96, 168) };
        AssertScene(scene);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void WorksInLandscape(int seed)
    {
        var portrait = TableRenderer.RandomScene(seed);

        // Rotate the portrait scene a quarter turn: 8 Ball Pool style.
        var width = portrait.Height;
        var height = portrait.Width;
        Vec2 Rotate(Vec2 p) => new(p.Y, portrait.Width - p.X);
        var c = portrait.Cushion;
        var scene = portrait with
        {
            Width = width,
            Height = height,
            Cushion = RectD.FromEdges(c.Top, portrait.Width - c.Right, c.Bottom, portrait.Width - c.Left),
            Balls = portrait.Balls.Select(b => b with { Position = Rotate(b.Position) }).ToList(),
            Stick = null,
            AimGuide = null,
        };

        AssertScene(scene);
    }

    [Fact]
    public void SplitsAFullRack()
    {
        var scene = TableRenderer.RackScene();
        AssertScene(scene);
    }

    [Fact]
    public void IgnoresTheCueStickAndAimGuide()
    {
        var withStick = TableRenderer.RandomScene(3, stick: true, guide: true);
        var frame = TableRenderer.Render(withStick);
        var result = DetectAll(frame);

        Assert.Equal(withStick.Balls.Count, result.Count);
    }

    [Fact]
    public void PocketMouthIsNotTheEightBall()
    {
        const double radius = 12;
        var cushion = new RectD(65, 45, 470, 210);
        var table = TableGeometry.FromCushion(cushion);
        var pocket = table.Pockets.First(p => p.Name == "top-left");
        var otherPocket = table.Pockets.First(p => p.Name == "bottom-right");
        var eight = otherPocket.Position + otherPocket.Inward * (1.8 * radius);
        var scene = new TableScene
        {
            Width = 600,
            Height = 300,
            Cushion = cushion,
            BallRadius = radius,
            CushionWidth = radius,
            Balls = [new DemoBall(eight, 8)],
            Noise = 0,
        };
        var frame = TableRenderer.Render(scene);
        var darkMouth = pocket.Position - pocket.Inward * (0.7 * radius);

        var result = BallClassifier.Classify(frame,
            [new DetectedBall(darkMouth, 1), new DetectedBall(eight, 1)],
            radius, Defaults, table: table);

        Assert.Single(result);
        Assert.Equal(BallKind.Eight, result[0].Kind);
        Assert.True(Vec2.Distance(eight, result[0].Position) < 0.1);
    }

    [Fact]
    public void RecoversCueWhenItsRoundOutlineWasNotDetected()
    {
        var scene = TableRenderer.RandomScene(3, stick: true, guide: true);
        var frame = TableRenderer.Render(scene);
        var table = new TableDetector().Detect(frame, Defaults)!;
        var detection = new BallDetector().Detect(frame, table.Felt, table.Geometry, null, Defaults);
        var cuePosition = scene.Balls.Single(ball => ball.Number == 0).Position;
        var withoutCue = detection.Balls
            .Where(ball => Vec2.Distance(ball.Position, cuePosition) > 1.8 * scene.BallRadius)
            .ToList();

        var rescued = CueBallFinder.Find(frame, table.Geometry, detection.Radius, withoutCue, detection.Occluders);

        var look = BallClassifier.Measure(frame, cuePosition, detection.Radius, detection.Occluders);
        Assert.True(rescued is not null,
            $"cue {cuePosition}, radius {detection.Radius:0.0}, white {look.White:0.00}, outer {look.OuterWhite:0.00}, " +
            $"coloured {look.Colored:0.00}, black {look.Black:0.00}, rim {look.RimColored:0.00}, " +
            $"visible {look.Visible:0.00}, nearest {withoutCue.Min(ball => Vec2.Distance(ball.Position, cuePosition)):0.0}, bounds {table.Geometry.Cushion}");
        Assert.Equal(BallKind.Cue, rescued.Kind);
        Assert.True(Vec2.Distance(cuePosition, rescued.Position) < scene.BallRadius * 0.35);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(18)]
    public void CueRecoveryDoesNotMistakeStripesOrAimGuideForCue(int seed)
    {
        var original = TableRenderer.RandomScene(seed, stick: false, guide: true);
        var scene = original with { Balls = original.Balls.Where(ball => ball.Number != 0).ToList() };
        var frame = TableRenderer.Render(scene);
        var table = new TableDetector().Detect(frame, Defaults)!;
        var detection = new BallDetector().Detect(frame, table.Felt, table.Geometry, null, Defaults);

        Assert.Null(CueBallFinder.Find(frame, table.Geometry, detection.Radius, detection.Balls, detection.Occluders));
    }

    private void AssertScene(TableScene scene)
    {
        var frame = TableRenderer.Render(scene);
        var found = DetectAll(frame);

        foreach (var ball in found)
        {
            output.WriteLine($"found {ball.Kind} #{ball.Number} at {ball.Position} white={ball.Look.White:0.00} outer={ball.Look.OuterWhite:0.00} rimcol={ball.Look.RimColored:0.00} score={ball.Look.StripeScore:0.00} " +
                             $"black={ball.Look.Black:0.00} col={ball.Look.Colored:0.00} hue={ball.Look.Hue:0} v={ball.Look.Value:0.00}");
        }

        var tolerance = scene.BallRadius * 0.12;
        foreach (var expected in scene.Balls)
        {
            var match = found.MinBy(f => Vec2.Distance(f.Position, expected.Position));
            Assert.True(match is not null, $"ball {expected.Number} not found");
            var distance = Vec2.Distance(match!.Position, expected.Position);
            Assert.True(distance <= tolerance,
                $"ball {expected.Number} at {expected.Position} found {distance:0.0}px away at {match.Position}");
            Assert.True(expected.Kind == match.Kind,
                $"ball {expected.Number} at {expected.Position} classified {match.Kind} #{match.Number}");
            Assert.True(expected.Number == match.Number,
                $"ball {expected.Number} at {expected.Position} numbered {match.Number}");
        }

        Assert.Equal(scene.Balls.Count, found.Count);
    }

    private static IReadOnlyList<ClassifiedBall> DetectAll(Frame frame)
    {
        var table = new TableDetector().Detect(frame, Defaults);
        Assert.NotNull(table);
        var detection = new BallDetector().Detect(frame, table!.Felt, table.Geometry, null, Defaults);
        return BallClassifier.Classify(frame, detection.Balls, detection.Radius, Defaults);
    }
}
