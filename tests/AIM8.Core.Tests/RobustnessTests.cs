using AIM8.Core.Demo;
using AIM8.Core.Geometry;
using AIM8.Core.Vision;
using Xunit.Abstractions;

namespace AIM8.Core.Tests;

/// <summary>
/// Many random layouts at once, scored rather than all-or-nothing: the
/// numbers here are what to watch when changing the detector.
/// </summary>
public sealed class RobustnessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("green", false, 0.99)]
    [InlineData("blue", false, 0.99)]
    [InlineData("green", true, 0.96)]
    public void NamesAlmostEveryBall(string cloth, bool stickOverBall, double required)
    {
        var settings = new AimSettings();
        int total = 0, right = 0, extra = 0;

        for (var seed = 100; seed < 140; seed++)
        {
            var scene = TableRenderer.RandomScene(seed, stickOverBall: stickOverBall);
            if (cloth == "blue") scene = scene with { Felt = new Rgb(30, 96, 168) };
            var frame = TableRenderer.Render(scene);

            var table = new TableDetector().Detect(frame, settings);
            Assert.True(table is not null, $"seed {seed}: no table");

            var detection = new BallDetector().Detect(frame, table!.Felt, table.Geometry, null, settings);
            var found = BallClassifier.Classify(frame, detection.Balls, detection.Radius, settings, detection.Occluders);

            foreach (var truth in scene.Balls)
            {
                total++;
                var match = found.MinBy(f => Vec2.Distance(f.Position, truth.Position));
                var located = match is not null && Vec2.Distance(match.Position, truth.Position) <= scene.BallRadius * 0.2;
                if (located && match!.Kind == truth.Kind && match.Number == truth.Number)
                {
                    right++;
                    continue;
                }

                output.WriteLine($"seed {seed}: ball {truth.Number} -> {(match is null ? "none" : $"{match.Kind} #{match.Number} {Vec2.Distance(match.Position, truth.Position):0.0}px")}");
            }

            extra += found.Count(f => scene.Balls.All(t => Vec2.Distance(t.Position, f.Position) > scene.BallRadius * 0.5));
        }

        var share = right / (double)total;
        output.WriteLine($"{cloth}, stick over a ball: {stickOverBall} - {right}/{total} right ({share:P1}), {extra} phantom balls");
        Assert.True(share >= required, $"only {share:P1} of balls found and named");
        Assert.Equal(0, extra);
    }
}
