using System.Text.Json;
using AIM8.Core.Demo;
using AIM8.Core.Geometry;
using AIM8.Core.Vision;

namespace AIM8.Core.Tests;

public sealed class EngineTests
{
    [Fact]
    public void PlansOnceTheTableIsStill()
    {
        var frame = TableRenderer.Render(TableRenderer.RandomScene(4));
        var engine = new AimEngine { Settings = new AimSettings { Team = Team.Stripes } };

        var first = engine.Process(frame);
        Assert.Equal(AnalysisStatus.Moving, first.Status);

        var second = engine.Process(frame);
        Assert.Equal(AnalysisStatus.Ready, second.Status);
        Assert.NotNull(second.Plan.Best);
        Assert.All(second.Plan.Targets, b => Assert.Equal(BallKind.Stripe, b.Kind));
    }

    [Fact]
    public void BallsThatMoveResetTheWait()
    {
        var scene = TableRenderer.RandomScene(6);
        var engine = new AimEngine();
        engine.Process(TableRenderer.Render(scene));
        Assert.Equal(AnalysisStatus.Ready, engine.Process(TableRenderer.Render(scene)).Status);

        var moved = scene with
        {
            Balls = scene.Balls.Select((b, i) => i == 0 ? b with { Position = b.Position + new Vec2(0, scene.BallRadius) } : b).ToList(),
        };
        Assert.Equal(AnalysisStatus.Moving, engine.Process(TableRenderer.Render(moved)).Status);
    }

    [Fact]
    public void SwitchingTeamChangesTheTargets()
    {
        var frame = TableRenderer.Render(TableRenderer.RandomScene(9));
        var engine = new AimEngine();
        engine.Process(frame);

        engine.Settings = new AimSettings { Team = Team.Solids };
        var solids = engine.Process(frame);
        engine.Settings = new AimSettings { Team = Team.Stripes };
        var stripes = engine.Process(frame);

        Assert.All(solids.Plan.Targets, b => Assert.Equal(BallKind.Solid, b.Kind));
        Assert.All(stripes.Plan.Targets, b => Assert.Equal(BallKind.Stripe, b.Kind));
    }

    [Fact]
    public void ReportsNoTableOnAMenuScreen()
    {
        var pixels = new byte[400 * 800 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4 % 400 / 2);
            pixels[i + 1] = 30;
            pixels[i + 2] = (byte)(i / 4 / 400 % 200);
            pixels[i + 3] = 255;
        }

        var result = new AimEngine().Process(new Frame(pixels, 400, 800));
        Assert.Equal(AnalysisStatus.NoTable, result.Status);
    }

    [Fact]
    public void UsesAHandCalibratedTable()
    {
        var scene = TableRenderer.RandomScene(12);
        var c = scene.Cushion;
        var manual = new NormalizedRect(c.Left / scene.Width, c.Top / scene.Height, c.Right / scene.Width, c.Bottom / scene.Height);
        var engine = new AimEngine { Settings = new AimSettings { ManualTable = manual } };
        var frame = TableRenderer.Render(scene);

        engine.Process(frame);
        var result = engine.Process(frame);

        Assert.True(result.ManualTable);
        Assert.Equal(scene.Balls.Count, result.Balls.Count);
    }

    [Fact]
    public void OverlayJsonCarriesThePlan()
    {
        var frame = TableRenderer.Render(TableRenderer.RandomScene(4));
        var engine = new AimEngine();
        engine.Process(frame);
        var result = engine.Process(frame);

        using var json = JsonDocument.Parse(OverlayJson.Serialize(result, 42));
        var root = json.RootElement;

        Assert.Equal("result", root.GetProperty("type").GetString());
        Assert.Equal(42, root.GetProperty("seq").GetInt64());
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal(result.Balls.Count, root.GetProperty("balls").GetArrayLength());
        Assert.Equal(6, root.GetProperty("table").GetProperty("pockets").GetArrayLength());

        var shot = root.GetProperty("shots")[0];
        Assert.True(shot.GetProperty("cue").GetArrayLength() >= 2);
        Assert.Equal(2, shot.GetProperty("ghost").GetArrayLength());
    }

    [Fact]
    public void RunsFastEnoughForLiveUse()
    {
        var frame = TableRenderer.Render(TableRenderer.RandomScene(4));
        var engine = new AimEngine();
        engine.Process(frame);
        engine.Process(frame);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) engine.Process(frame);
        var perFrame = clock.Elapsed.TotalMilliseconds / 5;

        // 828 x 1792 in a debug build, on a machine that may be busy; a Release
        // build runs this in about a third of the time.
        Assert.True(perFrame < 400, $"{perFrame:0} ms per frame");
    }
}
