using Muralis.Core.Models;

namespace Muralis.Core.Dock;

/// <summary>
/// One edge's coordinate frame. Everything the dock does is expressed as a position along the rail
/// and a depth away from its own edge — depth 0 is exactly on the edge line and grows towards the
/// middle of the display — so all four edges share one set of formulas instead of four.
/// </summary>
/// <remarks>
/// The display's position is carried by the frame and never assumed: a monitor whose origin is not
/// (0, 0) puts the rail where that monitor's edge really is. Every number in and out is DIP relative
/// to the display's top-left corner; only <see cref="Rect"/> leaves in pixels, for the window region.
/// </remarks>
public readonly struct DockFrame
{
    private readonly PixelRect _bounds;

    public DockFrame(DockEdge edge, PixelRect boundsPixels, double scaleFactor)
    {
        Edge = edge;
        _bounds = boundsPixels;
        ScaleFactor = scaleFactor > 0 ? scaleFactor : 1.0;
    }

    public DockEdge Edge { get; }

    public double ScaleFactor { get; }

    public DockAxis Axis => Edge.Axis();

    public bool IsVertical => Axis == DockAxis.Vertical;

    /// <summary>How long the edge is, in DIP: the rail's axis.</summary>
    public double AlongExtentDip => (IsVertical ? _bounds.Height : _bounds.Width) / ScaleFactor;

    /// <summary>How deep the display is, in DIP: the axis the rail's thickness runs along.</summary>
    public double CrossExtentDip => (IsVertical ? _bounds.Width : _bounds.Height) / ScaleFactor;

    /// <summary>The middle of this edge, along the rail. The rail is always centred here.</summary>
    public double AlongCentreDip => AlongExtentDip / 2.0;

    /// <summary>The point at an along position and a depth, relative to the display's top-left corner.</summary>
    public (double X, double Y) PointAt(double alongDip, double depthDip) => IsVertical
        ? (Edge == DockEdge.Left ? depthDip : CrossExtentDip - depthDip, alongDip)
        : (alongDip, Edge == DockEdge.Top ? depthDip : CrossExtentDip - depthDip);

    /// <summary>A point given relative to the display, in display pixels.</summary>
    public (int X, int Y) PixelAt(double alongDip, double depthDip)
    {
        var (x, y) = PointAt(alongDip, depthDip);
        return (
            _bounds.X + (int)Math.Round(x * ScaleFactor),
            _bounds.Y + (int)Math.Round(y * ScaleFactor));
    }

    /// <summary>
    /// A rectangle given as along and depth extents, in display pixels. Depth may be negative, which
    /// is what puts a hidden rail just off the edge it belongs to.
    /// </summary>
    public PixelRect Rect(double alongStart, double alongLength, double depthStart, double depthThickness)
    {
        var (x1, y1) = PointAt(alongStart, depthStart);
        var (x2, y2) = PointAt(alongStart + alongLength, depthStart + depthThickness);

        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var scale = ScaleFactor;

        return new PixelRect(
            _bounds.X + (int)Math.Round(left * scale),
            _bounds.Y + (int)Math.Round(top * scale),
            Math.Max(1, (int)Math.Round(Math.Abs(x2 - x1) * scale)),
            Math.Max(1, (int)Math.Round(Math.Abs(y2 - y1) * scale)));
    }
}
