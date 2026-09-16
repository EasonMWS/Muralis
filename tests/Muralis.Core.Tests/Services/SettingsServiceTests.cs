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
        });

        // Update saves in the background; wait for the written file to carry what was asked for.
        await WaitForFileAsync(_settingsPath, "\"Dark\"");

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

    // The save that Update starts is detached, so the test waits for the file to say what was written
    // rather than merely to exist. Under a loaded machine the two are not the same moment, and reading
    // the file the instant it appears is what made this test flaky.
    private static async Task WaitForFileAsync(string path, string expected, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path)
                    && (await File.ReadAllTextAsync(path)).Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException)
            {
                // The finished file is not in place yet; the next pass reads it.
            }

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
