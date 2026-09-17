using Xunit;

namespace Muralis.Core.Tests.Architecture;

/// <summary>
/// Locks the flagship entry point's boundary in place: the home page's Muralis Mode hero speaks to the
/// desktop experience and to nothing underneath it. The retired takeover, the mode service and the Clean
/// Desktop presentation all still exist in the repository, so without this the next change to the hero
/// could quietly reach past the one service that owns the mode's lifecycle and start a second one.
/// </summary>
public sealed class MuralisModeHeroGuardTests
{
    /// <summary>Everything that makes up the hero: its state, the page that shows it, and the code behind.</summary>
    private static readonly string[] HeroSurfaces =
    [
        "src/Muralis.Core/Desktop/MuralisModeHero.cs",
        "src/Muralis.App/ViewModels/HomeViewModel.cs",
        "src/Muralis.App/Views/HomePage.xaml",
        "src/Muralis.App/Views/HomePage.xaml.cs",
    ];

    /// <summary>
    /// The layers that own the desktop below the product model. Hiding Explorer's icons, bringing the dock
    /// up and giving the icons back are their jobs; asking for them from here would be a second lifecycle.
    /// </summary>
    private static readonly string[] FrozenLayerTokens =
    [
        "ICleanDesktopPresentation",
        "CleanDesktopPresentation",
        "IDesktopModeService",
        "DesktopModeService",
        "IDesktopTakeoverService",
        "DesktopTakeoverService",
        "IDesktopCanvasService",
        "DesktopCanvasService",
        "DesktopTakeoverMachine",
        "DesktopLayoutStore",
        "DllImport",
    ];

    [Fact]
    public void TheHeroReachesTheDesktopOnlyThroughTheDesktopExperience()
    {
        foreach (var surface in HeroSurfaces)
        {
            var source = Source(surface);
            foreach (var token in FrozenLayerTokens)
            {
                Assert.True(
                    !source.Contains(token, StringComparison.Ordinal),
                    $"{surface} must not depend on {token}: the mode's lifecycle belongs to IDesktopExperienceService.");
            }
        }

        // And the one service it is allowed to depend on is the one it does depend on.
        Assert.Contains(
            "IDesktopExperienceService",
            Source("src/Muralis.Core/Desktop/MuralisModeHero.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CustomizeIsTheSettingsPage()
    {
        // The experience is entered on the home page and configured in settings. There is no third place,
        // and no section deep-link to keep in step: the settings page already opens on the mode.
        var viewModel = Source("src/Muralis.App/ViewModels/HomeViewModel.cs");

        Assert.Contains("CustomizeMuralisMode", viewModel, StringComparison.Ordinal);
        Assert.Contains("Routes.Settings", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHerosStateIsReadFromTheDesktopRatherThanRemembered()
    {
        // One thing decides what the hero shows: the desktop's own reported state. A mode kept here as a
        // field would be a second copy of it, and the settings page could then disagree with the home page.
        var hero = Source("src/Muralis.Core/Desktop/MuralisModeHero.cs");

        Assert.Contains("IDesktopExperienceService", hero, StringComparison.Ordinal);
        Assert.Contains("_desktop.Status", hero, StringComparison.Ordinal);
        Assert.Contains("status.IsMuralis", hero, StringComparison.Ordinal);
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
