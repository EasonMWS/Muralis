using System.Runtime.InteropServices;

namespace ClearDockPoc;

/// <summary>
/// The left button's state, as the renderer sees it.
/// </summary>
/// <remarks>
/// <para>
/// The product's dock reads press and release from its own raw input path, so the button arrives in the same
/// ordered stream as the movement around it. <c>RawPointerBroker</c> carries movement only — it is a pointer
/// position fan-out, not a button one — so this prototype reads the button state alongside it.
/// </para>
/// <para>
/// <see cref="GetAsyncKeyState"/> is a direct read of the current physical key state, not a polled sample: it is
/// asked at the moment a report is applied, which is what makes a press and its surrounding movement resolve
/// into the same sequence. It is still a gap against the product, and it is recorded as one rather than hidden:
/// a shipping renderer wants the button on the broker's stream, next to the position.
/// </para>
/// </remarks>
internal sealed class PointerButtonState
{
    private const int VkLeftButton = 0x01;

    /// <summary>Whether the left button is physically down right now.</summary>
    public bool IsLeftDown => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
