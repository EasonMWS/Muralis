namespace Muralis.Core.Canvas;

/// <summary>
/// The one definition of the proximity scale curve, kept pure so the animation layer and the
/// tests agree on it. Distances are DIP, between the cursor and an item's fixed centre.
/// </summary>
public static class CanvasProximity
{
    /// <summary>
    /// The scale an item renders at when the cursor is <paramref name="distanceDip"/> away. Zero
    /// distance gives <paramref name="maxScale"/>, distances at or beyond
    /// <paramref name="influenceRadiusDip"/> give 1, and everything in between follows
    /// <paramref name="falloff"/>.
    /// </summary>
    public static double ScaleAt(
        double distanceDip,
        double maxScale,
        double influenceRadiusDip,
        ProximityFalloff falloff)
    {
        var ceiling = Math.Max(1.0, maxScale);
        var radius = influenceRadiusDip;
        if (radius <= 0 || ceiling <= 1.0)
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
        var curve = falloff switch
        {
            ProximityFalloff.Gaussian => NormalizedGaussian(t),
            _ => Smoothstep(t),
        };

        return 1.0 + (ceiling - 1.0) * curve;
    }

    /// <summary>The same curve, read from an options object.</summary>
    public static double ScaleAt(double distanceDip, CanvasProximityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ScaleAt(distanceDip, options.MaxScale, options.InfluenceRadiusDip, options.Falloff);
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
