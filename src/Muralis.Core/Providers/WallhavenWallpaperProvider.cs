using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Providers;

/// <summary>
/// Wallhaven (wallhaven.cc) via its public API. Muralis only ever requests SFW content
/// (<c>purity=100</c>) and keeps to the documented endpoints; responses are cached so the
/// API is not polled while browsing. An API key is optional — without one, SFW search and
/// browsing still work.
/// </summary>
public sealed class WallhavenWallpaperProvider : IWallpaperProvider
{
    private const string SearchEndpoint = "https://wallhaven.cc/api/v1/search";
    private const string DetailEndpoint = "https://wallhaven.cc/api/v1/w/";

    /// <summary>General + anime + people categories.</summary>
    private const string Categories = "111";

    /// <summary>SFW only. Muralis never requests sketchy or NSFW content.</summary>
    private const string Purity = "100";

    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FeaturedCacheLifetime = TimeSpan.FromHours(3);

    private readonly HttpClient _httpClient;
    private readonly IProviderConfiguration _configuration;
    private readonly ILogger<WallhavenWallpaperProvider> _logger;

    public WallhavenWallpaperProvider(
        HttpClient httpClient,
        IProviderConfiguration configuration,
        ILogger<WallhavenWallpaperProvider> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public string Id => "wallhaven";

    public string DisplayName => "Wallhaven";

    public bool SupportsSearch => true;

    public async Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(
        WallpaperQuery query,
        CancellationToken cancellationToken = default)
    {
        var url = BuildSearchUrl(
            searchText: query.SearchText,
            sorting: string.IsNullOrWhiteSpace(query.SearchText) ? "date_added" : "relevance",
            page: query.Page,
            topRange: null);

        var response = await FetchSearchAsync(url, SearchCacheLifetime, cancellationToken).ConfigureAwait(false);
        return MapItems(response).Take(query.PageSize).ToList();
    }

    public async Task<IReadOnlyList<Wallpaper>> GetFeaturedAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        var url = BuildSearchUrl(searchText: null, sorting: "toplist", page: 1, topRange: "1M");
        var response = await FetchSearchAsync(url, FeaturedCacheLifetime, cancellationToken).ConfigureAwait(false);
        return MapItems(response).Take(count).ToList();
    }

    public async Task<Wallpaper?> GetWallpaperAsync(string id, CancellationToken cancellationToken = default)
    {
        var hash = id.StartsWith(Id + ":", StringComparison.Ordinal) ? id[(Id.Length + 1)..] : id;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var url = DetailEndpoint + Uri.EscapeDataString(hash) + BuildApiKeyQuery();
        var response = await RemoteJson
            .GetAsync<WallhavenDetailResponse>(_httpClient, url, SearchCacheLifetime, _logger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response?.Data is null ? null : MapItem(response.Data);
    }

    private async Task<WallhavenSearchResponse> FetchSearchAsync(string url, TimeSpan cacheLifetime, CancellationToken cancellationToken)
    {
        var response = await RemoteJson
            .GetAsync<WallhavenSearchResponse>(_httpClient, url, cacheLifetime, _logger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response ?? new WallhavenSearchResponse();
    }

    private string BuildSearchUrl(string? searchText, string sorting, int page, string? topRange)
    {
        var query = new List<string>
        {
            "categories=" + Categories,
            "purity=" + Purity,
            "sorting=" + Uri.EscapeDataString(sorting),
            "page=" + Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(topRange))
        {
            query.Add("topRange=" + Uri.EscapeDataString(topRange));
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query.Add("q=" + Uri.EscapeDataString(searchText.Trim()));
        }

        var apiKey = _configuration.GetApiKey(Id);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            query.Add("apikey=" + Uri.EscapeDataString(apiKey));
        }

        return SearchEndpoint + "?" + string.Join('&', query);
    }

    private string BuildApiKeyQuery()
    {
        var apiKey = _configuration.GetApiKey(Id);
        return string.IsNullOrWhiteSpace(apiKey) ? string.Empty : "?apikey=" + Uri.EscapeDataString(apiKey);
    }

    private IEnumerable<Wallpaper> MapItems(WallhavenSearchResponse response)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in response.Data)
        {
            var wallpaper = MapItem(item);
            if (wallpaper is null || !seen.Add(wallpaper.Id))
            {
                continue;
            }

            yield return wallpaper;
        }
    }

    private Wallpaper? MapItem(WallhavenItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        return new Wallpaper
        {
            Id = WallpaperId.ForRemote(Id, item.Id),
            Title = BuildTitle(item),
            RemoteUrl = item.Path,
            ThumbnailUrl = item.Thumbs?.Large ?? item.Thumbs?.Original ?? item.Path,
            Width = item.DimensionX,
            Height = item.DimensionY,
            FileSize = item.FileSize,
            CreatedAt = ParseCreatedAt(item.CreatedAt),
            Tags = BuildTags(item),
            Source = WallpaperSource.Online,
        };
    }

    private string BuildTitle(WallhavenItem item)
    {
        // Wallhaven's search API carries no title; the short hash is the wallhaven.cc id,
        // which is what the site itself shows.
        return "Wallhaven " + item.Id;
    }

    private static IReadOnlyList<string> BuildTags(WallhavenItem item)
    {
        var tags = new List<string> { "Wallhaven" };
        if (!string.IsNullOrWhiteSpace(item.Category))
        {
            tags.Add(FileNameHelper.ToTitle(item.Category));
        }

        return tags;
    }

    private static DateTimeOffset ParseCreatedAt(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.Now;

    private sealed class WallhavenSearchResponse
    {
        [JsonPropertyName("data")]
        public List<WallhavenItem> Data { get; set; } = [];
    }

    private sealed class WallhavenDetailResponse
    {
        [JsonPropertyName("data")]
        public WallhavenItem? Data { get; set; }
    }

    private sealed class WallhavenItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("dimension_x")]
        public int DimensionX { get; set; }

        [JsonPropertyName("dimension_y")]
        public int DimensionY { get; set; }

        [JsonPropertyName("file_size")]
        public long FileSize { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("thumbs")]
        public WallhavenThumbs? Thumbs { get; set; }
    }

    private sealed class WallhavenThumbs
    {
        [JsonPropertyName("large")]
        public string? Large { get; set; }

        [JsonPropertyName("original")]
        public string? Original { get; set; }

        [JsonPropertyName("small")]
        public string? Small { get; set; }
    }
}
