namespace AIM8.Core.Vision;

/// <summary>
/// One captured screen image as 8-bit RGBA, row-major, no padding - the
/// layout canvas getImageData produces.
/// </summary>
public sealed class Frame
{
    public Frame(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "frame must not be empty");
        if (pixels.Length < width * height * 4) throw new ArgumentException("pixel buffer is smaller than the frame", nameof(pixels));

        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public int Offset(int x, int y) => (y * Width + x) * 4;
}

/// <summary>HSV with hue in degrees [0, 360) and saturation/value in [0, 1].</summary>
public static class Hsv
{
    public static void FromRgb(byte r, byte g, byte b, out float hue, out float saturation, out float value)
    {
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        value = max / 255f;
        saturation = max == 0 ? 0 : delta / (float)max;

        if (delta == 0)
        {
            hue = 0;
            return;
        }

        float h;
        if (max == r) h = (g - b) / (float)delta;
        else if (max == g) h = 2 + (b - r) / (float)delta;
        else h = 4 + (r - g) / (float)delta;

        h *= 60;
        if (h < 0) h += 360;
        hue = h;
    }

    /// <summary>Shortest distance between two hues, in degrees (0..180).</summary>
    public static float HueDistance(float a, float b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }
}

/// <summary>
/// What the cloth looks like in this frame. Shadows and lighting gradients
/// keep the felt's hue but change its brightness, so hue is matched tightly
/// and value loosely.
/// </summary>
public sealed class FeltModel
{
    private readonly bool[] _lookup = new bool[1 << 15];

    /// <param name="hueSpread">90th percentile of the cloth's hue deviation, in degrees.</param>
    /// <param name="saturationCeiling">
    /// Most saturated the cloth itself gets (measured across the table), or
    /// null for no ceiling.
    /// </param>
    public FeltModel(
        float hue, float saturation, float value, double tolerance, double hueSpread = 5, double? saturationCeiling = null)
    {
        Hue = hue;
        Saturation = saturation;
        Value = value;
        Tolerance = tolerance;
        HueSpread = hueSpread;

        var t = Math.Clamp(tolerance, 0.3, 3);
        HueTolerance = (float)(Math.Clamp(hueSpread * 1.8 + 4, 8, 20) * t + (saturation < 0.35 ? 6 : 0));
        MinSaturation = (float)Math.Max(0.08, saturation * 0.45 / t);

        // Cloth is rarely as saturated as a ball of the same hue (green 6 on
        // green felt), so a ceiling just above the cloth's own range saves
        // that ball.
        MaxSaturation = saturationCeiling is { } ceiling ? (float)Math.Min(1.01, ceiling + 0.06 * t) : 1.01f;
        MinValue = (float)Math.Max(0.06, value * 0.45 / t);
        MaxValue = (float)Math.Min(1.01, value * 1.45 * t + 0.05);

        // 5 bits per channel is far finer than these thresholds need, and turns
        // a per-pixel HSV conversion into a table read.
        for (var i = 0; i < _lookup.Length; i++)
        {
            var r = (byte)(((i >> 10) & 31) * 8 + 4);
            var g = (byte)(((i >> 5) & 31) * 8 + 4);
            var b = (byte)((i & 31) * 8 + 4);
            _lookup[i] = Classify(r, g, b);
        }
    }

    public float Hue { get; }

    public float Saturation { get; }

    public double Tolerance { get; }

    public double HueSpread { get; }

    public FeltModel WithSaturationCeiling(double ceiling) =>
        new(Hue, Saturation, Value, Tolerance, HueSpread, ceiling);

    public float Value { get; }

    public float HueTolerance { get; }

    public float MinSaturation { get; }

    public float MaxSaturation { get; }

    public float MinValue { get; }

    public float MaxValue { get; }

    public bool IsFelt(byte r, byte g, byte b) => _lookup[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];

    public bool IsFelt(Frame frame, int x, int y)
    {
        var o = frame.Offset(x, y);
        var p = frame.Pixels;
        return IsFelt(p[o], p[o + 1], p[o + 2]);
    }

    private bool Classify(byte r, byte g, byte b)
    {
        Hsv.FromRgb(r, g, b, out var h, out var s, out var v);
        if (s < MinSaturation || s > MaxSaturation || v < MinValue || v > MaxValue) return false;
        return Hsv.HueDistance(h, Hue) <= HueTolerance;
    }

    public override string ToString() => $"felt h={Hue:0} s={Saturation:0.00} v={Value:0.00}";
}
