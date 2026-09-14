using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

public sealed class ImageCacheService : IImageCacheService
{
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

        foreach (var wallpaper in wallpapers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (wallpaper.HasLocalFile || wallpaper.CachedThumbnailPath is not null || string.IsNullOrEmpty(wallpaper.ThumbnailUrl))
            {
                continue;
            }

            try
            {
                var targetPath = Path.Combine(
                    _thumbnailDirectory,
                    WallpaperId.ShortHash(wallpaper.ThumbnailUrl) + ".jpg");

                if (!File.Exists(targetPath))
                {
                    var bytes = await _httpClient
                        .GetByteArrayAsync(wallpaper.ThumbnailUrl, cancellationToken)
                        .ConfigureAwait(true); // resume on the caller's (UI) thread so the assignment below is safe
                    await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(true);
                }

                wallpaper.CachedThumbnailPath = targetPath;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Could not cache the thumbnail for {Id}", wallpaper.Id);
            }
        }
    }

    public long GetCacheSize() => DirectoryHelper.GetDirectorySize(_thumbnailDirectory);
}
