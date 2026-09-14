using System.Text.Json;
using Microsoft.Extensions.Logging;
using Muralis.Core.Networking;

namespace Muralis.Core.Helpers;

/// <summary>
/// Fetches and deserializes JSON from the network with a bounded timeout, metadata caching
/// and retry (through the shared HTTP handler pipeline). Providers use this instead of
/// talking to <see cref="HttpClient"/> directly so every source behaves the same way.
/// </summary>
public static class RemoteJson
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <param name="cacheTtl">How long the response stays fresh in the metadata cache.</param>
    public static async Task<T?> GetAsync<T>(
        HttpClient httpClient,
        string url,
        TimeSpan cacheTtl,
        ILogger logger,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(MetadataCacheHandler.CacheTtlKey, cacheTtl);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }
}
