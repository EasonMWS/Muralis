namespace Muralis.Core.Canvas;

/// <summary>The shape of the distance-to-scale curve.</summary>
public enum ProximityFalloff
{
    /// <summary>Smoothstep: flat near the cursor, tapering gently to nothing at the radius.</summary>
    Smoothstep,

    /// <summary>Gaussian bell, normalised so it also reaches exactly zero at the radius.</summary>
    Gaussian,
}

/// <summary>
/// Distance-driven magnification parameters. An item beyond <see cref="InfluenceRadiusDip"/> keeps
/// its natural size; the closer the cursor gets, the larger it grows, up to <see cref="MaxScale"/>
/// at zero distance. Every falloff reaches exactly 1 at the radius, so nothing outside it moves.
/// </summary>
public sealed class CanvasProximityOptions
{
    public double MaxScale { get; set; } = 1.6;

    public double InfluenceRadiusDip { get; set; } = 280;

    public ProximityFalloff Falloff { get; set; } = ProximityFalloff.Smoothstep;

    public CanvasProximityOptions Clone() => new()
    {
        MaxScale = MaxScale,
        InfluenceRadiusDip = InfluenceRadiusDip,
        Falloff = Falloff,
    };
}
