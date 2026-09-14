using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>
/// In-memory wallpaper library. Milestone 3 swaps the storage for SQLite behind
/// the same <see cref="ILocalLibrary"/> interface.
/// </summary>
public sealed class LocalLibrary : ILocalLibrary
{
    private static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".avif"];

    private readonly ILogger<LocalLibrary> _logger;
    private readonly Lock _gate = new();
    private readonly List<Wallpaper> _items = [];

    public LocalLibrary(ILogger<LocalLibrary> logger) => _logger = logger;

    public IReadOnlyList<Wallpaper> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public event EventHandler? Changed;

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
            lock (_gate)
            {
                if (_items.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
                {
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

            lock (_gate)
            {
                _items.Add(wallpaper);
            }

            added++;
        }

        _logger.LogInformation(
            "Library import finished: {Added} added, {Duplicates} duplicates, {Failed} failed",
            added, duplicates, failed);

        if (added > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return new ImportResult(added, duplicates, failed);
    }

    public bool Remove(string wallpaperId)
    {
        ArgumentException.ThrowIfNullOrEmpty(wallpaperId);

        lock (_gate)
        {
            var removed = _items.RemoveAll(item => string.Equals(item.Id, wallpaperId, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                _logger.LogInformation("Removed {Id} from the library (file on disk is untouched)", wallpaperId);
            }

            if (removed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return removed;
        }
    }

    public Wallpaper? Find(string wallpaperId)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(item => string.Equals(item.Id, wallpaperId, StringComparison.Ordinal));
        }
    }

    public void MarkUsed(string wallpaperId, DateTimeOffset usedAt)
    {
        var item = Find(wallpaperId);
        if (item is not null)
        {
            item.LastUsedAt = usedAt;
        }
    }

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
                return new Wallpaper
                {
                    Id = id,
                    Title = FileNameHelper.ToTitle(path),
                    LocalPath = path,
                    Width = width,
                    Height = height,
                    FileSize = new FileInfo(path).Length,
                    CreatedAt = new FileInfo(path).CreationTimeUtc,
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
