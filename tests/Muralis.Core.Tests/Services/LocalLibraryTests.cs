using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Repositories;
using Muralis.Core.Services;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class LocalLibraryTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly LocalLibrary _library;

    public LocalLibraryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-library-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "test.db");
        _library = CreateLibrary(_databasePath);
    }

    [Fact]
    public async Task Import_AddsSupportedImagesWithMetadata()
    {
        await _library.InitializeAsync();
        var path = CreateImage("wide shot-1.png", 1920, 1080);

        var result = await _library.ImportAsync([path]);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Failed);
        var item = Assert.Single(_library.Items);
        Assert.Equal("Wide Shot 1", item.Title);
        Assert.Equal(1920, item.Width);
        Assert.Equal(1080, item.Height);
        Assert.True(item.FileSize > 0);
    }

    [Fact]
    public async Task Import_SkipsDuplicates()
    {
        await _library.InitializeAsync();
        var path = CreateImage("one.png");

        await _library.ImportAsync([path]);
        var second = await _library.ImportAsync([path]);

        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Duplicates);
        Assert.Single(_library.Items);
    }

    [Fact]
    public async Task Import_CountsUnsupportedFilesAsFailed()
    {
        await _library.InitializeAsync();
        var textFile = Path.Combine(_directory, "notes.txt");
        await File.WriteAllTextAsync(textFile, "not an image");

        var result = await _library.ImportAsync([textFile, Path.Combine(_directory, "missing.png")]);

        Assert.Equal(0, result.Added);
        Assert.Equal(2, result.Failed);
        Assert.Empty(_library.Items);
    }

    [Fact]
    public async Task Remove_DropsRecordButKeepsFileOnDisk()
    {
        await _library.InitializeAsync();
        var path = CreateImage("keep-me.png");
        await _library.ImportAsync([path]);
        var id = _library.Items[0].Id;

        var removed = await _library.RemoveAsync(id);

        Assert.True(removed);
        Assert.Empty(_library.Items);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RecordUsage_UpdatesLastUsedAndRecentlyUsed()
    {
        await _library.InitializeAsync();
        var path = CreateImage("used.png");
        await _library.ImportAsync([path]);
        var item = _library.Items[0];

        await _library.RecordUsageAsync(item, "Display 1");

        Assert.NotNull(item.LastUsedAt);
        var recent = await _library.GetRecentlyUsedAsync(5);
        var used = Assert.Single(recent);
        Assert.Equal(item.Id, used.Id);
    }

    [Fact]
    public async Task Favorites_PersistAcrossInstances()
    {
        await _library.InitializeAsync();
        var path = CreateImage("starred.png");
        await _library.ImportAsync([path]);
        await _library.SetFavoriteAsync(_library.Items[0], true);

        // A fresh library over the same database must still see the favorite.
        var reopened = CreateLibrary(_databasePath);
        await reopened.InitializeAsync();

        var favorite = Assert.Single(reopened.Favorites);
        Assert.Equal(_library.Items[0].Id, favorite.Id);
    }

    [Fact]
    public async Task FavoritingAnExternalWallpaper_AddsItToTheCatalog()
    {
        await _library.InitializeAsync();
        var external = new Muralis.Core.Models.Wallpaper
        {
            Id = "bing:2026-09-14",
            Title = "Bing daily image",
            RemoteUrl = "https://example.com/image.jpg",
            Source = Muralis.Core.Models.WallpaperSource.Online,
        };

        await _library.SetFavoriteAsync(external, true);

        var favorite = Assert.Single(_library.Favorites);
        Assert.Equal("bing:2026-09-14", favorite.Id);

        // Online entries never show up in the local library list.
        Assert.Empty(_library.Items);
    }

    [Fact]
    public async Task Import_RaisesChangedOnlyWhenSomethingWasAdded()
    {
        await _library.InitializeAsync();
        var path = CreateImage("event.png");
        var raised = 0;
        _library.Changed += (_, _) => raised++;

        await _library.ImportAsync([path]);
        await _library.ImportAsync([path]);

        Assert.Equal(1, raised);
    }

    private static LocalLibrary CreateLibrary(string databasePath)
    {
        var repository = new SqliteWallpaperRepository(NullLogger<SqliteWallpaperRepository>.Instance, databasePath);
        return new LocalLibrary(repository, NullLogger<LocalLibrary>.Instance);
    }

    private string CreateImage(string name, int width = 320, int height = 200)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, CreatePngHeader(width, height));
        return path;
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        var bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        bytes[11] = 0x0D; // IHDR length 13
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        return bytes;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }
}
