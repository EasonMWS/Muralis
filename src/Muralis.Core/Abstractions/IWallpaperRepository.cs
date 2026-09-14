using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>Persistent storage for the wallpaper catalog and usage history.</summary>
public interface IWallpaperRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Wallpaper>> GetAllAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default);

    /// <summary>Returns true when a record was deleted.</summary>
    Task<bool> DeleteAsync(string wallpaperId, CancellationToken cancellationToken = default);

    Task AddHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Catalog entries ordered by most recent desktop use.</summary>
    Task<IReadOnlyList<Wallpaper>> GetRecentlyUsedAsync(int limit, CancellationToken cancellationToken = default);
}
