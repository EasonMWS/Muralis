namespace Muralis.Core.Dock;

/// <summary>Which display edge the dock rail hugs.</summary>
public enum DockEdge
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>Where a rail's length runs: down the display for the side edges, across it for the others.</summary>
public enum DockAxis
{
    Vertical,
    Horizontal,
}

/// <summary>The four edges, and the one fact every edge-dependent decision is built on.</summary>
public static class DockEdgeInfo
{
    /// <summary>Every edge, in the order the settings offer them.</summary>
    public static readonly IReadOnlyList<DockEdge> All =
        [DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom];

    /// <summary>The axis the rail's length runs along. Left and right differ only in which way depth grows.</summary>
    public static DockAxis Axis(this DockEdge edge) =>
        edge is DockEdge.Left or DockEdge.Right ? DockAxis.Vertical : DockAxis.Horizontal;

    /// <summary>True when the rail runs down the display.</summary>
    public static bool IsVertical(this DockEdge edge) => edge.Axis() == DockAxis.Vertical;
}
