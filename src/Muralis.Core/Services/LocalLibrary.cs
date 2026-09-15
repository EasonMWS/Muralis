using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>
/// The catalog facade in front of <see cref="IWallpaperRepository"/>. Keeps an in-memory
/// mirror so the UI can read synchronously, while every change is written through to SQLite.
/// </summary>
public sealed class LocalLibrary : ILocalLibrary
{
    private static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".avif"];

    private readonly IWallpaperRepository _repository;
    private readonly ILogger<LocalLibrary> _logger;
    private readonly Lock _gate = new();
    private readonly Lock _backfillGate = new();
    private readonly List<Wallpaper> _cache = [];
    private Task? _hashBackfill;

    public LocalLibrary(IWallpaperRepository repository, ILogger<LocalLibrary> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public IReadOnlyList<Wallpaper> Items
    {
        get
        {
            lock (_gate)
            {
                return _cache
                    .Where(item => item.Source == WallpaperSource.Local)
                    .OrderByDescending(item => item.CreatedAt)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<Wallpaper> Favorites
    {
        get
        {
            lock (_gate)
            {
                return _cache
                    .Where(item => item.IsFavorite)
                    .OrderByDescending(item => item.CreatedAt)
                    .ToArray();
            }
        }
    }

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var items = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _cache.Clear();
            _cache.AddRange(items);
        }

        _logger.LogInformation("Catalog loaded with {Count} wallpapers ({Favorites} favorites)",
            items.Count, items.Count(item => item.IsFavorite));

        RaiseChanged();

        // Hash the entries that predate content-based duplicate detection in the background;
        // anything that needs the hashes awaits this task through EnsureContentHashesAsync.
        _ = EnsureContentHashesAsync();
    }

    public async Task<ImportResult> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        await EnsureContentHashesAsync().ConfigureAwait(false);

        var added = 0;
        var duplicates = 0;
        var failed = 0;
        var batchHashes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSupportedImage(path))
            {
                _logger.LogDebug("Skipping unsupported file {Path}", path);
                failed++;
                continue;
            }

            var id = WallpaperId.ForLocalFile(path);
            if (Find(id) is not null)
            {
                duplicates++;
                continue;
            }

            var contentHash = await ContentHash.TryComputeAsync(path, cancellationToken).ConfigureAwait(false);
            if (contentHash is not null)
            {
                var match = FindByHash(contentHash);
                if (match is not null || batchHashes.Contains(contentHash))
                {
                    _logger.LogInformation(
                        "Skipping '{Path}': identical content is already in the library as '{Existing}'",
                        path,
                        match?.Id ?? "another file of this import");
                    duplicates++;
                    continue;
                }
            }

            var wallpaper = await CreateWallpaperAsync(id, path).ConfigureAwait(false);
            if (wallpaper is null)
            {
                failed++;
                continue;
            }

            wallpaper.ContentHash = contentHash;
            await _repository.UpsertAsync(wallpaper, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _cache.Add(wallpaper);
            }

            if (contentHash is not null)
            {
                batchHashes.Add(contentHash);
            }

            added++;
        }

        _logger.LogInformation(
            "Library import finished: {Added} added, {Duplicates} duplicates, {Failed} failed",
            added, duplicates, failed);

        if (added > 0)
        {
            RaiseChanged();
        }

        return new ImportResult(added, duplicates, failed);
    }

    public async Task<bool> RemoveAsync(string wallpaperId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(wallpaperId);

        var removed = await _repository.DeleteAsync(wallpaperId, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return false;
        }

        lock (_gate)
        {
            _cache.RemoveAll(item => string.Equals(item.Id, wallpaperId, StringComparison.Ordinal));
        }

        _logger.LogInformation("Removed {Id} from the library (file on disk is untouched)", wallpaperId);
        RaiseChanged();
        return true;
    }

    public async Task SetFavoriteAsync(Wallpaper wallpaper, bool isFavorite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);

        wallpaper.IsFavorite = isFavorite;
        await _repository.UpsertAsync(wallpaper, cancellationToken).ConfigureAwait(false);
        EnsureCached(wallpaper);

        _logger.LogDebug("Favorite state for {Id} set to {IsFavorite}", wallpaper.Id, isFavorite);
        RaiseChanged();
    }

    public async Task SaveAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);

        await _repository.UpsertAsync(wallpaper, cancellationToken).ConfigureAwait(false);
        EnsureCached(wallpaper);
        RaiseChanged();
    }

    public async Task RecordUsageAsync(Wallpaper wallpaper, string? monitorName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);

        var now = DateTimeOffset.Now;
        wallpaper.LastUsedAt = now;

        await _repository.UpsertAsync(wallpaper, cancellationToken).ConfigureAwait(false);
        await _repository.AddHistoryAsync(
            new HistoryEntry
            {
                WallpaperId = wallpaper.Id,
                Title = wallpaper.Title,
                AppliedAt = now,
                MonitorName = monitorName,
            },
            cancellationToken).ConfigureAwait(false);

        EnsureCached(wallpaper);
        RaiseChanged();
    }

    public Task<IReadOnlyList<Wallpaper>> GetRecentlyUsedAsync(int limit, CancellationToken cancellationToken = default) =>
        _repository.GetRecentlyUsedAsync(limit, cancellationToken);

    public Wallpaper? Find(string wallpaperId)
    {
        lock (_gate)
        {
            return _cache.FirstOrDefault(item => string.Equals(item.Id, wallpaperId, StringComparison.Ordinal));
        }
    }

    public async Task<Wallpaper?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentHash);

        await EnsureContentHashesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return FindByHash(contentHash);
    }

    private Wallpaper? FindByHash(string contentHash)
    {
        lock (_gate)
        {
            return _cache.FirstOrDefault(item =>
                item.ContentHash is { Length: > 0 } hash
                && string.Equals(hash, contentHash, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Runs the hash backfill at most once; everybody who needs hashes awaits the same task.
    /// </summary>
    private Task EnsureContentHashesAsync()
    {
        lock (_backfillGate)
        {
            return _hashBackfill ??= BackfillContentHashesAsync();
        }
    }

    /// <summary>
    /// Computes and persists content hashes for catalog entries that have a file on disk
    /// but no hash yet (entries imported before this was tracked). Never throws.
    /// </summary>
    private async Task BackfillContentHashesAsync()
    {
        try
        {
            Wallpaper[] pending;
            lock (_gate)
            {
                pending = _cache
                    .Where(item => item.ContentHash is null && !string.IsNullOrEmpty(item.LocalPath))
                    .ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            var updated = 0;
            foreach (var wallpaper in pending)
            {
                var hash = await ContentHash.TryComputeAsync(wallpaper.LocalPath!).ConfigureAwait(false);
                if (hash is null)
                {
                    continue;
                }

                wallpaper.ContentHash = hash;
                await _repository.UpsertAsync(wallpaper).ConfigureAwait(false);
                updated++;
            }

            _logger.LogInformation(
                "Computed content hashes for {Updated} of {Total} catalog entries",
                updated,
                pending.Length);
        }
        catch (Exception ex)
        {
            // Duplicate detection degrades to the file's identity until the next attempt.
            _logger.LogWarning(ex, "Content-hash backfill failed");
        }
    }

    private void EnsureCached(Wallpaper wallpaper)
    {
        lock (_gate)
        {
            // Replace any stale entry: callers hand over a fresh object for the same id
            // (provider results, a second session's reload), and the catalog must reflect
            // the state that was just written instead of keeping the older instance.
            var index = _cache.FindIndex(item => string.Equals(item.Id, wallpaper.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _cache[index] = wallpaper;
            }
            else
            {
                _cache.Add(wallpaper);
            }
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static bool IsSupportedImage(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(path)
        && SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static async Task<Wallpaper?> CreateWallpaperAsync(string id, string path)
    {
        try
        {
            // Header parsing and stat calls are cheap but touch the disk; keep them off the UI thread.
            return await Task.Run(() =>
            {
                ImageMetadataReader.TryReadDimensions(path, out var width, out var height);
                var fileInfo = new FileInfo(path);
                return new Wallpaper
                {
                    Id = id,
                    Title = FileNameHelper.ToTitle(path),
                    LocalPath = path,
                    Width = width,
                    Height = height,
                    FileSize = fileInfo.Length,
                    CreatedAt = fileInfo.CreationTimeUtc,
                    Source = WallpaperSource.Local,
                };
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
