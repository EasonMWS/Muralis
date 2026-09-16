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

/// <summary>Springs the canvas animates with, one per kind of motion.</summary>
public sealed class CanvasMotionOptions
{
    /// <summary>Item growth and shrink under the cursor: quick and nearly bounce-free.</summary>
    public CanvasSpring Hover { get; set; } = new() { PeriodSeconds = 0.30, DampingRatio = 0.85 };

    /// <summary>Dock expand and retract: a little slower, with a slight elastic settle.</summary>
    public CanvasSpring Dock { get; set; } = new() { PeriodSeconds = 0.40, DampingRatio = 0.75 };

    public CanvasMotionOptions Clone() => new()
    {
        Hover = Hover.Clone(),
        Dock = Dock.Clone(),
    };
}
