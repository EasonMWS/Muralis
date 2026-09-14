using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.Core.Tests.TestSupport;

/// <summary>Configurable provider used to test the provider manager without network access.</summary>
internal sealed class FakeWallpaperProvider : IWallpaperProvider
{
    public FakeWallpaperProvider(string id, string? displayName = null)
    {
        Id = id;
        DisplayName = displayName ?? id;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public bool SupportsSearch { get; set; } = true;

    public bool RequiresApiKey { get; set; }

    public Func<int, IReadOnlyList<Wallpaper>>? OnFeatured { get; set; }

    public Func<WallpaperQuery, IReadOnlyList<Wallpaper>>? OnSearch { get; set; }

    public Exception? SearchException { get; set; }

    public int SearchCount { get; private set; }

    public List<WallpaperQuery> SearchQueries { get; } = [];

    public string? DownloadUrlOverride { get; set; }

    public Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        SearchCount++;
        SearchQueries.Add(query);

        return SearchException is not null
            ? Task.FromException<IReadOnlyList<Wallpaper>>(SearchException)
            : Task.FromResult(OnSearch?.Invoke(query) ?? []);
    }

    public Task<IReadOnlyList<Wallpaper>> GetFeaturedAsync(int count, CancellationToken cancellationToken = default) =>
        SearchException is not null
            ? Task.FromException<IReadOnlyList<Wallpaper>>(SearchException)
            : Task.FromResult(OnFeatured?.Invoke(count) ?? OnSearch?.Invoke(new WallpaperQuery { PageSize = count }) ?? []);

    public Task<Wallpaper?> GetWallpaperAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<Wallpaper?>(null);

    public Task<string> GetDownloadUrlAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default) =>
        Task.FromResult(DownloadUrlOverride ?? wallpaper.RemoteUrl ?? string.Empty);
}
