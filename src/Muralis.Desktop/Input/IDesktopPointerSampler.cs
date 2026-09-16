namespace Muralis.Desktop.Input;

/// <summary>
/// Reads the pointer as it is right now: position, buttons and context in one go. A seam, so the
/// router's behaviour can be tested without a pointer, a window or Windows itself.
/// </summary>
internal interface IDesktopPointerSampler
{
    /// <summary>
    /// Reads the current pointer. Returns false when it cannot be read at all (a locked desktop),
    /// in which case nothing about the values is promised.
    /// </summary>
    bool TryRead(out int x, out int y, out DesktopPointerButtons buttons, out DesktopPointerContext context);
}
