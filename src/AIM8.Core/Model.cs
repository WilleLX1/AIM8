using AIM8.Core.Geometry;

namespace AIM8.Core;

public enum BallKind
{
    Unknown,
    Cue,
    Eight,

    /// <summary>1-7, "whole" balls.</summary>
    Solid,

    /// <summary>9-15, "half" balls.</summary>
    Stripe,
}

/// <summary>The group the player is shooting at.</summary>
public enum Team
{
    /// <summary>Whole balls, 1-7.</summary>
    Solids,

    /// <summary>Half balls, 9-15.</summary>
    Stripes,

    /// <summary>Groups not decided yet: any solid or stripe is a legal target.</summary>
    Open,
}

/// <summary>A ball as the assist understands it after detection, classification and tracking.</summary>
/// <param name="Id">Stable across frames while the ball stays put.</param>
/// <param name="Number">1-15 when the colour could be read, 0 for the cue ball, -1 when unknown.</param>
/// <param name="Color">Measured colour as #rrggbb, for drawing.</param>
public sealed record Ball(int Id, Vec2 Position, BallKind Kind, int Number, string Color, double Confidence)
{
    public bool IsObjectBall => Kind is BallKind.Solid or BallKind.Stripe or BallKind.Eight;

    public string Label => Kind switch
    {
        BallKind.Cue => "cue",
        BallKind.Eight => "8",
        _ when Number > 0 => Number.ToString(),
        BallKind.Solid => "solid",
        BallKind.Stripe => "stripe",
        _ => "?",
    };
}

/// <param name="Position">Where an object ball's centre should be sent to drop.</param>
/// <param name="Inward">Unit vector from the pocket into the table.</param>
public sealed record Pocket(int Index, string Name, Vec2 Position, Vec2 Inward, bool IsSide);

/// <summary>A cushion as a line the ball centre bounces off (the cushion nose moved in by one radius).</summary>
/// <param name="Normal">Unit vector pointing into the table.</param>
public sealed record CushionLine(string Name, Vec2 A, Vec2 B, Vec2 Normal)
{
    /// <summary>Signed distance of <paramref name="p"/> from the line, positive on the table side.</summary>
    public double SignedDistance(Vec2 p) => (p - A).Dot(Normal);

    public Vec2 Mirror(Vec2 p) => p - Normal * (2 * SignedDistance(p));
}

/// <summary>
/// The playing surface: the rectangle bounded by the cushion noses, with six
/// pockets at its corners and the middle of its long sides.
/// </summary>
public sealed record TableGeometry(RectD Cushion, IReadOnlyList<Pocket> Pockets)
{
    public bool Horizontal => Cushion.Width >= Cushion.Height;

    /// <summary>Where a ball centre can be: the cushion rectangle shrunk by one radius.</summary>
    public RectD CenterBounds(double radius) => Cushion.Inflate(-radius);

    public IReadOnlyList<CushionLine> CushionLines(double radius)
    {
        var b = CenterBounds(radius);
        return
        [
            new CushionLine("left", new Vec2(b.Left, b.Top), new Vec2(b.Left, b.Bottom), new Vec2(1, 0)),
            new CushionLine("right", new Vec2(b.Right, b.Top), new Vec2(b.Right, b.Bottom), new Vec2(-1, 0)),
            new CushionLine("top", new Vec2(b.Left, b.Top), new Vec2(b.Right, b.Top), new Vec2(0, 1)),
            new CushionLine("bottom", new Vec2(b.Left, b.Bottom), new Vec2(b.Right, b.Bottom), new Vec2(0, -1)),
        ];
    }

    public static TableGeometry FromCushion(RectD cushion)
    {
        double l = cushion.Left, t = cushion.Top, r = cushion.Right, b = cushion.Bottom;
        var diagonal = 1 / Math.Sqrt(2);

        var pockets = new List<Pocket>
        {
            new(0, "top-left", new Vec2(l, t), new Vec2(diagonal, diagonal), false),
            new(1, "top-right", new Vec2(r, t), new Vec2(-diagonal, diagonal), false),
            new(2, "bottom-left", new Vec2(l, b), new Vec2(diagonal, -diagonal), false),
            new(3, "bottom-right", new Vec2(r, b), new Vec2(-diagonal, -diagonal), false),
        };

        if (cushion.Width >= cushion.Height)
        {
            pockets.Add(new Pocket(4, "top side", new Vec2((l + r) / 2, t), new Vec2(0, 1), true));
            pockets.Add(new Pocket(5, "bottom side", new Vec2((l + r) / 2, b), new Vec2(0, -1), true));
        }
        else
        {
            pockets.Add(new Pocket(4, "left side", new Vec2(l, (t + b) / 2), new Vec2(1, 0), true));
            pockets.Add(new Pocket(5, "right side", new Vec2(r, (t + b) / 2), new Vec2(-1, 0), true));
        }

        return new TableGeometry(cushion, pockets);
    }
}
