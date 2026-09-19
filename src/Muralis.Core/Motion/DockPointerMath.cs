namespace Muralis.Core.Motion;

/// <summary>
/// The rectangle the pointer has to be inside for the dock to react, along a rail.
/// </summary>
/// <param name="Left">The left edge, in the dock's own DIP space.</param>
/// <param name="Top">The top edge, in the dock's own DIP space.</param>
/// <param name="Right">The right edge, in the dock's own DIP space.</param>
/// <param name="Bottom">The bottom edge, in the dock's own DIP space.</param>
public readonly record struct DockPointerRegion(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Whether the region has any extent at all.</summary>
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    /// <summary>How much room the region leaves on each side, given the dock it was built around.</summary>
    public double Width => Right - Left;

    /// <summary>How tall the region is.</summary>
    public double Height => Bottom - Top;
}

/// <summary>
/// Turns a cursor position into a pointer position the motion engine can use, and decides whether the pointer
/// is on the dock at all.
/// </summary>
/// <remarks>
/// <para>
/// Pure arithmetic, so it can be tested without a window: the two things that go wrong with a pointer source
/// are a coordinate space that does not match the one the icon centres were measured in, and a region that is
/// the wrong size, and both are decidable here.
/// </para>
/// <para>
/// The cursor arrives in physical screen pixels. The dock's icons are measured in the dock's own DIP space,
/// so the window's physical origin is subtracted first and that client-pixel distance is divided by the
/// window's current pixels-per-DIP value. The value comes from the window itself rather than from the primary
/// display, which keeps the conversion correct when the dock moves between differently scaled monitors.
/// </para>
/// </remarks>
public static class DockPointerMath
{
    /// <summary>
    /// How far outside a region the pointer may be and still count as inside it.
    /// </summary>
    /// <remarks>
    /// A pointer sitting exactly on the boundary between "on the dock" and "off it" must not flicker between
    /// the two as it moves by a fraction of a pixel, and a cursor can be reported one pixel outside a window
    /// edge while still being drawn over it.
    /// </remarks>
    public const double RegionToleranceDip = 1;

    /// <summary>
    /// Where the pointer is in the dock's own space, from where the cursor is on screen.
    /// </summary>
    /// <param name="screenX">Cursor x, in physical screen pixels.</param>
    /// <param name="screenY">Cursor y, in physical screen pixels.</param>
    /// <param name="windowOriginX">Where the dock window's own origin sits on screen.</param>
    /// <param name="windowOriginY">Where the dock window's own origin sits on screen.</param>
    /// <param name="pixelsPerDip">How many physical screen pixels make one dock DIP.</param>
    public static (double X, double Y) ToDockSpace(
        int screenX,
        int screenY,
        double windowOriginX,
        double windowOriginY,
        double pixelsPerDip)
    {
        var scale = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        return ((screenX - windowOriginX) / scale, (screenY - windowOriginY) / scale);
    }

    /// <summary>
    /// The region that starts the motion: the dock's own drawn bounds.
    /// </summary>
    /// <remarks>
    /// Deliberately the dock's bounds and not the window's. A resting window is bigger than the dock, and its
    /// empty margin is not part of the dock: a pointer there has not reached anything, so the dock must not
    /// wake up for it.
    /// </remarks>
    public static DockPointerRegion RestingRegion(
        double dockLeft,
        double dockTop,
        double dockRight,
        double dockBottom) =>
        new(dockLeft, dockTop, dockRight, dockBottom);

    /// <summary>
    /// The region that keeps the motion running once the window has grown: the same dock bounds, plus the room
    /// the magnification was given to grow into.
    /// </summary>
    /// <remarks>
    /// Larger than the resting region on every side the reserve is spent on, which is what stops a pointer
    /// chasing the peak of the wave — up, or out to either end of the run — from falling out of the region and
    /// collapsing the window it is being drawn in.
    /// </remarks>
    public static DockPointerRegion MotionRegion(
        double dockLeft,
        double dockTop,
        double dockRight,
        double dockBottom,
        double horizontalReachDip,
        double verticalReserveDip) =>
        new(
            dockLeft - horizontalReachDip,
            dockTop - verticalReserveDip,
            dockRight + horizontalReachDip,
            dockBottom);

    /// <summary>
    /// The stable leave region in screen space: visual reserve plus a small semantic exit margin on every
    /// exposed edge. The caller supplies values in one coordinate unit; the arithmetic is unit-agnostic.
    /// </summary>
    public static DockPointerRegion ExpandedRegion(
        double dockLeft,
        double dockTop,
        double dockRight,
        double dockBottom,
        double horizontalReach,
        double verticalReserve,
        double exitMargin)
    {
        var margin = Math.Max(0, exitMargin);
        return new DockPointerRegion(
            dockLeft - horizontalReach - margin,
            dockTop - verticalReserve - margin,
            dockRight + horizontalReach + margin,
            dockBottom + margin);
    }

    /// <summary>Whether a point in the dock's own space is inside a region.</summary>
    public static bool Contains(DockPointerRegion region, double x, double y)
    {
        if (region.IsEmpty)
        {
            return false;
        }

        return x >= region.Left - RegionToleranceDip
            && x <= region.Right + RegionToleranceDip
            && y >= region.Top - RegionToleranceDip
            && y <= region.Bottom + RegionToleranceDip;
    }
}
