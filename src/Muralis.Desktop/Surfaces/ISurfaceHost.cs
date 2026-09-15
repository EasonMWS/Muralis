namespace Muralis.Desktop.Surfaces;

/// <summary>
/// Creates and destroys the windows surfaces live in — the single owner of the desktop layer's
/// windowing mechanism. The shell is its only caller; new desktop content must go through the
/// shell instead of creating hosts of its own.
/// </summary>
public interface ISurfaceHost
{
    Task<IDesktopSurface> CreateAsync(SurfaceRequest request, CancellationToken cancellationToken);

    Task DestroyAsync(IDesktopSurface surface);
}
