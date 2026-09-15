using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// The user's wallpaper collection, persisted in the catalog database. Imported files are
/// referenced in place: the library never copies or modifies the original images.
/// </summary>
public interface ILocalLibrary
{
    /// <summary>Wallpapers imported from local files, newest first.</summary>
    IReadOnlyList<Wallpaper> Items { get; }

    /// <summary>Favorited wallpapers of any source.</summary>
    IReadOnlyList<Wallpaper> Favorites { get; }

    event EventHandler? Changed;

    /// <summary>Loads the catalog from disk. Called once during application startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the given image files to the library, skipping duplicates and unsupported files.
    /// </summary>
    Task<ImportResult> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>Removes the catalog record. The image file on disk is left untouched.</summary>
    Task<bool> RemoveAsync(string wallpaperId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a wallpaper as favorite / not favorite. Favoriting a wallpaper that is not in
    /// the catalog yet adds a record for it, so favorites survive restarts for any source.
    /// </summary>
    Task SetFavoriteAsync(Wallpaper wallpaper, bool isFavorite, CancellationToken cancellationToken = default);

    /// <summary>Persists changes made to a wallpaper (e.g. a finished download), adding it if needed.</summary>
    Task SaveAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a catalog entry whose file content matches the given hash, if any. Entries
    /// created before content hashes were tracked are hashed on first use.
    /// </summary>
    Task<Wallpaper?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default);

    /// <summary>Records that the wallpaper was applied to the desktop and when.</summary>
    Task RecordUsageAsync(Wallpaper wallpaper, string? monitorName, CancellationToken cancellationToken = default);

    /// <summary>Catalog entries ordered by most recent desktop use.</summary>
    Task<IReadOnlyList<Wallpaper>> GetRecentlyUsedAsync(int limit, CancellationToken cancellationToken = default);

    Wallpaper? Find(string wallpaperId);
}

public sealed record ImportResult(int Added, int Duplicates, int Failed);
