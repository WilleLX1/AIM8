using AIM8.Core.Geometry;

namespace AIM8.Core;

/// <summary>A rectangle in coordinates relative to the frame (0..1), so it survives capture scaling.</summary>
public sealed record NormalizedRect(double X0, double Y0, double X1, double Y1)
{
    public RectD ToPixels(int width, int height) => RectD.FromEdges(
        Math.Min(X0, X1) * width, Math.Min(Y0, Y1) * height,
        Math.Max(X0, X1) * width, Math.Max(Y0, Y1) * height);

    public bool IsUsable => Math.Abs(X1 - X0) > 0.05 && Math.Abs(Y1 - Y0) > 0.05;
}

/// <summary>Everything the user can tune about detection and shot selection.</summary>
public sealed record AimSettings
{
    public Team Team { get; init; } = Team.Solids;

    /// <summary>Object ball off one cushion into the pocket.</summary>
    public bool AllowBanks { get; init; } = true;

    /// <summary>Cue ball off one cushion onto the object ball.</summary>
    public bool AllowKicks { get; init; } = true;

    /// <summary>Own ball into another own ball into the pocket.</summary>
    public bool AllowCombos { get; init; } = true;

    /// <summary>How many runner-up shots to draw faintly next to the best one.</summary>
    public int Alternatives { get; init; } = 2;

    /// <summary>Multiplier on how far a colour may be from the felt and still count as felt.</summary>
    public double FeltTolerance { get; init; } = 1.0;

    /// <summary>
    /// Share of white a ball needs to count as a stripe. 0 picks the split from
    /// the balls on the table.
    /// </summary>
    public double StripeThreshold { get; init; }

    /// <summary>Ball radius as a fraction of the table's short side. 0 measures it from the frame.</summary>
    public double BallSize { get; init; }

    /// <summary>Look for a cushion drawn in the felt colour and move the table edge onto its nose.</summary>
    public bool DetectCushionEdge { get; init; } = true;

    /// <summary>Table rectangle set by hand, overriding detection.</summary>
    public NormalizedRect? ManualTable { get; init; }

    /// <summary>Consecutive still frames before a shot is planned.</summary>
    public int StableFrames { get; init; } = 2;

    /// <summary>Thinner cuts than this are not offered.</summary>
    public double MaxCutAngle { get; init; } = 78;
}
