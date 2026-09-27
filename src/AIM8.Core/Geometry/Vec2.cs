namespace AIM8.Core.Geometry;

/// <summary>A point or direction in frame pixels. Y grows downwards, as on screen.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double LengthSquared => X * X + Y * Y;

    public Vec2 Normalized()
    {
        var length = Length;
        return length < 1e-12 ? Zero : new Vec2(X / length, Y / length);
    }

    public double Dot(Vec2 other) => X * other.X + Y * other.Y;

    public double Cross(Vec2 other) => X * other.Y - Y * other.X;

    /// <summary>Rotated 90 degrees.</summary>
    public Vec2 Perpendicular => new(-Y, X);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);

    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    public static Vec2 FromAngle(double radians) => new(Math.Cos(radians), Math.Sin(radians));

    public override string ToString() => $"({X:0.0}, {Y:0.0})";
}

/// <summary>Axis-aligned rectangle in frame pixels.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public Vec2 Center => new(X + Width / 2, Y + Height / 2);

    public double ShortSide => Math.Min(Width, Height);

    public double LongSide => Math.Max(Width, Height);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    public bool Contains(Vec2 p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    /// <summary>Grows (positive) or shrinks (negative) every side by <paramref name="amount"/>.</summary>
    public RectD Inflate(double amount) =>
        new(X - amount, Y - amount, Width + 2 * amount, Height + 2 * amount);

    public override string ToString() => $"[{Left:0},{Top:0} {Width:0}x{Height:0}]";
}

public static class Geo
{
    /// <summary>Parameter t in [0,1] of the point on segment a-b closest to p.</summary>
    public static double ClosestParameter(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-12) return 0;
        return Math.Clamp((p - a).Dot(ab) / lengthSquared, 0, 1);
    }

    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var t = ClosestParameter(p, a, b);
        return Vec2.Distance(p, a + (b - a) * t);
    }

    /// <summary>Unsigned angle between two directions, in degrees.</summary>
    public static double AngleBetween(Vec2 u, Vec2 v)
    {
        var lengths = u.Length * v.Length;
        if (lengths < 1e-12) return 0;
        return Math.Acos(Math.Clamp(u.Dot(v) / lengths, -1, 1)) * 180 / Math.PI;
    }

    public static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
