using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Networking;
using Muralis.Core.Services;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class MetadataCacheTests : IDisposable
{
    private readonly string _directory;

    public MetadataCacheTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-metadata-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task TryGetAsync_WithNoEntry_ReturnsNull()
    {
        var cache = CreateCache();

        Assert.Null(await cache.TryGetAsync("missing"));
    }

    [Fact]
    public async Task SetAsync_ThenTryGet_ReturnsPayload()
    {
        var cache = CreateCache();

        await cache.SetAsync("feed", """{ "hello": "world" }""", TimeSpan.FromMinutes(10));

        Assert.Equal("""{ "hello": "world" }""", await cache.TryGetAsync("feed"));
    }

    [Fact]
    public async Task TryGetAsync_AfterExpiry_ReturnsNull()
    {
        var cache = CreateCache();
        await cache.SetAsync("short", "payload", TimeSpan.FromMilliseconds(50));

        await Task.Delay(300);

        Assert.Null(await cache.TryGetAsync("short"));
    }

    [Fact]
    public async Task Handler_ServesRepeatedRequestsFromCache()
    {
        var cache = CreateCache();
        var inner = FakeHttpMessageHandler.Json("""{ "value": 42 }""");
        using var client = CreateClient(cache, inner);

        var first = await SendAsync(client, "https://example.test/data");
        var second = await SendAsync(client, "https://example.test/data");

        Assert.Equal("""{ "value": 42 }""", first);
        Assert.Equal(first, second);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task Handler_WithoutTtlOption_PassesThrough()
    {
        var cache = CreateCache();
        var inner = FakeHttpMessageHandler.Json("""{ "value": 42 }""");
        using var client = CreateClient(cache, inner);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/live");
        using var response = await client.SendAsync(request);

        Assert.Equal(1, inner.RequestCount);
        Assert.NotNull(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Handler_DoesNotCacheNonJsonResponses()
    {
        var cache = CreateCache();
        var inner = FakeHttpMessageHandler.Bytes([1, 2, 3], "image/jpeg");
        using var client = CreateClient(cache, inner);

        await SendAsync(client, "https://example.test/image.jpg");
        await SendAsync(client, "https://example.test/image.jpg");

        Assert.Equal(2, inner.RequestCount);
    }

    private MetadataCache CreateCache() => new(NullLogger<MetadataCache>.Instance, _directory);

    private static HttpClient CreateClient(IMetadataCache cache, HttpMessageHandler inner) =>
        new(new MetadataCacheHandler(cache, NullLogger<MetadataCacheHandler>.Instance, inner));

    private static async Task<string> SendAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(MetadataCacheHandler.CacheTtlKey, TimeSpan.FromMinutes(30));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
