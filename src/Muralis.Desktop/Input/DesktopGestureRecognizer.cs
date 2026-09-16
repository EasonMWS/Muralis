using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Input;

/// <summary>What a press that has ended turned out to be.</summary>
internal enum DesktopGesture
{
    /// <summary>No press was being tracked.</summary>
    None,

    /// <summary>A press that stayed inside the drag rectangle: the canvas reads it as a selection.</summary>
    Click,

    /// <summary>A click that followed the previous one closely enough to be its second half.</summary>
    DoubleClick,

    /// <summary>A press that turned into a drag; the item has already been moved and will not launch.</summary>
    DragEnd,
}

/// <summary>
/// Reads the pointer's presses into the three gestures the canvas acts on: a click, a double-click
/// and a drag. Pure — it is fed coordinates and a clock — so the rules are provable without a mouse.
/// </summary>
/// <remarks>
/// <para>
/// The thresholds are the user's own, read from the system: how far a press may wander before it
/// counts as a drag (the drag rectangle), and how far apart in place and time two clicks may be and
/// still be one double-click. Coordinates are display pixels, which is the unit those thresholds
/// are published in.
/// </para>
/// <para>
/// A press becomes a drag the first time it leaves the drag rectangle, and from then on it can never
/// be a click — so a completed drag can never launch anything, however briefly it was held. A click
/// landing inside the double-click rectangle of the previous one in time is reported as a
/// double-click and ends the chain, so a third click starts over rather than packing a fourth event.
/// </para>
/// </remarks>
internal sealed class DesktopGestureRecognizer
{
    /// <summary>Windows' own default, used only if the system will not say how long a double-click may take.</summary>
    private const long FallbackDoubleClickMilliseconds = 500;

    private readonly double _dragWidthPixels;
    private readonly double _dragHeightPixels;
    private readonly double _doubleClickWidthPixels;
    private readonly double _doubleClickHeightPixels;
    private readonly long _doubleClickMilliseconds;

    private bool _pressed;
    private bool _dragging;
    private double _pressXPixels;
    private double _pressYPixels;

    private bool _clicked;
    private double _clickXPixels;
    private double _clickYPixels;
    private long _clickedAtMilliseconds;

    internal DesktopGestureRecognizer(
        int dragWidthPixels,
        int dragHeightPixels,
        int doubleClickWidthPixels,
        int doubleClickHeightPixels,
        long doubleClickMilliseconds)
    {
        _dragWidthPixels = Math.Max(1, dragWidthPixels);
        _dragHeightPixels = Math.Max(1, dragHeightPixels);
        _doubleClickWidthPixels = Math.Max(1, doubleClickWidthPixels);
        _doubleClickHeightPixels = Math.Max(1, doubleClickHeightPixels);
        _doubleClickMilliseconds = doubleClickMilliseconds > 0 ? doubleClickMilliseconds : FallbackDoubleClickMilliseconds;
    }

    /// <summary>A recognizer tuned to the user's own mouse settings.</summary>
    internal static DesktopGestureRecognizer ForThisSystem()
    {
        var doubleClickMilliseconds = NativeMethods.GetDoubleClickTime();
        return new DesktopGestureRecognizer(
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxDrag),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyDrag),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxDoubleClick),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyDoubleClick),
            doubleClickMilliseconds == 0 ? FallbackDoubleClickMilliseconds : doubleClickMilliseconds);
    }

    /// <summary>Whether a press is being tracked right now.</summary>
    internal bool IsPressed => _pressed;

    /// <summary>Whether the press being tracked has become a drag.</summary>
    internal bool IsDragging => _dragging;

    /// <summary>A press began.</summary>
    internal void Press(double xPixels, double yPixels)
    {
        _pressed = true;
        _dragging = false;
        _pressXPixels = xPixels;
        _pressYPixels = yPixels;
    }

    /// <summary>
    /// The pointer moved while pressed. Returns true from the moment the press counts as a drag, so
    /// the canvas knows when it may start moving the item.
    /// </summary>
    internal bool Move(double xPixels, double yPixels)
    {
        if (!_pressed)
        {
            return false;
        }

        if (!_dragging
            && (Math.Abs(xPixels - _pressXPixels) > _dragWidthPixels / 2.0
                || Math.Abs(yPixels - _pressYPixels) > _dragHeightPixels / 2.0))
        {
            _dragging = true;

            // A drag interrupts any click chain: whatever was clicked before it is over.
            _clicked = false;
        }

        return _dragging;
    }

    /// <summary>Where the press ended and when; what it turned out to be.</summary>
    internal DesktopGesture Release(double xPixels, double yPixels, long nowMilliseconds)
    {
        if (!_pressed)
        {
            return DesktopGesture.None;
        }

        _pressed = false;

        if (_dragging)
        {
            _dragging = false;
            return DesktopGesture.DragEnd;
        }

        var second = _clicked
            && nowMilliseconds - _clickedAtMilliseconds <= _doubleClickMilliseconds
            && Math.Abs(xPixels - _clickXPixels) <= _doubleClickWidthPixels / 2.0
            && Math.Abs(yPixels - _clickYPixels) <= _doubleClickHeightPixels / 2.0;

        if (second)
        {
            _clicked = false;
            return DesktopGesture.DoubleClick;
        }

        _clicked = true;
        _clickXPixels = xPixels;
        _clickYPixels = yPixels;
        _clickedAtMilliseconds = nowMilliseconds;
        return DesktopGesture.Click;
    }

    /// <summary>The press is over without a gesture: the capture was taken away, or the display changed.</summary>
    internal void Cancel()
    {
        _pressed = false;
        _dragging = false;
    }
}
