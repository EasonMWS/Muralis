using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Providers;

/// <summary>
/// NASA's Astronomy Picture of the Day. Requires a (free) API key, configured through
/// <c>providers.json</c> or the <c>MURALIS_PROVIDER_NASA_API_KEY</c> environment variable —
/// the key is never compiled into the application. Video entries are skipped because only
/// images can become wallpapers.
/// </summary>
public sealed class ApodWallpaperProvider : IWallpaperProvider
{
    private const string Endpoint = "https://api.nasa.gov/planetary/apod";

    /// <summary>NASA serves today's image when no date is given.</summary>
    private static readonly TimeSpan FeaturedCacheLifetime = TimeSpan.FromHours(6);

    private static readonly TimeSpan CollectionCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly IProviderConfiguration _configuration;
    private readonly ILogger<ApodWallpaperProvider> _logger;

    public ApodWallpaperProvider(
        HttpClient httpClient,
        IProviderConfiguration configuration,
        ILogger<ApodWallpaperProvider> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public string Id => "nasa";

    public string DisplayName => "NASA APOD";

    /// <summary>The archive is a daily image; it has no search API.</summary>
    public bool SupportsSearch => false;

    public bool RequiresApiKey => true;

    public async Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(
        WallpaperQuery query,
        CancellationToken cancellationToken = default)
    {
        // APOD has no search or paging: return a fresh random selection of the archive.
        var count = Math.Clamp(query.PageSize, 1, 50);
        var url = BuildUrl($"count={count.ToString(CultureInfo.InvariantCulture)}");
        var items = await RemoteJson
            .GetAsync<List<ApodItem>>(_httpClient, url, CollectionCacheLifetime, _logger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return MapItems(items);
    }

    public async Task<IReadOnlyList<Wallpaper>> GetFeaturedAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(null);
        var item = await RemoteJson
            .GetAsync<ApodItem>(_httpClient, url, FeaturedCacheLifetime, _logger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var wallpaper = item is null ? null : MapItem(item);
        return wallpaper is null ? [] : [wallpaper];
    }

    public async Task<Wallpaper?> GetWallpaperAsync(string id, CancellationToken cancellationToken = default)
    {
        var date = id.StartsWith(Id + ":", StringComparison.Ordinal) ? id[(Id.Length + 1)..] : id;
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var url = BuildUrl("date=" + parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var item = await RemoteJson
            .GetAsync<ApodItem>(_httpClient, url, FeaturedCacheLifetime, _logger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return item is null ? null : MapItem(item);
    }

    private string BuildUrl(string? parameters)
    {
        var apiKey = _configuration.GetApiKey(Id);
        var query = string.IsNullOrWhiteSpace(parameters) ? string.Empty : parameters + "&";
        return $"{Endpoint}?{query}api_key={Uri.EscapeDataString(apiKey ?? string.Empty)}";
    }

    private List<Wallpaper> MapItems(List<ApodItem>? items)
    {
        var wallpapers = new List<Wallpaper>();
        foreach (var item in items ?? [])
        {
            var wallpaper = MapItem(item);
            if (wallpaper is not null)
            {
                wallpapers.Add(wallpaper);
            }
        }

        return wallpapers;
    }

    private Wallpaper? MapItem(ApodItem item)
    {
        // Only still images can be applied as a wallpaper.
        if (!string.Equals(item.MediaType, "image", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(item.Url)
            || string.IsNullOrWhiteSpace(item.Date))
        {
            return null;
        }

        return new Wallpaper
        {
            Id = WallpaperId.ForRemote(Id, item.Date),
            Title = string.IsNullOrWhiteSpace(item.Title) ? "NASA " + item.Date : item.Title!,
            RemoteUrl = string.IsNullOrWhiteSpace(item.HdUrl) ? item.Url : item.HdUrl,
            ThumbnailUrl = item.Url,
            CreatedAt = ParseDate(item.Date),
            Tags = BuildTags(item),
            Source = WallpaperSource.Online,
        };
    }

    private static IReadOnlyList<string> BuildTags(ApodItem item)
    {
        var tags = new List<string> { "NASA", "Space" };
        var credit = item.Copyright?.Trim();
        if (!string.IsNullOrWhiteSpace(credit))
        {
            tags.Add(credit);
        }

        return tags;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.Now;

    private sealed class ApodItem
    {
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("media_type")]
        public string? MediaType { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("hdurl")]
        public string? HdUrl { get; set; }

        [JsonPropertyName("copyright")]
        public string? Copyright { get; set; }
    }
}
