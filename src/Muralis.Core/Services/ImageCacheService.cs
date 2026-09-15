using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

public sealed class ImageCacheService : IImageCacheService
{
    /// <summary>Enough to fill a screenful at once without hammering the source.</summary>
    private const int MaxParallelDownloads = 4;

    /// <summary>
    /// Ceiling for the on-disk thumbnail folder. Thumbnails are small (tens of KB), so this
    /// holds thousands of wallpapers; the oldest files are dropped once the cap is crossed so a
    /// long-lived install cannot grow the cache without bound.
    /// </summary>
    private const long DefaultMaxCacheBytes = 256L * 1024 * 1024;

    private const double PruneTargetRatio = 0.8;

    private readonly HttpClient _httpClient;
    private readonly ILogger<ImageCacheService> _logger;
    private readonly string _thumbnailDirectory;
    private readonly long _maxCacheBytes;

    public ImageCacheService(
        HttpClient httpClient,
        ILogger<ImageCacheService> logger,
        string? thumbnailDirectory = null,
        long maxCacheBytes = DefaultMaxCacheBytes)
    {
        _httpClient = httpClient;
        _logger = logger;
        _thumbnailDirectory = thumbnailDirectory ?? AppPaths.ThumbnailsDirectory;
        _maxCacheBytes = maxCacheBytes;
    }

    public async Task WarmThumbnailsAsync(IReadOnlyList<Wallpaper> wallpapers, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_thumbnailDirectory);

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var pending = new List<(Wallpaper Wallpaper, string TargetPath)>();
        var skipped = 0;

        foreach (var wallpaper in wallpapers)
        {
            if (wallpaper.HasLocalFile
                || wallpaper.CachedThumbnailPath is not null
                || string.IsNullOrEmpty(wallpaper.ThumbnailUrl))
            {
                skipped++;
                continue;
            }

            var targetPath = Path.Combine(
                _thumbnailDirectory,
                WallpaperId.ShortHash(wallpaper.ThumbnailUrl) + ".jpg");

            if (File.Exists(targetPath))
            {
                // Already downloaded earlier; just point the wallpaper at it.
                wallpaper.CachedThumbnailPath = targetPath;
                skipped++;
                continue;
            }

            pending.Add((wallpaper, targetPath));
        }

        if (pending.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(MaxParallelDownloads);
        var downloads = pending
            .Select(item => DownloadThumbnailAsync(item.Wallpaper, item.TargetPath, gate, cancellationToken))
            .ToList();

        try
        {
            // Resume on the caller's thread: the assignments below touch bound properties.
            await Task.WhenAll(downloads).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The page was left; whatever finished is still assigned below.
        }

        var fetched = 0;
        foreach (var download in downloads)
        {
            if (download.Status == TaskStatus.RanToCompletion && download.Result is { } completed)
            {
                completed.Wallpaper.CachedThumbnailPath = completed.TargetPath;
                fetched++;
            }
        }

        if (fetched > 0)
        {
            // New files are the only way the folder grows, so this is where the bound is checked.
            PruneCacheIfOverLimit();
        }

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Thumbnail warm-up cancelled: {Fetched} of {Total} fetched in {ElapsedMs:0} ms",
                fetched,
                pending.Count,
                elapsedMs);
            return;
        }

        _logger.LogInformation(
            "Thumbnails warmed: {Fetched} of {Total} fetched ({Skipped} not needed) in {ElapsedMs:0} ms",
            fetched,
            pending.Count,
            skipped,
            elapsedMs);
    }

    private async Task<(Wallpaper Wallpaper, string TargetPath)?> DownloadThumbnailAsync(
        Wallpaper wallpaper,
        string targetPath,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        var url = wallpaper.ThumbnailUrl;
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            var bytes = await _httpClient
                .GetByteArrayAsync(url, cancellationToken)
                .ConfigureAwait(false);

            // A unique temporary name keeps concurrent warm-ups of the same thumbnail safe.
            var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
            return (wallpaper, targetPath);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not cache the thumbnail for {Id}", wallpaper.Id);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public long GetCacheSize() => DirectoryHelper.GetDirectorySize(_thumbnailDirectory);

    /// <summary>
    /// Drops the oldest thumbnails once the folder exceeds <see cref="_maxCacheBytes"/>. Evicted
    /// files re-download on demand, so pruning costs at most one extra fetch per wallpaper.
    /// </summary>
    private void PruneCacheIfOverLimit()
    {
        try
        {
            var directory = new DirectoryInfo(_thumbnailDirectory);
            if (!directory.Exists)
            {
                return;
            }

            var files = directory.GetFiles("*.jpg");
            long totalBytes = 0;
            foreach (var file in files)
            {
                totalBytes += file.Length;
            }

            if (totalBytes <= _maxCacheBytes)
            {
                return;
            }

            var targetBytes = (long)(_maxCacheBytes * PruneTargetRatio);
            var removed = 0;
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (totalBytes <= targetBytes)
                {
                    break;
                }

                try
                {
                    var length = file.Length;
                    file.Delete();
                    totalBytes -= length;
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file in use stays until the next sweep.
                }
            }

            _logger.LogInformation(
                "Thumbnail cache pruned: {Removed} of {Total} files removed, {Megabytes:0.0} MB kept",
                removed,
                files.Length,
                totalBytes / 1024d / 1024d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not prune the thumbnail cache");
        }
    }
}
