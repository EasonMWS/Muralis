using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

public sealed class ImageCacheService : IImageCacheService
{
    /// <summary>Enough to fill a screenful at once without hammering the source.</summary>
    private const int MaxParallelDownloads = 4;

    private readonly HttpClient _httpClient;
    private readonly ILogger<ImageCacheService> _logger;
    private readonly string _thumbnailDirectory;

    public ImageCacheService(HttpClient httpClient, ILogger<ImageCacheService> logger, string? thumbnailDirectory = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _thumbnailDirectory = thumbnailDirectory ?? AppPaths.ThumbnailsDirectory;
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
}
