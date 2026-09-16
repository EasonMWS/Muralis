using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Dock;
using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Shows the desktop canvas prototype — a free-form layer of items above the desktop icons — and
/// takes it away again. Implemented per platform in the app layer (<c>Muralis.Desktop</c>) so view
/// models never touch native APIs. The implementation reuses the desktop shell and adds nothing to
/// the system: switching the canvas off returns the desktop exactly as it was, and the native icons
/// keep working either way.
/// </summary>
/// <remarks>
/// The items on the canvas are the user's own: each one is an explicit import of a program,
/// shortcut, folder or address, and the service only ever stores the reference. Nothing is copied,
/// moved or scanned, and removing an item never touches what it points at.
/// </remarks>
public interface IDesktopCanvasService
{
    /// <summary>What the canvas is doing right now.</summary>
    CanvasPrototypeStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes. May be raised on a background thread.</summary>
    event EventHandler<CanvasPrototypeStatus>? StatusChanged;

    /// <summary>
    /// Puts the saved prototype layout (or the seed layout, when there is nothing valid saved) on
    /// the primary display, above the icons. Calling this while the canvas is already showing
    /// simply returns the current status.
    /// </summary>
    Task<CanvasPrototypeStatus> EnableAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the canvas from the desktop; the native desktop is not touched either way.</summary>
    Task DisableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The items on the canvas right now, as copies the caller may keep. Answered whether the
    /// canvas is showing or not: with nothing mounted, what the next mount will show is read from
    /// the layout file.
    /// </summary>
    Task<IReadOnlyList<DesktopItem>> GetItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an item the user just picked — a program, shortcut, folder or address — to the canvas
    /// and saves it with the layout. Returns false when the layout already holds an item with the
    /// same id. The target itself is only referenced.
    /// </summary>
    Task<bool> AddItemAsync(DesktopItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes an item off the canvas and out of the layout. The file, shortcut or folder it points
    /// at is not touched; no real desktop item is ever deleted from here. Returns false when the
    /// layout does not hold the item.
    /// </summary>
    Task<bool> RemoveItemAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings the items an adoption planned onto the canvas, each pointing at where the file already
    /// is. Answered the same way whether the canvas is showing or not, and answered with the items
    /// that were added so a first run can say what it did. Nothing is adopted while adoption is
    /// switched off for the layout, and an entry the plan already saw on the canvas is not added
    /// twice.
    /// </summary>
    Task<DesktopAdoptionResult> AdoptAsync(
        DesktopAdoptionPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The dock as it is configured right now, as a copy the caller may keep. Answered whether the
    /// canvas is showing or not.
    /// </summary>
    Task<DockOptions> GetDockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the dock: whether it is on, which edge it hugs, whether it hides itself, and what it
    /// holds. Entries the items no longer support are dropped. Returns false when the change does not
    /// make a coherent dock.
    /// </summary>
    Task<bool> UpdateDockAsync(DockOptions dock, CancellationToken cancellationToken = default);

    /// <summary>
    /// The desktop mode and the list of turned-down desktop sources as the layout keeps them, as a
    /// copy the caller may keep. Answered whether the canvas is showing or not.
    /// </summary>
    Task<DesktopTakeoverOptions> GetTakeoverOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers the desktop mode and the adoption settings. The canvas is not what carries them out —
    /// it only keeps them, so the next launch knows which desktop the user asked for.
    /// </summary>
    Task UpdateTakeoverOptionsAsync(
        DesktopTakeoverOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the canvas taking any input — no hover, no click, no hit area — without taking it off the
    /// desktop. It is how the shutdown order makes the desktop unclickable in the moment between the
    /// native icons coming back and the canvas going away, and it is safe to call with nothing mounted.
    /// </summary>
    Task SuspendInteractionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The canvas as it looked at its last change, for the development overlay; readable from any
    /// thread. <c>null</c> while the canvas is not showing.
    /// </summary>
    CanvasDiagnosticsSnapshot? Diagnostics { get; }
}
