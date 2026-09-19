using System.Text.RegularExpressions;
using System.Xml.Linq;
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

    [Fact]
    public void TheHeroOffersActionsRatherThanAModeSwitch()
    {
        var xaml = Source("src/Muralis.App/Views/HomePage.xaml");

        Assert.Contains("HeroPrimaryAction", xaml, StringComparison.Ordinal);
        Assert.Contains("HeroCustomizeAction", xaml, StringComparison.Ordinal);
        Assert.Contains("HeroExitAction", xaml, StringComparison.Ordinal);
        Assert.Contains("EnterMuralisModeCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ExitMuralisModeCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("CustomizeMuralisModeCommand", xaml, StringComparison.Ordinal);

        // A switch would say the mode is a setting and hide which way the change is going; the hero enters
        // and leaves the mode with a verb instead.
        Assert.DoesNotContain("ToggleSwitch", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=", xaml, StringComparison.Ordinal);

        // Every word comes from the catalogs: a literal here would be English whatever the language is.
        Assert.DoesNotMatch(new Regex("Text=\"[^{]"), xaml);
    }

    [Fact]
    public void TheHeroReadsAsAFlagshipExperienceAtEveryWidth()
    {
        var xaml = Source("src/Muralis.App/Views/HomePage.xaml");
        var codeBehind = Source("src/Muralis.App/Views/HomePage.xaml.cs");

        Assert.Contains("x:Name=\"HeroCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"360\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Home_MuralisMode_Brand", xaml, StringComparison.Ordinal);
        Assert.Contains("Home_MuralisMode_Preview_Native", xaml, StringComparison.Ordinal);
        Assert.Contains("Home_MuralisMode_Preview_Muralis", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeroFeatureSummary\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Viewbox Stretch=\"Uniform\">", xaml, StringComparison.Ordinal);
        Assert.Contains("WidthThatStacksTheHero", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("HeroPreview.Visibility", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStringTheHeroShowsExistsInBothLanguages()
    {
        var referenced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var surface in new[] { "src/Muralis.App/Views/HomePage.xaml", "src/Muralis.App/ViewModels/HomeViewModel.cs" })
        {
            foreach (Match match in Regex.Matches(Source(surface), @"Home_MuralisMode_[A-Za-z_]+"))
            {
                referenced.Add(match.Value);
            }
        }

        Assert.NotEmpty(referenced);

        var english = HeroKeys("src/Muralis.App/Strings/Resources.resx");
        var chinese = HeroKeys("src/Muralis.App/Strings/Resources.zh-CN.resx");

        // A key in one catalog and not the other is a string that goes missing in one language only.
        Assert.Equal(english, chinese);

        foreach (var key in referenced)
        {
            Assert.True(english.Contains(key), $"{key} is shown by the hero but missing from Resources.resx.");
            Assert.True(chinese.Contains(key), $"{key} is shown by the hero but missing from Resources.zh-CN.resx.");
        }
    }

    private static string[] HeroKeys(string relativePath) =>
    [
        .. XDocument
            .Load(Absolute(relativePath))
            .Root!
            .Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(name => name is not null && name.StartsWith("Home_MuralisMode_", StringComparison.Ordinal))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal),
    ];

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
