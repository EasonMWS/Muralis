using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Xunit;

namespace Muralis.Core.Tests.Models;

/// <summary>
/// Guards the migration of the product mode: a file written when four relationships existed still
/// opens, and it opens as the mode that really is in place rather than as a fourth one that no longer
/// has an implementation behind it.
/// </summary>
public sealed class DesktopExperienceModeTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _settingsPath;

    public DesktopExperienceModeTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "muralis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _settingsPath = Path.Combine(_tempDirectory, "settings.json");
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

    [Fact]
    public async Task LoadsTheRetiredCleanDesktopModeAsMuralis()
    {
        await WriteSettingsAsync("CleanDesktop");

        var service = CreateService();
        await service.LoadAsync();

        // Not Native: the user's icons are hidden, and downgrading them to a mode they never chose would
        // be the one outcome this migration must not produce.
        Assert.Equal(DesktopExperienceMode.Muralis, service.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task LoadsTheRetiredTakeoverModeAsNative()
    {
        await WriteSettingsAsync("FullTakeoverExperimental");

        var service = CreateService();
        await service.LoadAsync();

        // The withdrawn takeover is not re-entered by reading a file that asked for it.
        Assert.Equal(DesktopExperienceMode.Native, service.Current.DesktopExperience.Mode);
    }

    [Theory]
    [InlineData("\"SomethingElse\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task LoadsAnyOtherValueAsNative(string modeJson)
    {
        await File.WriteAllTextAsync(
            _settingsPath,
            $$"""
              {
                "SchemaVersion": {{AppSettings.CurrentSchemaVersion}},
                "DesktopExperience": { "Mode": {{modeJson}} }
              }
              """);

        var service = CreateService();
        await service.LoadAsync();

        Assert.Equal(DesktopExperienceMode.Native, service.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task WritesTheModeByItsCurrentNameOnly()
    {
        var service = CreateService();
        await service.LoadAsync();

        service.Update(settings => settings.DesktopExperience.Mode = DesktopExperienceMode.Muralis);
        await service.SaveAsync();

        var json = await File.ReadAllTextAsync(_settingsPath);
        Assert.Contains("\"Muralis\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanDesktop", json, StringComparison.Ordinal);
        Assert.DoesNotContain("FullTakeoverExperimental", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"Muralis\"", DesktopExperienceMode.Muralis)]
    [InlineData("\"Native\"", DesktopExperienceMode.Native)]
    [InlineData("\"muralis\"", DesktopExperienceMode.Muralis)]
    [InlineData("\"CleanDesktop\"", DesktopExperienceMode.Muralis)]
    [InlineData("1", DesktopExperienceMode.Muralis)]
    [InlineData("0", DesktopExperienceMode.Native)]
    public async Task LoadsEveryShapeTheModeWasEverWrittenIn(string modeJson, DesktopExperienceMode expected)
    {
        await WriteRawModeAsync(modeJson);

        var service = CreateService();
        await service.LoadAsync();

        Assert.Equal(expected, service.Current.DesktopExperience.Mode);
    }

    private Task WriteSettingsAsync(string mode) =>
        File.WriteAllTextAsync(
            _settingsPath,
            $$"""
              {
                "SchemaVersion": 1,
                "DesktopExperience": { "Mode": "{{mode}}" }
              }
              """);

    private Task WriteRawModeAsync(string modeJson) =>
        File.WriteAllTextAsync(
            _settingsPath,
            $$"""
              {
                "SchemaVersion": {{AppSettings.CurrentSchemaVersion}},
                "DesktopExperience": { "Mode": {{modeJson}} }
              }
              """);

    private SettingsService CreateService() => new(NullLogger<SettingsService>.Instance, _settingsPath);
}
