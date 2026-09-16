namespace Muralis.Desktop.Input;

/// <summary>
/// One reading of the desktop pointer: where it is in virtual screen pixels, what it is over and
/// which buttons are down. The position only means something while the context is not
/// <see cref="DesktopPointerContext.Foreign"/>; <see cref="Unknown"/> stands for "nothing read yet".
/// </summary>
/// <remarks>
/// Pixels, not DIP: the router knows nothing about displays. Whoever draws on a display converts,
/// with that display's scale factor.
/// </remarks>
public readonly record struct DesktopPointerState(int X, int Y, DesktopPointerContext Context, DesktopPointerButtons Buttons)
{
    /// <summary>Nothing has been read: no position, no context, no buttons.</summary>
    public static DesktopPointerState Unknown { get; } = new(0, 0, DesktopPointerContext.Foreign, DesktopPointerButtons.None);

    /// <summary>Whether the pointer is over the desktop layer or over one of our surfaces.</summary>
    public bool IsOverDesktop => Context != DesktopPointerContext.Foreign;
}
