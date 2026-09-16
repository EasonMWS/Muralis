using Muralis.Desktop.Monitors;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop.Shell;

/// <summary>
/// The front door to the desktop layer: one instance per process, owner of the shell thread, the
/// display snapshot and every surface mounted on the desktop. Designed to stay cross-process
/// friendly (single future host process): data-only parameters, events instead of callbacks, no
/// UI-thread affinity, and it never hands out native window handles as API.
/// </summary>
public interface IDesktopShell
{
    /// <summary>Where the shell is in its lifecycle right now.</summary>
    DesktopShellState State { get; }

    /// <summary>The current display snapshot. The manager raises its own event when it changes.</summary>
    IMonitorManager Monitors { get; }

    /// <summary>Raised on every lifecycle transition. May be raised on a background thread.</summary>
    event EventHandler<DesktopShellState>? StateChanged;

    /// <summary>
    /// Explorer restarted, so everything mounted on the desktop layer is gone. The shell re-mounts
    /// its surfaces on its own; subscribers (tray, diagnostics) only need to react, not re-mount.
    /// May be raised on a background thread.
    /// </summary>
    event EventHandler? ShellRestarted;

    /// <summary>
    /// Registers the intent to show <paramref name="request"/>.Content on the requested display and
    /// returns the surface that represents that mount. The shell decides how and when to mount; the
    /// caller only holds the surface it can later remove.
    /// </summary>
    Task<IDesktopSurface> AddSurfaceAsync(SurfaceRequest request, CancellationToken cancellationToken);

    /// <summary>Removes a surface and releases everything behind it. Safe to call while orphaned.</summary>
    Task RemoveSurfaceAsync(IDesktopSurface surface, CancellationToken cancellationToken);

    /// <summary>
    /// Shuts the desktop layer down for good: every surface is released, the shell thread ends and
    /// nothing is mounted again. Idempotent, never cancelled — a shutdown abandoned halfway would
    /// leave desktop windows behind — and awaiting it means the desktop layer really is handed back.
    /// </summary>
    Task ShutdownAsync();
}
