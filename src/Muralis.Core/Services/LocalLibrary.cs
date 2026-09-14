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
    private readonly List<Wallpaper> _cache = [];

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
    }

    public async Task<ImportResult> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var added = 0;
        var duplicates = 0;
        var failed = 0;

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

            var wallpaper = await CreateWallpaperAsync(id, path).ConfigureAwait(false);
            if (wallpaper is null)
            {
                failed++;
                continue;
            }

            await _repository.UpsertAsync(wallpaper, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _cache.Add(wallpaper);
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

    private void EnsureCached(Wallpaper wallpaper)
    {
        lock (_gate)
        {
            if (_cache.All(item => !string.Equals(item.Id, wallpaper.Id, StringComparison.Ordinal)))
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
