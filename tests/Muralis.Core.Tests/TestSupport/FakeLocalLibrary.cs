using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.Core.Tests.TestSupport;

/// <summary>In-memory catalog used by provider-manager tests to verify state overlay.</summary>
internal sealed class FakeLocalLibrary : ILocalLibrary
{
    private readonly Dictionary<string, Wallpaper> _items = new(StringComparer.Ordinal);

    public IReadOnlyList<Wallpaper> Items => _items.Values.Where(item => item.Source == WallpaperSource.Local).ToList();

    public IReadOnlyList<Wallpaper> Favorites => _items.Values.Where(item => item.IsFavorite).ToList();

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public void Add(Wallpaper wallpaper) => _items[wallpaper.Id] = wallpaper;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ImportResult> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ImportResult(0, 0, 0));

    public Task<bool> RemoveAsync(string wallpaperId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.Remove(wallpaperId));

    public Task SetFavoriteAsync(Wallpaper wallpaper, bool isFavorite, CancellationToken cancellationToken = default)
    {
        wallpaper.IsFavorite = isFavorite;
        _items[wallpaper.Id] = wallpaper;
        return Task.CompletedTask;
    }

    public Task SaveAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default)
    {
        _items[wallpaper.Id] = wallpaper;
        return Task.CompletedTask;
    }

    public Task RecordUsageAsync(Wallpaper wallpaper, string? monitorName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Wallpaper>> GetRecentlyUsedAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Wallpaper>>([]);

    public Wallpaper? Find(string wallpaperId) => _items.GetValueOrDefault(wallpaperId);
}
