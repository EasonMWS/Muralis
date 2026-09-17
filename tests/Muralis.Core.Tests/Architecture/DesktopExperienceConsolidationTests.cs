using Xunit;

namespace Muralis.Core.Tests.Architecture;

/// <summary>
/// Locks the two-mode product model in place. The product offers the native Windows desktop and Muralis
/// Mode and nothing else, so the surfaces that used to offer a third and fourth relationship — the
/// wallpaper page's desktop picker and edge dock, the tray's takeover entry, the settings page's separate
/// Dock section — must not come back, whatever the frozen layer underneath still does.
/// </summary>
public sealed class DesktopExperienceConsolidationTests
{
    [Fact]
    public void TheWallpaperPageIsAboutTheWallpaperNotTheDesktop()
    {
        var page = Source("src/Muralis.App/Views/DynamicWallpaperPage.xaml");
        var viewModel = Source("src/Muralis.App/ViewModels/DynamicWallpaperViewModel.cs");

        foreach (var token in new[]
                 {
                     "DesktopMode", "CanvasDock", "Dynamic_Section_Dock", "Dynamic_Section_Desktop",
                     "Dynamic_Desktop_Mode", "Dynamic_Desktop_State", "Dynamic_Dock",
                 })
        {
            Assert.DoesNotContain(token, page, StringComparison.Ordinal);
            Assert.DoesNotContain(token, viewModel, StringComparison.Ordinal);
        }

        // The desktop items card stays: the Shelf reads the very same document, and this card is where
        // the user edits it.
        Assert.Contains("Dynamic_Section_Canvas_Items", page, StringComparison.Ordinal);
        Assert.Contains("CanvasItems", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrayHasOneWayBackToTheWindowsDesktop()
    {
        // While Muralis owns the desktop the tray is the one piece of it the user can always reach, so
        // the offer is a single restore — never a way to turn the takeover on from a menu.
        var tray = Source("src/Muralis.App/Services/TrayService.cs");

        Assert.Contains("MenuRestoreDesktop", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("MenuTurnOffDesktop", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopMode.Preview", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopMode.Takeover", tray, StringComparison.Ordinal);
        Assert.Contains("DesktopExperienceMode.Native", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDockSettingsLiveUnderMuralisMode()
    {
        var page = Source("src/Muralis.App/Views/SettingsPage.xaml");

        // One section heading for the mode the dock belongs to, and no standalone Dock section left.
        Assert.Contains("Settings_Section_MuralisMode", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings_Section_Dock", page, StringComparison.Ordinal);

        // The dock lives on the native desktop too, so its switch is not taken away there — the mode
        // that requires it is what withholds the switch, and the page says so.
        var viewModel = Source("src/Muralis.App/ViewModels/SettingsViewModel.cs");
        Assert.Contains("CanChangeDockVisibility", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsMuralisMode", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRetiredWordingIsGoneFromTheStrings()
    {
        var english = Source("src/Muralis.App/Strings/Resources.resx");
        var chinese = Source("src/Muralis.App/Strings/Resources.zh-CN.resx");

        foreach (var key in new[]
                 {
                     "Settings_DesktopExperience_Clean", "Settings_DesktopExperience_Full",
                     "Settings_Section_Dock", "Tray_TurnOffDesktop", "Dynamic_Section_Dock",
                     "Dynamic_Section_Desktop", "Dynamic_Desktop_Mode_Takeover", "Dynamic_Dock_Enabled",
                 })
        {
            Assert.DoesNotContain(key, english, StringComparison.Ordinal);
            Assert.DoesNotContain(key, chinese, StringComparison.Ordinal);
        }

        // And the two modes the product offers are both named, in both languages.
        foreach (var key in new[]
                 {
                     "Settings_DesktopExperience_Native", "Settings_DesktopExperience_Muralis",
                     "Settings_Section_MuralisMode", "Settings_Dock_Required", "Tray_RestoreDesktop",
                 })
        {
            Assert.Contains(key, english, StringComparison.Ordinal);
            Assert.Contains(key, chinese, StringComparison.Ordinal);
        }
    }

    private static string Source(string relativePath) => File.ReadAllText(Absolute(relativePath));

    private static string Absolute(string relativePath) =>
        Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Muralis.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Muralis repository root.");
    }
}
