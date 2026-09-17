using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;

namespace Muralis.Core.Abstractions;

/// <summary>
/// The one place that decides what the desktop is: Explorer's own, a preview of Muralis's items drawn
/// over it, or a full takeover with the native icons hidden and the canvas as the way in. It composes
/// the canvas and the takeover rather than duplicating either — there is one canvas and one set of
/// items in every mode that shows them, so switching between preview and takeover never mounts a
/// second layer or puts the same item on twice.
/// </summary>
/// <remarks>
/// <para>
/// The mode is a wish, and every call answers with what is really true: a takeover that could not
/// hide the icons leaves the canvas showing and reports the mode as preview, and a give-back that
/// could not be verified leaves the desktop owing a recovery. Nothing is ever reported as taken over
/// unless the icons were verified gone.
/// </para>
/// <para>
/// Entering a showing mode is also where the user's own desktop is brought across: the first scan is
/// taken at that moment, by reference only, and an entry the user turned down is never brought back.
/// </para>
/// <para>
/// Retired as a product path. The product offers the native desktop and Muralis Mode, and Muralis Mode
/// is the clean-desktop presentation rather than this: hiding Explorer's icons is the only part of this
/// layer still in use, and preview and takeover are not choices the product offers. What is left here
/// is the one job the retired takeover still has — handing a desktop that owes Explorer its icons back
/// to Windows at startup — so nothing new should depend on it.
/// </para>
/// </remarks>
public interface IDesktopModeService
{
    /// <summary>What the desktop is doing right now.</summary>
    DesktopModeStatus Status { get; }

    /// <summary>Raised on every change. May be raised on a background thread.</summary>
    event EventHandler<DesktopModeStatus>? Changed;

    /// <summary>
    /// Reads the mode the document asks for and puts it on the desktop. The call a fresh process makes
    /// once the crash marker has been dealt with.
    /// </summary>
    Task<DesktopModeStatus> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the desktop into the given mode, remembers it, and reports what really happened. Switching
    /// between preview and takeover keeps the canvas that is already mounted.
    /// </summary>
    Task<DesktopModeStatus> ApplyAsync(DesktopMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// The emergency give-back: it needs no canvas, no layout and no mode, restores what the crash
    /// marker recorded, and takes Muralis off the desktop. Running it on a desktop that was never
    /// taken over is a success, not an error.
    /// </summary>
    Task<DesktopModeStatus> RestoreNativeDesktopAsync(CancellationToken cancellationToken = default);
}
