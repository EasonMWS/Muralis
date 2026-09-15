namespace Muralis.Desktop.Surfaces;

/// <summary>
/// What a surface shows. Lifecycle contract: the shell mounts it once per attach and tells it
/// when its geometry changed; it never tells content about Explorer restarts — the shell
/// re-mounts it instead (a fresh <see cref="MountAsync"/> after an unmount). Content must be
/// usable on any thread the shell chooses and must not block the caller's thread.
/// </summary>
public interface ISurfaceContent : IAsyncDisposable
{
    SurfaceKind Kind { get; }

    /// <summary>Input the content needs. <see cref="SurfaceInteraction.None"/> for anything behind the icons.</summary>
    SurfaceInteraction Interaction { get; }

    /// <summary>When the content may be activated. <see cref="SurfaceActivation.Never"/> for anything behind the icons.</summary>
    SurfaceActivation Activation { get; }

    /// <summary>Starts showing on <paramref name="target"/>. Called again after every re-mount.</summary>
    Task MountAsync(ISurfaceTarget target, CancellationToken cancellationToken);

    /// <summary>Stops showing and releases whatever the mount acquired. Must tolerate being called when not mounted.</summary>
    Task UnmountAsync();

    /// <summary>The display's size or DPI changed while mounted.</summary>
    void OnGeometryChanged(MonitorGeometry geometry, double scale);
}
