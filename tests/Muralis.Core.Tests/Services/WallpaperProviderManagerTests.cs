using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class WallpaperProviderManagerTests : IDisposable
{
    private readonly string _directory;
    private readonly SettingsService _settings;
    private readonly FakeLocalLibrary _library = new();
    private readonly FakeProviderConfiguration _configuration = new();
    private readonly FakeWallpaperProvider _bing = new("bing", "Bing");
    private readonly FakeWallpaperProvider _wallhaven = new("wallhaven", "Wallhaven");
    private readonly FakeWallpaperProvider _nasa = new("nasa", "NASA") { RequiresApiKey = true };
    private readonly FakeWallpaperProvider _mock = new("mock", "Samples");

    public WallpaperProviderManagerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-manager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(_directory, "settings.json"));
        _settings.LoadAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void AllProviders_KeepRegistrationOrder()
    {
        var manager = CreateManager();

        Assert.Equal(["bing", "wallhaven", "nasa", "mock"], manager.Providers.Select(provider => provider.Id));
    }

    [Fact]
    public void Availability_ReflectsSettingsAndKeys()
    {
        var manager = CreateManager();

        Assert.Equal(ProviderAvailability.Available, manager.GetAvailability(_bing));
        Assert.Equal(ProviderAvailability.NeedsApiKey, manager.GetAvailability(_nasa));

        _configuration.WithKey("nasa", "key");
        Assert.Equal(ProviderAvailability.Available, manager.GetAvailability(_nasa));

        manager.SetEnabled("nasa", false);
        Assert.Equal(ProviderAvailability.Disabled, manager.GetAvailability(_nasa));
    }

    [Fact]
    public void EnabledProviders_ExcludeDisabledAndUnconfigured()
    {
        var manager = CreateManager();

        Assert.Equal(["bing", "wallhaven", "mock"], manager.EnabledProviders.Select(provider => provider.Id));

        manager.SetEnabled("wallhaven", false);
        Assert.Equal(["bing", "mock"], manager.EnabledProviders.Select(provider => provider.Id));
    }

    [Fact]
    public async Task SetEnabled_PersistsThroughSettings()
    {
        var manager = CreateManager();
        manager.SetEnabled("wallhaven", false);

        await _settings.SaveAsync();
        var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(_directory, "settings.json"));
        await reloaded.LoadAsync();

        Assert.Contains("wallhaven", reloaded.Current.Providers.DisabledProviders);
    }

    [Fact]
    public void SetEnabled_RefusesToDisableTheLastSource()
    {
        var manager = CreateManager();
        manager.SetEnabled("wallhaven", false);
        manager.SetEnabled("mock", false);
        manager.SetEnabled("nasa", false);

        // Only bing is left, and it must stay on.
        Assert.False(manager.SetEnabled("bing", false));
        Assert.True(manager.IsEnabled("bing"));
    }

    [Fact]
    public void DefaultProvider_FollowsSettingsAndFallsBack()
    {
        var manager = CreateManager();

        Assert.Equal("bing", manager.DefaultProvider?.Id);

        manager.SetDefaultProvider("wallhaven");
        Assert.Equal("wallhaven", manager.DefaultProvider?.Id);

        manager.SetEnabled("wallhaven", false);
        Assert.Equal("bing", manager.DefaultProvider?.Id);
    }

    [Fact]
    public async Task SearchAsync_AllSources_InterleavesResults()
    {
        var manager = CreateManager();
        _bing.OnSearch = _ => [Wallpaper("bing:1"), Wallpaper("bing:2"), Wallpaper("bing:3")];
        _wallhaven.OnSearch = _ => [Wallpaper("wallhaven:1"), Wallpaper("wallhaven:2")];

        var result = await manager.SearchAsync(new WallpaperQuery(), providerId: null, CancellationToken.None);

        Assert.Equal(
            ["bing:1", "wallhaven:1", "bing:2", "wallhaven:2", "bing:3"],
            result.Items.Select(item => item.Id));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task SearchAsync_OneFailingSource_StillReturnsTheOthers()
    {
        var manager = CreateManager();
        _bing.OnSearch = _ => [Wallpaper("bing:1")];
        _wallhaven.SearchException = new HttpRequestException("blocked");
        _mock.OnSearch = _ => [Wallpaper("mock:1")];

        var result = await manager.SearchAsync(new WallpaperQuery(), providerId: null, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("wallhaven", failure.Provider.Id);
        Assert.Contains("blocked", failure.Message);
    }

    [Fact]
    public async Task SearchAsync_SpecificSource_QueriesOnlyThatOne()
    {
        var manager = CreateManager();
        _bing.OnSearch = _ => [Wallpaper("bing:1")];
        _wallhaven.OnSearch = _ => [Wallpaper("wallhaven:1")];

        var result = await manager.SearchAsync(new WallpaperQuery(), "wallhaven", CancellationToken.None);

        Assert.Equal(["wallhaven:1"], result.Items.Select(item => item.Id));
        Assert.Equal(0, _bing.SearchCount);
        Assert.Equal(1, _wallhaven.SearchCount);
    }

    [Fact]
    public async Task SearchAsync_WithUnavailableSource_ReportsAFailure()
    {
        var manager = CreateManager();

        var result = await manager.SearchAsync(new WallpaperQuery(), "nasa", CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Single(result.Failures);
        Assert.Equal(0, _nasa.SearchCount);
    }

    [Fact]
    public async Task SearchAsync_OverlaysCatalogState()
    {
        var manager = CreateManager();
        _bing.OnSearch = _ => [Wallpaper("bing:1")];
        _library.Add(new Wallpaper
        {
            Id = "bing:1",
            Title = "Known",
            IsFavorite = true,
            LocalPath = @"C:\wallpapers\known.jpg",
            Width = 3840,
            Height = 2160,
        });

        var result = await manager.SearchAsync(new WallpaperQuery(), "bing", CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.True(item.IsFavorite);
        Assert.Equal(@"C:\wallpapers\known.jpg", item.LocalPath);
        Assert.Equal(3840, item.Width);
    }

    [Fact]
    public async Task GetFeaturedAsync_UsesTheDefaultSource()
    {
        var manager = CreateManager();
        _bing.OnFeatured = count => [Wallpaper("bing:1")];
        _wallhaven.OnFeatured = count => [Wallpaper("wallhaven:1")];
        manager.SetDefaultProvider("wallhaven");

        var result = await manager.GetFeaturedAsync(10, CancellationToken.None);

        Assert.Equal("wallhaven:1", Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task GetFeaturedAsync_FallsBackToSamples()
    {
        var manager = CreateManager();
        _bing.SearchException = new HttpRequestException("offline");
        _mock.OnFeatured = count => [Wallpaper("mock:1")];

        var result = await manager.GetFeaturedAsync(10, CancellationToken.None);

        Assert.Equal("mock:1", Assert.Single(result.Items).Id);
        Assert.Single(result.Failures);
        Assert.Equal("bing", result.Failures[0].Provider.Id);
    }

    [Fact]
    public async Task GetDownloadUrlAsync_AsksTheOwningProvider()
    {
        var manager = CreateManager();
        _wallhaven.DownloadUrlOverride = "https://wallhaven.test/final.jpg";

        var url = await manager.GetDownloadUrlAsync(Wallpaper("wallhaven:1"), CancellationToken.None);

        Assert.Equal("https://wallhaven.test/final.jpg", url);
    }

    private WallpaperProviderManager CreateManager() =>
        new(
            [_bing, _wallhaven, _nasa, _mock],
            _settings,
            _configuration,
            _library,
            NullLogger<WallpaperProviderManager>.Instance);

    private static Wallpaper Wallpaper(string id) => new()
    {
        Id = id,
        Title = id,
        RemoteUrl = "https://example.test/" + id,
        Source = WallpaperSource.Online,
    };
}
