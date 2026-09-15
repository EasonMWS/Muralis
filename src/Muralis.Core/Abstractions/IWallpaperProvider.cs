using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// A source of wallpapers (an online service, a bundled sample set, ...). Implementations
/// must be thread-safe: they are registered as singletons and shared. Enabling and
/// disabling sources, and which one is the default, are user settings handled by
/// <c>WallpaperProviderManager</c> — providers themselves stay stateless.
/// </summary>
public interface IWallpaperProvider
{
    /// <summary>Stable identifier, e.g. <c>bing</c>, <c>wallhaven</c>, <c>nasa</c>.</summary>
    string Id { get; }

    /// <summary>English fallback name; the UI localizes through the <c>Provider_{Id}</c> resource key.</summary>
    string DisplayName { get; }

    bool SupportsSearch { get; }

    /// <summary>True when the provider is unusable until the user configures an API key.</summary>
    bool RequiresApiKey => false;

    /// <summary>Searches (or lists) wallpapers. Providers without search return their newest selection.</summary>
    Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(WallpaperQuery query, CancellationToken cancellationToken = default);

    /// <summary>Hand-picked or editorial wallpapers used to fill the Home page.</summary>
    Task<IReadOnlyList<Wallpaper>> GetFeaturedAsync(int count, CancellationToken cancellationToken = default)
    {
        return GetWallpapersAsync(new WallpaperQuery { PageSize = count }, cancellationToken);
    }

    /// <summary>Loads a single wallpaper by its provider id. Returns <c>null</c> when unknown.</summary>
    Task<Wallpaper?> GetWallpaperAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// URL the download service should fetch. Providers that must be notified about
    /// downloads (or that need to transform the URL first) override this.
    /// </summary>
    Task<string> GetDownloadUrlAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default) =>
        Task.FromResult(wallpaper.RemoteUrl ?? string.Empty);
}

public sealed record WallpaperQuery
{
    public string? SearchText { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 30;

    /// <summary>Providers with native support translate this into request parameters; the rest are filtered by the manager.</summary>
    public WallpaperOrientation Orientation { get; init; } = WallpaperOrientation.Any;

    /// <summary>Smallest long-edge class to return; see <see cref="WallpaperResolution"/>.</summary>
    public WallpaperResolution MinimumResolution { get; init; } = WallpaperResolution.Any;
}
