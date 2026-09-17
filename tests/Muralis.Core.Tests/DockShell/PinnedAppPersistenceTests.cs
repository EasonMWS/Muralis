using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.DockShell;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.DockShell;

/// <summary>
/// The pinned list as it lives on disk. The dock is only worth using if the applications the user put
/// in it come back the same way next time, and only safe if a section that cannot be read costs the
/// rest of the dock nothing.
/// </summary>
public sealed class PinnedAppPersistenceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("pinned-persistence");
    private readonly string _settingsPath;

    public PinnedAppPersistenceTests()
    {
        _settingsPath = _workspace.PathOf("settings.json");
    }

    [Fact]
    public async Task SavedPins_ComeBackInTheSameOrderWithTheSameValues()
    {
        var service = CreateService();
        await service.LoadAsync();
        service.Update(settings => settings.Dock.PinnedApps =
        [
            PinnedAppSettings.From(Pin("Editor", @"C:\tools\editor.exe", PinnedAppKind.Application, "pin-editor")),
            PinnedAppSettings.From(Pin(
                "Music",
                @"C:\Users\e\Desktop\Music.lnk",
                PinnedAppKind.Shortcut,
                "pin-music",
                arguments: "--quiet",
                workingDirectory: @"C:\Music")),
        ]);
        await service.SaveAsync();

        var reloaded = CreateService();
        await reloaded.LoadAsync();

        var saved = reloaded.Current.Dock.PinnedApps
            .Select(entry => entry.TryToPinnedApp(out var app) ? app : null)
            .OfType<PinnedApp>()
            .ToArray();

        Assert.Equal(["pin-editor", "pin-music"], saved.Select(app => app.Id));
        Assert.Equal(PinnedAppKind.Shortcut, saved[1].Kind);
        Assert.Equal(@"C:\Users\e\Desktop\Music.lnk", saved[1].LaunchTarget);
        Assert.Equal("--quiet", saved[1].Arguments);
        Assert.Equal(@"C:\Music", saved[1].WorkingDirectory);
    }

    [Fact]
    public async Task SettingsFile_WritesTheDockSectionInReadableText()
    {
        var service = CreateService();
        await service.LoadAsync();
        service.Update(settings => settings.Dock.PinnedApps =
            [PinnedAppSettings.From(Pin("Music", @"C:\Users\e\Desktop\Music.lnk", PinnedAppKind.Shortcut, "pin-music"))]);
        await service.SaveAsync();

        var json = await File.ReadAllTextAsync(_settingsPath);

        // The section is the file's own, and a kind the user could correct by hand reads as a name
        // rather than a number.
        Assert.Contains("\"Dock\"", json, StringComparison.Ordinal);
        Assert.Contains("\"PinnedApps\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Shortcut\"", json, StringComparison.Ordinal);
        Assert.Contains("\"LaunchTarget\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASettingsFileWithNoDockSection_StillLeavesTheDockUsable()
    {
        // Every settings file written before the dock existed looks like this. Reading one must leave
        // an empty zone that works, not a dock that cannot be drawn.
        await File.WriteAllTextAsync(_settingsPath, "{\n  \"SchemaVersion\": 2,\n  \"Theme\": \"Dark\"\n}");

        var service = CreateService();
        await service.LoadAsync();

        Assert.NotNull(service.Current.Dock);
        Assert.Empty(service.Current.Dock.PinnedApps);
        Assert.True(service.Current.Dock.IsVisible);
    }

    [Fact]
    public async Task ASettingsFileWithAnEmptyDockSection_StillLeavesTheDockUsable()
    {
        await File.WriteAllTextAsync(_settingsPath, "{\n  \"SchemaVersion\": 2,\n  \"Dock\": null\n}");

        var service = CreateService();
        await service.LoadAsync();

        Assert.NotNull(service.Current.Dock);
        Assert.Empty(service.Current.Dock.PinnedApps);
        Assert.True(service.Current.Dock.IsVisible);
    }

    [Theory]
    [InlineData("", "Editor", @"C:\tools\editor.exe", PinnedAppKind.Application)]
    [InlineData("pin-editor", "", @"C:\tools\editor.exe", PinnedAppKind.Application)]
    [InlineData("pin-editor", "Editor", "", PinnedAppKind.Application)]
    [InlineData("pin-editor", "Editor", @"C:\tools\editor.exe", (PinnedAppKind)99)]
    public void AnEntryThatCannotDescribeAnApplication_IsRefusedRatherThanRepaired(
        string id,
        string displayName,
        string launchTarget,
        PinnedAppKind kind)
    {
        var entry = new PinnedAppSettings
        {
            Id = id,
            DisplayName = displayName,
            LaunchTarget = launchTarget,
            Kind = kind,
        };

        Assert.False(entry.TryToPinnedApp(out var app));
        Assert.Null(app);
    }

    [Fact]
    public void AnIncompleteEntry_IsCompletedFromWhatItDoesSay()
    {
        // What the user pinned is the file; the identity and the icon can both be worked out from it,
        // so a hand-edited entry that leaves them out keeps working.
        var entry = new PinnedAppSettings
        {
            Id = "pin-editor",
            DisplayName = "Editor",
            LaunchTarget = @"C:\tools\editor.exe",
            Kind = PinnedAppKind.Application,
        };

        Assert.True(entry.TryToPinnedApp(out var app));
        Assert.NotNull(app);
        Assert.Equal(PinnedAppIdentity.Of(@"C:\tools\editor.exe", null), app!.Identity);
        Assert.Equal(@"C:\tools\editor.exe", app.IconIdentity);
        Assert.Null(app.Arguments);
        Assert.Null(app.WorkingDirectory);
    }

    [Fact]
    public void AnEntryWithAResolvedIdentity_KeepsTheIdentityItWasSavedWith()
    {
        // The identity is stored rather than recomputed so a shortcut whose target moves cannot quietly
        // become a second pin; a saved identity therefore wins over what the path currently says.
        var entry = new PinnedAppSettings
        {
            Id = "pin-music",
            DisplayName = "Music",
            LaunchTarget = @"C:\Users\e\Desktop\Music.lnk",
            Kind = PinnedAppKind.Shortcut,
            Identity = PinnedAppIdentity.Of(@"C:\Users\e\Desktop\Music.lnk", @"C:\Program Files\Music\music.exe"),
        };

        Assert.True(entry.TryToPinnedApp(out var app));
        Assert.Equal(PinnedAppIdentity.Normalize(@"C:\Program Files\Music\music.exe"), app!.Identity);
    }

    private SettingsService CreateService() => new(NullLogger<SettingsService>.Instance, _settingsPath);

    private static PinnedApp Pin(
        string name,
        string path,
        PinnedAppKind kind,
        string id,
        string? arguments = null,
        string? workingDirectory = null) =>
        new(id, name, path, path, kind, PinnedAppIdentity.Normalize(path), arguments, workingDirectory);

    public void Dispose() => _workspace.Dispose();
}
