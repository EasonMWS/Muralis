using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Repositories;
using Xunit;

namespace Muralis.Core.Tests.Repositories;

public sealed class SqliteWallpaperRepositoryTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteWallpaperRepositoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-repo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "catalog.db");
    }

    [Fact]
    public async Task Upsert_ThenGetAll_RoundTripsEveryField()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-3);

        await repository.UpsertAsync(new Wallpaper
        {
            Id = "local:abc",
            Title = "Blue Ridge 4k",
            LocalPath = @"C:\pics\blue.jpg",
            Width = 3840,
            Height = 2400,
            FileSize = 1_382_400,
            Tags = ["Nature", "Blue"],
            IsFavorite = true,
            Source = WallpaperSource.Local,
            CreatedAt = createdAt,
        });

        var loaded = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("local:abc", loaded.Id);
        Assert.Equal("Blue Ridge 4k", loaded.Title);
        Assert.Equal(@"C:\pics\blue.jpg", loaded.LocalPath);
        Assert.Equal(3840, loaded.Width);
        Assert.Equal(2400, loaded.Height);
        Assert.Equal(1_382_400, loaded.FileSize);
        Assert.Equal(["Nature", "Blue"], loaded.Tags);
        Assert.True(loaded.IsFavorite);
        Assert.Equal(WallpaperSource.Local, loaded.Source);
        Assert.Equal(createdAt, loaded.CreatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Upsert_UpdatesExistingRow()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var wallpaper = new Wallpaper { Id = "local:update", Title = "Before" };
        await repository.UpsertAsync(wallpaper);

        wallpaper.Title = "After";
        wallpaper.IsFavorite = true;
        await repository.UpsertAsync(wallpaper);

        var loaded = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("After", loaded.Title);
        Assert.True(loaded.IsFavorite);
    }

    [Fact]
    public async Task Delete_ReportsWhetherARowWasRemoved()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.UpsertAsync(new Wallpaper { Id = "local:gone" });

        Assert.True(await repository.DeleteAsync("local:gone"));
        Assert.False(await repository.DeleteAsync("local:gone"));
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task GetRecentlyUsed_ReturnsDistinctWallpapersNewestFirst()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.UpsertAsync(new Wallpaper { Id = "a", Title = "A" });
        await repository.UpsertAsync(new Wallpaper { Id = "b", Title = "B" });

        var now = DateTimeOffset.UtcNow;
        await repository.AddHistoryAsync(new HistoryEntry { WallpaperId = "a", AppliedAt = now.AddHours(-2) });
        await repository.AddHistoryAsync(new HistoryEntry { WallpaperId = "b", AppliedAt = now.AddHours(-1) });
        await repository.AddHistoryAsync(new HistoryEntry { WallpaperId = "a", AppliedAt = now });

        var recent = await repository.GetRecentlyUsedAsync(10);

        Assert.Equal(2, recent.Count);
        Assert.Equal("a", recent[0].Id); // most recently used first
        Assert.Equal("b", recent[1].Id);
    }

    [Fact]
    public async Task Data_SurvivesNewRepositoryInstance()
    {
        var first = CreateRepository();
        await first.InitializeAsync();
        await first.UpsertAsync(new Wallpaper { Id = "local:persisted", Title = "Persisted" });

        var second = CreateRepository();
        await second.InitializeAsync();

        var loaded = Assert.Single(await second.GetAllAsync());
        Assert.Equal("Persisted", loaded.Title);
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        var repository = CreateRepository();

        await repository.InitializeAsync();
        await repository.InitializeAsync();

        Assert.Empty(await repository.GetAllAsync());
    }

    private SqliteWallpaperRepository CreateRepository() =>
        new(NullLogger<SqliteWallpaperRepository>.Instance, _databasePath);

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
