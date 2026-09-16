namespace Muralis.Core.Canvas;

/// <summary>
/// The one definition of the proximity scale curve, kept pure so the animation layer and the
/// tests agree on it. Distances are DIP, between the cursor and an item's fixed centre.
/// </summary>
public static class CanvasProximity
{
    /// <summary>
    /// The scale an item renders at when the cursor is <paramref name="distanceDip"/> away. Zero
    /// distance gives <see cref="CanvasProximityOptions.MaxScale"/>, distances at or beyond the
    /// influence radius give 1, and everything in between follows the configured falloff.
    /// </summary>
    public static double ScaleAt(double distanceDip, CanvasProximityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var radius = options.InfluenceRadiusDip;
        var maxScale = Math.Max(1.0, options.MaxScale);
        if (radius <= 0 || maxScale <= 1.0)
        {
            return 1.0;
        }

        var distance = Math.Max(0.0, distanceDip);
        if (distance >= radius)
        {
            return 1.0;
        }

        // t: 1 at the cursor, 0 at the rim. Both curves are 0 at the rim by construction.
        var t = 1.0 - distance / radius;
        var curve = options.Falloff switch
        {
            ProximityFalloff.Gaussian => NormalizedGaussian(t),
            _ => Smoothstep(t),
        };

        return 1.0 + (maxScale - 1.0) * curve;
    }

    /// <summary>The scale for an item whose centre is at (<paramref name="centerXDip"/>, <paramref name="centerYDip"/>).</summary>
    public static double ScaleForItem(
        double cursorXDip,
        double cursorYDip,
        double centerXDip,
        double centerYDip,
        CanvasProximityOptions options)
    {
        var dx = cursorXDip - centerXDip;
        var dy = cursorYDip - centerYDip;
        return ScaleAt(Math.Sqrt(dx * dx + dy * dy), options);
    }

    private static double Smoothstep(double t) => t * t * (3.0 - 2.0 * t);

    private static double NormalizedGaussian(double t)
    {
        const double radiusOverSigma = 2.5;
        var edge = Math.Exp(-radiusOverSigma * radiusOverSigma);
        var falloff = 1.0 - t;
        var value = Math.Exp(-falloff * falloff * radiusOverSigma * radiusOverSigma);
        return (value - edge) / (1.0 - edge);
    }
}
