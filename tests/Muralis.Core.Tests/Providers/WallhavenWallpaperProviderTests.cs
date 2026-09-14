using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Providers;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Providers;

public sealed class WallhavenWallpaperProviderTests
{
    private const string SearchJson = """
        {
          "data": [
            {
              "id": "xk9w21",
              "url": "https://wallhaven.cc/w/xk9w21",
              "purity": "sfw",
              "category": "general",
              "dimension_x": 3840,
              "dimension_y": 2160,
              "resolution": "3840x2160",
              "file_size": 2456789,
              "file_type": "image/jpeg",
              "created_at": "2026-09-01 12:34:56",
              "path": "https://w.wallhaven.cc/full/xk/wallhaven-xk9w21.jpg",
              "thumbs": {
                "large": "https://th.wallhaven.cc/lg/xk/xk9w21.jpg",
                "original": "https://th.wallhaven.cc/orig/xk/xk9w21.jpg",
                "small": "https://th.wallhaven.cc/small/xk/xk9w21.jpg"
              }
            },
            {
              "id": "abc123",
              "category": "anime",
              "dimension_x": 1920,
              "dimension_y": 1080,
              "file_size": 800000,
              "created_at": "2026-08-20 08:00:00",
              "path": "https://w.wallhaven.cc/full/ab/wallhaven-abc123.png",
              "thumbs": { "large": "https://th.wallhaven.cc/lg/ab/abc123.jpg" }
            }
          ],
          "meta": { "current_page": 1, "last_page": 42, "per_page": 24, "total": 1000 }
        }
        """;

    private const string DetailJson = """
        {
          "data": {
            "id": "xk9w21",
            "category": "general",
            "dimension_x": 3840,
            "dimension_y": 2160,
            "file_size": 2456789,
            "created_at": "2026-09-01 12:34:56",
            "path": "https://w.wallhaven.cc/full/xk/wallhaven-xk9w21.jpg",
            "thumbs": { "large": "https://th.wallhaven.cc/lg/xk/xk9w21.jpg" }
          }
        }
        """;

    [Fact]
    public async Task GetWallpapersAsync_MapsResults()
    {
        var provider = CreateProvider(SearchJson);

        var items = await provider.GetWallpapersAsync(new WallpaperQuery { SearchText = "mountains", PageSize = 24 });

        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("wallhaven:xk9w21", first.Id);
        Assert.Equal("Wallhaven xk9w21", first.Title);
        Assert.Equal("https://w.wallhaven.cc/full/xk/wallhaven-xk9w21.jpg", first.RemoteUrl);
        Assert.Equal("https://th.wallhaven.cc/lg/xk/xk9w21.jpg", first.ThumbnailUrl);
        Assert.Equal(3840, first.Width);
        Assert.Equal(2160, first.Height);
        Assert.Equal(2456789, first.FileSize);
        Assert.Equal(WallpaperSource.Online, first.Source);
        Assert.Contains("Wallhaven", first.Tags);
        Assert.Contains("General", first.Tags);
    }

    [Fact]
    public async Task GetWallpapersAsync_RequestsSafeContentOnly()
    {
        var handler = CreateHandler(SearchJson);
        var provider = CreateProvider(handler);

        await provider.GetWallpapersAsync(new WallpaperQuery { SearchText = "mountains" });

        var url = handler.RequestedUrls.Single();
        Assert.Contains("purity=100", url);
        Assert.Contains("categories=111", url);
        Assert.Contains("sorting=relevance", url);
        Assert.Contains("q=mountains", url);
        Assert.DoesNotContain("apikey=", url);
    }

    [Fact]
    public async Task GetWallpapersAsync_WithoutSearchTerm_UsesNewestFirst()
    {
        var handler = CreateHandler(SearchJson);
        var provider = CreateProvider(handler);

        await provider.GetWallpapersAsync(new WallpaperQuery());

        Assert.Contains("sorting=date_added", handler.RequestedUrls.Single());
    }

    [Fact]
    public async Task GetFeaturedAsync_UsesToplist()
    {
        var handler = CreateHandler(SearchJson);
        var provider = CreateProvider(handler);

        var items = await provider.GetFeaturedAsync(1);

        var url = handler.RequestedUrls.Single();
        Assert.Contains("sorting=toplist", url);
        Assert.Contains("topRange=1M", url);
        Assert.Single(items);
    }

    [Fact]
    public async Task GetWallpapersAsync_WithConfiguredKey_SendsTheKey()
    {
        var handler = CreateHandler(SearchJson);
        var provider = CreateProvider(handler, new FakeProviderConfiguration().WithKey("wallhaven", "secret-key"));

        await provider.GetWallpapersAsync(new WallpaperQuery { SearchText = "space" });

        Assert.Contains("apikey=secret-key", handler.RequestedUrls.Single());
    }

    [Fact]
    public async Task GetWallpaperAsync_MapsDetails_AndAcceptsTheProviderPrefix()
    {
        var provider = CreateProvider(DetailJson);

        var wallpaper = await provider.GetWallpaperAsync("wallhaven:xk9w21");

        Assert.NotNull(wallpaper);
        Assert.Equal("wallhaven:xk9w21", wallpaper!.Id);
        Assert.Equal(3840, wallpaper.Width);
    }

    [Fact]
    public async Task GetWallpaperAsync_WithUnusableId_ReturnsNull()
    {
        var provider = CreateProvider(DetailJson);

        Assert.Null(await provider.GetWallpaperAsync("wallhaven:"));
    }

    private static FakeHttpMessageHandler CreateHandler(string json) => FakeHttpMessageHandler.Json(json);

    private static WallhavenWallpaperProvider CreateProvider(string json) =>
        CreateProvider(CreateHandler(json));

    private static WallhavenWallpaperProvider CreateProvider(
        FakeHttpMessageHandler handler,
        IProviderConfiguration? configuration = null)
    {
        var client = new HttpClient(handler);
        return new WallhavenWallpaperProvider(
            client,
            configuration ?? new FakeProviderConfiguration(),
            NullLogger<WallhavenWallpaperProvider>.Instance);
    }
}
