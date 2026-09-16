using Muralis.Core.Models;

namespace Muralis.Core.Dock;

/// <summary>
/// Where the rail and its items sit. Every edge is the same set of formulas read through a
/// <see cref="DockFrame"/>, so adding an edge, or moving the dock to another edge, changes no
/// geometry: the frame turns an along position and a depth into a point on the right edge.
/// </summary>
/// <remarks>
/// All numbers are DIP relative to the display's top-left corner, and nothing here assumes the
/// display starts at (0, 0) or that one DIP is one pixel: the frame carries both.
/// </remarks>
public static class DockGeometry
{
    /// <summary>Along-axis distance between two neighbouring items' centres, at their natural size.</summary>
    public static double StrideDip(DockOptions dock) => dock.ItemSizeDip + dock.SpacingDip;

    /// <summary>How long the run of items is at rest, padding excluded.</summary>
    public static double RestingRunDip(DockOptions dock, int itemCount)
    {
        ArgumentNullException.ThrowIfNull(dock);

        if (itemCount <= 0)
        {
            return 0;
        }

        return (itemCount * dock.ItemSizeDip) + ((itemCount - 1) * dock.SpacingDip);
    }

    /// <summary>
    /// How long the run can get with everything magnified at once. The window region is built from
    /// this, so a magnified item is never clipped and the region never has to change while the
    /// pointer moves.
    /// </summary>
    public static double MagnifiedRunDip(DockOptions dock, int itemCount) =>
        RestingRunDip(dock, itemCount) * Math.Max(1.0, dock.MaxScale);

    /// <summary>
    /// Resting centre of every item along the rail, in DIP, for a rail centred on
    /// <paramref name="railCentreAlongDip"/>. This is where the items sit when the pointer is away,
    /// and the reference the magnification measures distances against.
    /// </summary>
    public static IReadOnlyList<double> RestingCentres(DockOptions dock, double railCentreAlongDip, int itemCount)
    {
        ArgumentNullException.ThrowIfNull(dock);

        if (itemCount <= 0)
        {
            return [];
        }

        var centres = new double[itemCount];
        var cursor = railCentreAlongDip - (RestingRunDip(dock, itemCount) / 2.0) + (dock.ItemSizeDip / 2.0);
        for (var i = 0; i < itemCount; i++)
        {
            centres[i] = cursor;
            cursor += StrideDip(dock);
        }

        return centres;
    }

    /// <summary>The depth of the middle of an item, i.e. the middle of the rail while it is out.</summary>
    public static double ItemCentreDepthDip(DockOptions dock) =>
        dock.EdgeMarginDip + (dock.RailThicknessDip / 2.0);

    /// <summary>
    /// Where the rail's near and far edges sit in depth: fully out it starts at the edge margin,
    /// fully away it is pushed off the display until only <see cref="DockOptions.PeekSizeDip"/>
    /// of it is left showing. <paramref name="reveal"/> runs 0 (away) to 1 (out) and is what the
    /// spring animates, so the two ends are the same rail sliding, never a scaled copy of one.
    /// </summary>
    public static (double Start, double Thickness) DepthRange(DockOptions dock, double reveal)
    {
        ArgumentNullException.ThrowIfNull(dock);

        var thickness = dock.RailThicknessDip;
        var shown = dock.EdgeMarginDip;
        var hidden = -(thickness - Math.Clamp(dock.PeekSizeDip, 0, thickness));
        var t = Math.Clamp(reveal, 0, 1);
        return (hidden + ((shown - hidden) * t), thickness);
    }

    /// <summary>The rail's rectangle in display pixels.</summary>
    public static PixelRect RailRect(
        DockOptions dock,
        PixelRect bounds,
        double scaleFactor,
        double railLengthDip,
        double reveal)
    {
        ArgumentNullException.ThrowIfNull(dock);

        var frame = new DockFrame(dock.Edge, bounds, scaleFactor);
        var length = Math.Max(dock.RailThicknessDip, railLengthDip);
        var (depth, thickness) = DepthRange(dock, reveal);
        return frame.Rect(frame.AlongCentreDip - (length / 2.0), length, depth, thickness);
    }

    /// <summary>
    /// The rail's length in DIP for a run of items: the run plus the padding on either side, and
    /// never shorter than one item's row.
    /// </summary>
    public static double RailLengthDip(DockOptions dock, double runLengthDip)
    {
        ArgumentNullException.ThrowIfNull(dock);
        return Math.Max(dock.RailThicknessDip, runLengthDip + (2 * dock.PaddingDip));
    }

    /// <summary>The strip along the edge that the pointer can summon the dock from.</summary>
    public static PixelRect TriggerBand(DockOptions dock, PixelRect bounds, double scaleFactor)
    {
        ArgumentNullException.ThrowIfNull(dock);

        var frame = new DockFrame(dock.Edge, bounds, scaleFactor);
        return frame.Rect(0, frame.AlongExtentDip, 0, Math.Max(1.0, dock.TriggerThicknessDip));
    }

    /// <summary>
    /// Everything the dock can ever draw on its edge, in display pixels: the rail in either of its
    /// positions, the peek it leaves behind, and every item at its largest scale. The window region
    /// is built from this while the dock is out, so no animation of it is ever clipped.
    /// </summary>
    public static PixelRect Extent(
        DockOptions dock,
        PixelRect bounds,
        double scaleFactor,
        int itemCount)
    {
        ArgumentNullException.ThrowIfNull(dock);

        var frame = new DockFrame(dock.Edge, bounds, scaleFactor);
        var thickness = dock.RailThicknessDip;
        var centre = ItemCentreDepthDip(dock);
        var halfItem = dock.ItemSizeDip * Math.Max(1.0, dock.MaxScale) / 2.0;

        var along = RailLengthDip(dock, MagnifiedRunDip(dock, itemCount));
        var (away, _) = DepthRange(dock, 0);
        var depthStart = Math.Min(away, centre - halfItem);
        var depthEnd = Math.Max(dock.EdgeMarginDip + thickness, centre + halfItem);

        return frame.Rect(frame.AlongCentreDip - (along / 2.0), along, depthStart, depthEnd - depthStart);
    }

    /// <summary>Whether a display-relative DIP point is inside the rail's rectangle.</summary>
    public static bool RailContains(
        DockOptions dock,
        PixelRect bounds,
        double scaleFactor,
        double railLengthDip,
        double reveal,
        double xDip,
        double yDip)
    {
        ArgumentNullException.ThrowIfNull(dock);

        var frame = new DockFrame(dock.Edge, bounds, scaleFactor);
        var length = Math.Max(dock.RailThicknessDip, railLengthDip);
        var (along, depth) = ToAlongAndDepth(frame, xDip, yDip);
        var (depthStart, thickness) = DepthRange(dock, reveal);

        return along >= frame.AlongCentreDip - (length / 2.0)
            && along <= frame.AlongCentreDip + (length / 2.0)
            && depth >= depthStart
            && depth <= depthStart + thickness;
    }

    /// <summary>Whether a display-relative DIP point is in the strip along the dock's edge.</summary>
    public static bool InTriggerBand(DockOptions dock, PixelRect bounds, double scaleFactor, double xDip, double yDip)
    {
        ArgumentNullException.ThrowIfNull(dock);

        var frame = new DockFrame(dock.Edge, bounds, scaleFactor);
        var (_, depth) = ToAlongAndDepth(frame, xDip, yDip);
        return depth >= 0 && depth <= dock.TriggerThicknessDip;
    }

    /// <summary>
    /// A display-relative DIP point read as a position along the rail and a depth away from its edge.
    /// The inverse of <see cref="DockFrame.PointAt"/>, and the reason the pointer only has to be
    /// measured once whatever edge the dock is on.
    /// </summary>
    public static (double Along, double Depth) ToAlongAndDepth(DockFrame frame, double xDip, double yDip) =>
        frame.IsVertical
            ? (yDip, frame.Edge == DockEdge.Left ? xDip : frame.CrossExtentDip - xDip)
            : (xDip, frame.Edge == DockEdge.Top ? yDip : frame.CrossExtentDip - yDip);
}
