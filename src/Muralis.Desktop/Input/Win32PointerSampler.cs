using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Input;

/// <summary>
/// The real pointer: the cursor position, the physical button state and the context walk over the
/// window under it. All three are plain queries — nothing is installed, captured or intercepted.
/// </summary>
internal sealed class Win32PointerSampler : IDesktopPointerSampler
{
    private const int ButtonDownBit = 0x8000;

    private readonly IPointerWindowProbe _probe = new Win32PointerWindowProbe();

    public bool TryRead(out int x, out int y, out DesktopPointerButtons buttons, out DesktopPointerContext context)
    {
        x = 0;
        y = 0;
        buttons = DesktopPointerButtons.None;
        context = DesktopPointerContext.Foreign;

        if (!NativeMethods.GetCursorPos(out var point))
        {
            return false;
        }

        x = point.X;
        y = point.Y;
        buttons = ButtonsDown();
        context = PointerWindowClassifier.Classify(NativeMethods.WindowFromPoint(point), _probe);
        return true;
    }

    private static DesktopPointerButtons ButtonsDown()
    {
        var buttons = DesktopPointerButtons.None;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VkLButton) & ButtonDownBit) != 0)
        {
            buttons |= DesktopPointerButtons.Left;
        }

        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VkRButton) & ButtonDownBit) != 0)
        {
            buttons |= DesktopPointerButtons.Right;
        }

        return buttons;
    }
}
