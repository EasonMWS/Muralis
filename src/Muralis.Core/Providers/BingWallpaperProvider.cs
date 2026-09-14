using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Providers;

/// <summary>
/// Provider for the Bing daily images. Uses the public <c>HPImageArchive</c> endpoint,
/// which needs no API key. Full images are requested in UHD when Bing has them.
/// </summary>
public sealed class BingWallpaperProvider : IWallpaperProvider
{
    private const string ArchiveEndpoint = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt={0}";
    private const string ImageBase = "https://www.bing.com";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    private readonly HttpClient _httpClient;
    private readonly ILogger<BingWallpaperProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly List<Wallpaper> _cache = [];
    private DateTimeOffset _loadedAt;

    public BingWallpaperProvider(HttpClient httpClient, ILogger<BingWallpaperProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string Id => "bing";

    public string DisplayName => "Bing daily images";

    public bool SupportsSearch => false;

    public async Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        return _cache
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();
    }

    public async Task<IReadOnlyList<Wallpaper>> GetFeaturedAsync(int count, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _cache.Take(count).ToList();
    }

    public async Task<Wallpaper?> GetWallpaperAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _cache.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cache.Count > 0 && DateTimeOffset.Now - _loadedAt < CacheLifetime)
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.Count > 0 && DateTimeOffset.Now - _loadedAt < CacheLifetime)
            {
                return;
            }

            var market = CultureInfo.CurrentUICulture.Name;
            var url = string.Format(CultureInfo.InvariantCulture, ArchiveEndpoint, market);
            var response = await RemoteJson
                .GetAsync<BingArchiveResponse>(_httpClient, url, CacheLifetime, _logger, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var images = response?.Images ?? [];
            if (images.Count == 0)
            {
                throw new InvalidOperationException("The Bing wallpaper feed returned no images.");
            }

            _cache.Clear();
            foreach (var image in images)
            {
                var item = MapImage(image);
                if (item is not null)
                {
                    _cache.Add(item);
                }
            }

            _loadedAt = DateTimeOffset.Now;
            _logger.LogInformation("Bing provider loaded {Count} daily images", _cache.Count);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private Wallpaper? MapImage(BingImage image)
    {
        if (string.IsNullOrWhiteSpace(image.UrlBase) || string.IsNullOrWhiteSpace(image.StartDate))
        {
            return null;
        }

        var title = !string.IsNullOrWhiteSpace(image.Title)
            ? image.Title!
            : FirstSegment(image.Copyright);

        return new Wallpaper
        {
            Id = WallpaperId.ForRemote("bing", image.StartDate!),
            Title = title,
            RemoteUrl = $"{ImageBase}{image.UrlBase}_UHD.jpg",
            ThumbnailUrl = $"{ImageBase}{image.UrlBase}_1920x1080.jpg",
            Tags = ["Bing", "Daily"],
            Source = WallpaperSource.Online,
            CreatedAt = ParseStartDate(image.StartDate),
        };
    }

    private static string FirstSegment(string? copyright)
    {
        if (string.IsNullOrWhiteSpace(copyright))
        {
            return "Bing daily image";
        }

        var separator = copyright.IndexOf('(');
        var text = separator > 0 ? copyright[..separator] : copyright;
        return text.Trim().TrimEnd(',', '，');
    }

    private static DateTimeOffset ParseStartDate(string value) =>
        DateTimeOffset.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : DateTimeOffset.Now;

    private sealed class BingArchiveResponse
    {
        [JsonPropertyName("images")]
        public List<BingImage>? Images { get; set; }
    }

    private sealed class BingImage
    {
        [JsonPropertyName("startdate")]
        public string? StartDate { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("urlbase")]
        public string? UrlBase { get; set; }

        [JsonPropertyName("copyright")]
        public string? Copyright { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}
