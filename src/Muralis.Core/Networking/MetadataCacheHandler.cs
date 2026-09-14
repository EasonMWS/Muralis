using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;

namespace Muralis.Core.Networking;

/// <summary>
/// Serves GET responses from the metadata cache while they are fresh. Only requests that
/// opt in by setting <see cref="CacheTtlKey"/> are cached, and only successful JSON
/// responses — image downloads and thumbnails pass through untouched.
/// </summary>
public sealed class MetadataCacheHandler : DelegatingHandler
{
    /// <summary>Request option carrying the freshness window for the response.</summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> CacheTtlKey = new("Muralis.MetadataCacheTtl");

    private const long MaxCacheableBytes = 1024 * 1024;

    private readonly IMetadataCache _cache;
    private readonly ILogger<MetadataCacheHandler> _logger;

    public MetadataCacheHandler(IMetadataCache cache, ILogger<MetadataCacheHandler> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public MetadataCacheHandler(IMetadataCache cache, ILogger<MetadataCacheHandler> logger, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _cache = cache;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get
            || request.RequestUri is null
            || !request.Options.TryGetValue(CacheTtlKey, out var timeToLive))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var key = request.RequestUri.ToString();
        var cached = await _cache.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            _logger.LogDebug("Metadata cache hit for {Host}{Path}", request.RequestUri.Host, request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(cached, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is > MaxCacheableBytes
            || !IsJson(response))
        {
            return response;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        await _cache.SetAsync(key, payload, timeToLive, cancellationToken).ConfigureAwait(false);

        response.Content.Dispose();
        response.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return response;
    }

    private static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is "application/json" or "text/json";
}
