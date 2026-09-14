using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// A source of wallpapers (local folder, online service, ...). Implementations
/// must be thread-safe: they are registered as singletons and shared.
/// </summary>
public interface IWallpaperProvider
{
    /// <summary>Stable identifier, e.g. <c>local</c>, <c>bing</c>, <c>unsplash</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    bool SupportsSearch { get; }

    Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(WallpaperQuery query, CancellationToken cancellationToken = default);

    Task<Wallpaper?> GetWallpaperAsync(string id, CancellationToken cancellationToken = default);
}

public sealed record WallpaperQuery
{
    public string? SearchText { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 30;
}
