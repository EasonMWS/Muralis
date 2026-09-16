using Muralis.Core.Canvas;
using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Shows the desktop canvas prototype — a free-form layer of items above the desktop icons — and
/// takes it away again. Implemented per platform in the app layer (<c>Muralis.Desktop</c>) so view
/// models never touch native APIs. The implementation reuses the desktop shell and adds nothing to
/// the system: switching the canvas off returns the desktop exactly as it was, and the native icons
/// keep working either way.
/// </summary>
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
    /// The canvas as it looked at its last change, for the development overlay; readable from any
    /// thread. <c>null</c> while the canvas is not showing.
    /// </summary>
    CanvasDiagnosticsSnapshot? Diagnostics { get; }
}
