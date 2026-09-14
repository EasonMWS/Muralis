using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>Caches remote wallpaper thumbnails on disk so grids render offline and instantly.</summary>
public interface IImageCacheService
{
    /// <summary>
    /// Ensures every online wallpaper in <paramref name="wallpapers"/> has a local thumbnail
    /// and assigns <see cref="Wallpaper.CachedThumbnailPath"/>. Already-cached items are skipped.
    /// </summary>
    Task WarmThumbnailsAsync(IReadOnlyList<Wallpaper> wallpapers, CancellationToken cancellationToken = default);

    /// <summary>Total size of the thumbnail cache in bytes.</summary>
    long GetCacheSize();
}
