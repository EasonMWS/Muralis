using Muralis.Core.Models;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// One registered mount: content bound to a display. Held by the shell, which owns mounting,
/// re-mounting after shell restarts, positioning and z-order; callers only keep the handle they
/// get back from the shell.
/// </summary>
public interface IDesktopSurface : IAsyncDisposable
{
    DesktopSurfaceId Id { get; }

    ISurfaceContent Content { get; }

    SurfaceState State { get; }

    /// <summary>The display this surface is bound to. Stable across re-mounts.</summary>
    MonitorRef Monitor { get; }

    /// <summary>Position within the display's surface stack; higher draws on top.</summary>
    int ZOrder { get; }

    /// <summary>Mounts the content on the given host-provided target. Driven by the shell.</summary>
    Task AttachAsync(ISurfaceTarget target, CancellationToken cancellationToken);

    /// <summary>Unmounts the content. Driven by the shell.</summary>
    Task DetachAsync();

    /// <summary>Moves the surface to another display (or the same one with new geometry). Driven by the shell.</summary>
    void MoveTo(MonitorRef monitor, MonitorGeometry geometry);
}
