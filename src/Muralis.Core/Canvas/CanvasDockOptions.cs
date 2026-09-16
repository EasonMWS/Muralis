namespace Muralis.Core.Canvas;

/// <summary>Which display edge the dock rail hugs.</summary>
public enum CanvasDockEdge
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// The edge dock's geometry and auto-hide behaviour. Everything the rail does is driven by these
/// numbers so the prototype can be tuned from JSON instead of code.
/// </summary>
public sealed class CanvasDockOptions
{
    public CanvasDockEdge Edge { get; set; } = CanvasDockEdge.Left;

    /// <summary>How close to the edge, in DIP, the pointer must come to summon the rail.</summary>
    public double TriggerSizeDip { get; set; } = 24;

    /// <summary>How long the pointer must want the dock before it starts to expand.</summary>
    public int ShowDelayMilliseconds { get; set; } = 150;

    /// <summary>How long the pointer must stay away before the rail retracts.</summary>
    public int HideDelayMilliseconds { get; set; } = 600;

    /// <summary>Rail scale while retracted; 0 means fully gone.</summary>
    public double CollapsedScale { get; set; }

    /// <summary>Rail scale while expanded.</summary>
    public double ExpandedScale { get; set; } = 1.0;

    /// <summary>Item edge length in DIP inside the rail.</summary>
    public double ItemSizeDip { get; set; } = 56;

    /// <summary>Gap in DIP between rail items at rest.</summary>
    public double ItemSpacingDip { get; set; } = 16;

    /// <summary>Gap in DIP between the rail and the display edge while expanded.</summary>
    public double EdgeMarginDip { get; set; } = 10;

    /// <summary>Padding in DIP between the rail border and its items.</summary>
    public double PaddingDip { get; set; } = 10;

    /// <summary>Magnification along the rail; tighter and slightly softer than desktop hovering.</summary>
    public CanvasProximityOptions Proximity { get; set; } = new()
    {
        MaxScale = 1.5,
        InfluenceRadiusDip = 120,
        Falloff = ProximityFalloff.Smoothstep,
    };

    public CanvasDockOptions Clone() => new()
    {
        Edge = Edge,
        TriggerSizeDip = TriggerSizeDip,
        ShowDelayMilliseconds = ShowDelayMilliseconds,
        HideDelayMilliseconds = HideDelayMilliseconds,
        CollapsedScale = CollapsedScale,
        ExpandedScale = ExpandedScale,
        ItemSizeDip = ItemSizeDip,
        ItemSpacingDip = ItemSpacingDip,
        EdgeMarginDip = EdgeMarginDip,
        PaddingDip = PaddingDip,
        Proximity = Proximity.Clone(),
    };
}
