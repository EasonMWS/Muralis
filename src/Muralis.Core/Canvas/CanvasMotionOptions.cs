namespace Muralis.Core.Canvas;

/// <summary>
/// One spring in compositor terms: how long a full oscillation takes and how quickly it settles
/// (1 = no overshoot, below 1 = a visible, elastic overshoot).
/// </summary>
public sealed class CanvasSpring
{
    public double PeriodSeconds { get; set; } = 0.35;

    public double DampingRatio { get; set; } = 0.8;

    public CanvasSpring Clone() => new()
    {
        PeriodSeconds = PeriodSeconds,
        DampingRatio = DampingRatio,
    };
}

/// <summary>
/// The springs the free canvas moves with, one per kind of motion. The dock keeps its own spring
/// next to the rest of its parameters, where the size it magnifies to and the radius it magnifies
/// over live.
/// </summary>
public sealed class CanvasMotionOptions
{
    /// <summary>Item growth and shrink under the cursor: quick and nearly bounce-free.</summary>
    public CanvasSpring Hover { get; set; } = new() { PeriodSeconds = 0.30, DampingRatio = 0.85 };

    public CanvasMotionOptions Clone() => new()
    {
        Hover = Hover.Clone(),
    };
}
