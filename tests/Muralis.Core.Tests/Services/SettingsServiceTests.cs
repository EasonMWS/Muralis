using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _settingsPath;

    public SettingsServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "muralis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _settingsPath = Path.Combine(_tempDirectory, "settings.json");
    }

    [Fact]
    public async Task LoadAsync_WithNoFile_UsesDefaults()
    {
        var service = CreateService();

        await service.LoadAsync();

        Assert.Equal(AppTheme.System, service.Current.Theme);
        Assert.Equal(WallpaperFitMode.Fill, service.Current.DefaultFitMode);
        Assert.False(service.Current.Rotation.Enabled);
        Assert.False(service.Current.VideoWallpaper.Enabled);
        Assert.Equal(string.Empty, service.Current.VideoWallpaper.VideoPath);
        Assert.True(service.Current.VideoWallpaper.Muted);
        Assert.False(service.Current.DesktopCanvas.Enabled);
    }

    [Fact]
    public async Task Update_ThenLoad_RoundTripsValues()
    {
        var service = CreateService();
        await service.LoadAsync();

        service.Update(settings =>
        {
            settings.Theme = AppTheme.Dark;
            settings.DefaultFitMode = WallpaperFitMode.Span;
            settings.LaunchAtStartup = true;
            settings.Rotation.Enabled = true;
            settings.Rotation.Interval = RotationInterval.Hours6;
            settings.VideoWallpaper.Enabled = true;
            settings.VideoWallpaper.VideoPath = @"C:\videos\aurora.mp4";
            settings.VideoWallpaper.Muted = false;
            settings.DesktopCanvas.Enabled = true;
        });

        // Update saves in the background; give the detached save a moment to complete.
        await WaitForFileAsync(_settingsPath);

        var reloaded = CreateService();
        await reloaded.LoadAsync();

        Assert.Equal(AppTheme.Dark, reloaded.Current.Theme);
        Assert.Equal(WallpaperFitMode.Span, reloaded.Current.DefaultFitMode);
        Assert.True(reloaded.Current.LaunchAtStartup);
        Assert.True(reloaded.Current.Rotation.Enabled);
        Assert.Equal(RotationInterval.Hours6, reloaded.Current.Rotation.Interval);
        Assert.True(reloaded.Current.VideoWallpaper.Enabled);
        Assert.Equal(@"C:\videos\aurora.mp4", reloaded.Current.VideoWallpaper.VideoPath);
        Assert.False(reloaded.Current.VideoWallpaper.Muted);
        Assert.True(reloaded.Current.DesktopCanvas.Enabled);
    }

    [Fact]
    public async Task Update_RaisesSettingsChanged()
    {
        var service = CreateService();
        await service.LoadAsync();

        var raised = 0;
        service.SettingsChanged += (_, _) => raised++;

        service.Update(settings => settings.Theme = AppTheme.Light);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task LoadAsync_WithCorruptFile_FallsBackToDefaultsAndBacksUp()
    {
        await File.WriteAllTextAsync(_settingsPath, "{ this is not valid json");
        var service = CreateService();

        await service.LoadAsync();

        Assert.Equal(AppTheme.System, service.Current.Theme);
        Assert.True(File.Exists(_settingsPath + ".bad"));
    }

    [Fact]
    public async Task SaveAsync_WritesEnumsAsNames()
    {
        var service = CreateService();
        await service.LoadAsync();

        service.Update(settings => settings.Theme = AppTheme.Dark);
        await service.SaveAsync();

        var json = await File.ReadAllTextAsync(_settingsPath);
        Assert.Contains("\"Dark\"", json);
    }

    private SettingsService CreateService() => new(NullLogger<SettingsService>.Instance, _settingsPath);

    private static async Task WaitForFileAsync(string path, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup; the OS will reclaim the temp folder eventually.
        }
    }
}
