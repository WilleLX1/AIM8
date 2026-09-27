using AIM8.Core.Geometry;
using AIM8.Core.Vision;

namespace AIM8.Core.Demo;

public readonly record struct Rgb(byte R, byte G, byte B);

/// <param name="Number">0 cue, 1-7 solids, 8, 9-15 stripes.</param>
/// <param name="Angle">Rotation of the ball's pattern, radians.</param>
public sealed record DemoBall(Vec2 Position, int Number, double Angle = 0)
{
    public BallKind Kind => Number switch
    {
        0 => BallKind.Cue,
        8 => BallKind.Eight,
        < 8 => BallKind.Solid,
        _ => BallKind.Stripe,
    };
}

/// <param name="Direction">Unit vector from the tip towards the butt.</param>
public sealed record DemoStick(Vec2 Tip, Vec2 Direction, double Length, double TipWidth, double ButtWidth);

/// <summary>A mobile-game-looking pool screen with known ball positions.</summary>
public sealed record TableScene
{
    public int Width { get; init; } = 828;

    public int Height { get; init; } = 1792;

    /// <summary>The cushion-nose rectangle: what the detector should find.</summary>
    public RectD Cushion { get; init; }

    public double BallRadius { get; init; }

    public Rgb Felt { get; init; } = new(38, 120, 72);

    public Rgb Background { get; init; } = new(22, 27, 36);

    public Rgb Rail { get; init; } = new(104, 60, 30);

    /// <summary>Width of the cushion strip, drawn as darker felt between rail and nose.</summary>
    public double CushionWidth { get; init; }

    public bool Shadows { get; init; } = true;

    /// <summary>Uniform per-channel noise amplitude, like video compression grain.</summary>
    public int Noise { get; init; } = 3;

    public int Seed { get; init; } = 1;

    public IReadOnlyList<DemoBall> Balls { get; init; } = [];

    public DemoStick? Stick { get; init; }

    /// <summary>The game's own aim guide: a thin line ending in a hollow circle.</summary>
    public (Vec2 From, Vec2 To)? AimGuide { get; init; }

    public static readonly IReadOnlyDictionary<int, Rgb> BallColors = new Dictionary<int, Rgb>
    {
        [1] = new(250, 196, 18),
        [2] = new(22, 70, 205),
        [3] = new(222, 32, 30),
        [4] = new(112, 40, 152),
        [5] = new(246, 118, 18),
        [6] = new(0, 112, 46),
        [7] = new(128, 24, 30),
    };
}

public static class TableRenderer
{
    private static readonly Rgb CueWhite = new(244, 242, 232);
    private static readonly Rgb White = new(248, 248, 244);
    private static readonly Rgb Black = new(22, 22, 26);

    /// <summary>A random mid-game layout in portrait, the way GamePigeon-style games show the table.</summary>
    public static TableScene RandomScene(
        int seed, int width = 828, int height = 1792, bool stick = true, bool guide = true, bool stickOverBall = false)
    {
        var random = new Random(seed);
        var cushion = PortraitCushion(width, height);
        var r = cushion.ShortSide / 30;

        var numbers = new List<int> { 0, 8 };
        numbers.AddRange(Enumerable.Range(1, 7).OrderBy(_ => random.Next()).Take(random.Next(2, 8)));
        numbers.AddRange(Enumerable.Range(9, 7).OrderBy(_ => random.Next()).Take(random.Next(2, 8)));

        var inner = cushion.Inflate(-r * 1.3);
        var placed = new List<DemoBall>();
        foreach (var number in numbers)
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                Vec2 p;
                if (placed.Count > 2 && random.NextDouble() < 0.25)
                {
                    // Frozen to another ball: the case the detector must split.
                    var other = placed[random.Next(placed.Count)].Position;
                    p = other + Vec2.FromAngle(random.NextDouble() * Math.PI * 2) * (2 * r + 0.6);
                }
                else
                {
                    p = new Vec2(inner.Left + random.NextDouble() * inner.Width, inner.Top + random.NextDouble() * inner.Height);
                }

                if (!inner.Contains(p) || placed.Any(b => Vec2.Distance(b.Position, p) < 2 * r + 0.5)) continue;
                placed.Add(new DemoBall(p, number, random.NextDouble() * Math.PI));
                break;
            }
        }

        var cueBall = placed.First(b => b.Number == 0).Position;

        // Point the stick where it covers no other ball, as it is while you
        // line a shot up; RandomScene with stickOverBall covers that case.
        var aim = Vec2.FromAngle(random.NextDouble() * Math.PI * 2);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var candidate = Vec2.FromAngle(random.NextDouble() * Math.PI * 2);
            var tip = cueBall - candidate * (1.7 * r);
            var butt = tip - candidate * (34 * r);
            var covers = placed.Any(b => b.Number != 0 && Geo.DistanceToSegment(b.Position, tip, butt) < 1.7 * r);
            aim = candidate;
            if (covers == stickOverBall) break;
        }

        return new TableScene
        {
            Width = width,
            Height = height,
            Cushion = cushion,
            BallRadius = r,
            CushionWidth = r * 0.9,
            Seed = seed,
            Balls = placed,
            Stick = stick ? new DemoStick(cueBall - aim * (1.7 * r), -aim, 34 * r, 0.55 * r, 1.05 * r) : null,
            AimGuide = guide ? (cueBall + aim * r, cueBall + aim * (9 * r)) : null,
        };
    }

    /// <summary>A full rack and the cue ball behind the head string.</summary>
    public static TableScene RackScene(int width = 828, int height = 1792)
    {
        var cushion = PortraitCushion(width, height);
        var r = cushion.ShortSide / 30;
        var apex = new Vec2(cushion.Center.X, cushion.Top + cushion.Height * 0.3);
        var order = new[] { 1, 9, 2, 10, 8, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15 };
        var balls = new List<DemoBall>();
        var index = 0;
        for (var row = 0; row < 5; row++)
        {
            for (var i = 0; i <= row; i++)
            {
                var x = apex.X + (i - row / 2.0) * (2 * r + 0.8);
                var y = apex.Y - row * Math.Sqrt(3) * (r + 0.4);
                balls.Add(new DemoBall(new Vec2(x, y), order[index++], index * 0.7));
            }
        }

        balls.Add(new DemoBall(new Vec2(cushion.Center.X, cushion.Top + cushion.Height * 0.75), 0));

        return new TableScene
        {
            Width = width,
            Height = height,
            Cushion = cushion,
            BallRadius = r,
            CushionWidth = r * 0.9,
            Balls = balls,
        };
    }

    public static RectD PortraitCushion(int width, int height)
    {
        var tableWidth = width * 0.76;
        var tableHeight = Math.Min(tableWidth * 1.96, height * 0.78);
        return new RectD((width - tableWidth) / 2, height * 0.54 - tableHeight / 2, tableWidth, tableHeight);
    }

    public static Frame Render(TableScene scene)
    {
        var w = scene.Width;
        var h = scene.Height;
        var img = new float[w * h * 3];
        var r = scene.BallRadius;
        var cushion = scene.Cushion;
        var cushionOuter = cushion.Inflate(scene.CushionWidth);
        var railOuter = cushionOuter.Inflate(r * 1.6);

        // Background, rail, cushion strip and bed.
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = new Vec2(x + 0.5, y + 0.5);
                Rgb c;
                double k;
                if (cushion.Contains(p))
                {
                    c = scene.Felt;
                    var off = (p - cushion.Center);
                    var falloff = (off.X * off.X) / (cushion.Width * cushion.Width) + (off.Y * off.Y) / (cushion.Height * cushion.Height);
                    k = 1.04 - 0.16 * falloff;
                }
                else if (cushionOuter.Contains(p))
                {
                    c = scene.Felt;
                    k = 0.68;
                }
                else if (railOuter.Contains(p))
                {
                    c = scene.Rail;
                    k = 0.9 + 0.1 * Math.Sin(y * 0.05 + x * 0.013);
                }
                else
                {
                    c = scene.Background;
                    k = 1 - 0.25 * y / h;
                }

                Set(img, w, x, y, c.R * k, c.G * k, c.B * k);
            }
        }

        // Pockets cut into the corners and the middle of the long sides.
        var geometry = TableGeometry.FromCushion(cushion);
        foreach (var pocket in geometry.Pockets)
        {
            var centre = pocket.Position - pocket.Inward * (pocket.IsSide ? 0.9 * r : 0.7 * r);
            Disc(img, w, h, centre, pocket.IsSide ? 1.6 * r : 1.85 * r, (_, _) => (8, 8, 10), 1);
        }

        if (scene.Shadows)
        {
            foreach (var ball in scene.Balls)
            {
                var centre = ball.Position + new Vec2(0.18 * r, 0.22 * r);
                var extent = 1.05 * r;
                ForBox(w, h, centre, extent + 2, (x, y) =>
                {
                    var d = Vec2.Distance(new Vec2(x + 0.5, y + 0.5), centre);
                    if (d > extent + 1) return;
                    var strength = 0.5 * Math.Clamp((extent + 1 - d) / 3, 0, 1);
                    Scale(img, w, x, y, 1 - strength);
                });
            }
        }

        foreach (var ball in scene.Balls) DrawBall(img, w, h, ball, r);

        if (scene.AimGuide is { } guide)
        {
            Line(img, w, h, guide.From, guide.To, 1.0, (250, 250, 250), 0.85);
            Ring(img, w, h, guide.To, r, 0.8, (250, 250, 250), 0.85);
        }

        if (scene.Stick is { } stick) DrawStick(img, w, h, stick);

        // A game HUD above the table: avatars and the pocketed-ball tray.
        FillRect(img, w, h, new RectD(0, 0, w, h * 0.075), (14, 17, 24));
        Disc(img, w, h, new Vec2(w * 0.1, h * 0.04), h * 0.025, (_, _) => (70, 140, 230), 1);
        Disc(img, w, h, new Vec2(w * 0.9, h * 0.04), h * 0.025, (_, _) => (230, 90, 70), 1);
        for (var i = 0; i < 7; i++)
        {
            var colour = TableScene.BallColors[i + 1];
            Disc(img, w, h, new Vec2(w * 0.25 + i * r * 2.4, h * 0.04), r * 0.8, (_, _) => (colour.R, colour.G, colour.B), 1);
        }

        var random = new Random(scene.Seed);
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var noise = scene.Noise > 0 ? random.Next(-scene.Noise, scene.Noise + 1) : 0;
                pixels[i * 4 + c] = (byte)Math.Clamp(Math.Round(img[i * 3 + c] + noise), 0, 255);
            }

            pixels[i * 4 + 3] = 255;
        }

        return new Frame(pixels, w, h);
    }

    private static void DrawBall(float[] img, int w, int h, DemoBall ball, double r)
    {
        var light = Normalize(-0.4, -0.5, 0.77);
        var cos = Math.Cos(ball.Angle);
        var sin = Math.Sin(ball.Angle);
        var colour = ball.Number switch
        {
            0 => CueWhite,
            8 => Black,
            < 8 => TableScene.BallColors[ball.Number],
            _ => TableScene.BallColors[ball.Number - 8],
        };

        ForBox(w, h, ball.Position, r + 2, (x, y) =>
        {
            var dx = (x + 0.5 - ball.Position.X) / r;
            var dy = (y + 0.5 - ball.Position.Y) / r;
            var d = Math.Sqrt(dx * dx + dy * dy) * r;
            var alpha = Math.Clamp(r + 0.5 - d, 0, 1);
            if (alpha <= 0) return;

            var rr = Math.Min(1, dx * dx + dy * dy);
            var nz = Math.Sqrt(1 - rr);
            var diffuse = Math.Max(0, dx * light.X + dy * light.Y + nz * light.Z);
            var shade = 0.32 + 0.78 * diffuse;

            // Pattern coordinates rotate with the ball.
            var px = dx * cos - dy * sin;
            var py = dx * sin + dy * cos;

            Rgb surface;
            switch (ball.Number)
            {
                case 0:
                    surface = CueWhite;
                    break;
                case 8:
                    surface = Sq(px - 0.12, py + 0.08) < 0.42 * 0.42 ? White : Black;
                    break;
                case < 8:
                    surface = Sq(px - 0.1, py + 0.12) < 0.36 * 0.36 ? White : colour;
                    break;
                default:
                    surface = Math.Abs(py) < 0.5
                        ? (Sq(px - 0.08, py) < 0.3 * 0.3 ? White : colour)
                        : White;
                    break;
            }

            var highlight = 0.85 * Math.Exp(-(Sq(dx + 0.36, dy + 0.42)) / 0.018);
            var br = Math.Min(255, surface.R * shade + 255 * highlight);
            var bg = Math.Min(255, surface.G * shade + 255 * highlight);
            var bb = Math.Min(255, surface.B * shade + 255 * highlight);
            Blend(img, w, x, y, br, bg, bb, alpha);
        });
    }

    private static void DrawStick(float[] img, int w, int h, DemoStick stick)
    {
        var end = stick.Tip + stick.Direction * stick.Length;
        var centre = (stick.Tip + end) / 2;
        var normal = stick.Direction.Perpendicular;

        ForBox(w, h, centre, stick.Length / 2 + stick.ButtWidth, (x, y) =>
        {
            var p = new Vec2(x + 0.5, y + 0.5) - stick.Tip;
            var t = p.Dot(stick.Direction);
            if (t < 0 || t > stick.Length) return;

            var f = t / stick.Length;
            var half = (stick.TipWidth + (stick.ButtWidth - stick.TipWidth) * f) / 2;
            var lateral = Math.Abs(p.Dot(normal));
            var alpha = Math.Clamp(half + 0.5 - lateral, 0, 1);
            if (alpha <= 0) return;

            (double R, double G, double B) c = f < 0.025
                ? (70, 110, 170)
                : f < 0.05
                    ? (232, 226, 210)
                    : f < 0.7 ? (196, 146, 84) : (70, 34, 18);
            var shade = 0.75 + 0.25 * (1 - lateral / Math.Max(half, 1));
            Blend(img, w, x, y, c.R * shade, c.G * shade, c.B * shade, alpha);
        });
    }

    private static void Line(float[] img, int w, int h, Vec2 a, Vec2 b, double halfWidth, (double R, double G, double B) c, double opacity)
    {
        var centre = (a + b) / 2;
        ForBox(w, h, centre, Vec2.Distance(a, b) / 2 + halfWidth + 2, (x, y) =>
        {
            var d = Geo.DistanceToSegment(new Vec2(x + 0.5, y + 0.5), a, b);
            var alpha = Math.Clamp(halfWidth + 0.5 - d, 0, 1) * opacity;
            if (alpha > 0) Blend(img, w, x, y, c.R, c.G, c.B, alpha);
        });
    }

    private static void Ring(float[] img, int w, int h, Vec2 centre, double radius, double halfWidth, (double R, double G, double B) c, double opacity)
    {
        ForBox(w, h, centre, radius + halfWidth + 2, (x, y) =>
        {
            var d = Math.Abs(Vec2.Distance(new Vec2(x + 0.5, y + 0.5), centre) - radius);
            var alpha = Math.Clamp(halfWidth + 0.5 - d, 0, 1) * opacity;
            if (alpha > 0) Blend(img, w, x, y, c.R, c.G, c.B, alpha);
        });
    }

    private static void Disc(float[] img, int w, int h, Vec2 centre, double radius, Func<int, int, (double, double, double)> colour, double opacity)
    {
        ForBox(w, h, centre, radius + 2, (x, y) =>
        {
            var d = Vec2.Distance(new Vec2(x + 0.5, y + 0.5), centre);
            var alpha = Math.Clamp(radius + 0.5 - d, 0, 1) * opacity;
            if (alpha <= 0) return;
            var (cr, cg, cb) = colour(x, y);
            Blend(img, w, x, y, cr, cg, cb, alpha);
        });
    }

    private static void FillRect(float[] img, int w, int h, RectD rect, (double R, double G, double B) c)
    {
        for (var y = Math.Max(0, (int)rect.Top); y < Math.Min(h, (int)rect.Bottom); y++)
        {
            for (var x = Math.Max(0, (int)rect.Left); x < Math.Min(w, (int)rect.Right); x++)
            {
                Set(img, w, x, y, c.R, c.G, c.B);
            }
        }
    }

    private static void ForBox(int w, int h, Vec2 centre, double extent, Action<int, int> action)
    {
        var x0 = Math.Max(0, (int)Math.Floor(centre.X - extent));
        var x1 = Math.Min(w - 1, (int)Math.Ceiling(centre.X + extent));
        var y0 = Math.Max(0, (int)Math.Floor(centre.Y - extent));
        var y1 = Math.Min(h - 1, (int)Math.Ceiling(centre.Y + extent));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++) action(x, y);
        }
    }

    private static void Set(float[] img, int w, int x, int y, double r, double g, double b)
    {
        var i = (y * w + x) * 3;
        img[i] = (float)r;
        img[i + 1] = (float)g;
        img[i + 2] = (float)b;
    }

    private static void Scale(float[] img, int w, int x, int y, double k)
    {
        var i = (y * w + x) * 3;
        img[i] = (float)(img[i] * k);
        img[i + 1] = (float)(img[i + 1] * k);
        img[i + 2] = (float)(img[i + 2] * k);
    }

    private static void Blend(float[] img, int w, int x, int y, double r, double g, double b, double alpha)
    {
        var i = (y * w + x) * 3;
        img[i] = (float)(img[i] * (1 - alpha) + r * alpha);
        img[i + 1] = (float)(img[i + 1] * (1 - alpha) + g * alpha);
        img[i + 2] = (float)(img[i + 2] * (1 - alpha) + b * alpha);
    }

    private static double Sq(double a, double b) => a * a + b * b;

    private static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        var l = Math.Sqrt(x * x + y * y + z * z);
        return (x / l, y / l, z / l);
    }
}
