using System.Text.RegularExpressions;
using Xunit;

namespace Muralis.Core.Tests.Architecture;

public sealed class DesignFoundationTests
{
    [Fact]
    public void ApplicationMergesTheFoundationBeforeCompatibilityStyles()
    {
        var app = Source("src/Muralis.App/App.xaml");
        var expectedOrder = new[]
        {
            "UI/Tokens/Colors.xaml",
            "UI/Tokens/Typography.xaml",
            "UI/Tokens/Spacing.xaml",
            "UI/Tokens/Radius.xaml",
            "UI/Tokens/Elevation.xaml",
            "UI/Tokens/Motion.xaml",
            "UI/Materials/Materials.xaml",
            "UI/Controls/Controls.xaml",
            "Themes/Styles.xaml",
        };

        var cursor = -1;
        foreach (var resource in expectedOrder)
        {
            var next = app.IndexOf(resource, cursor + 1, StringComparison.Ordinal);
            Assert.True(next > cursor, $"{resource} is missing or merged out of order");
            cursor = next;
        }
    }

    [Fact]
    public void SemanticTokensAndReferenceComponentsExist()
    {
        foreach (var path in new[]
                 {
                     "src/Muralis.App/UI/Tokens/Colors.xaml",
                     "src/Muralis.App/UI/Tokens/Colors.Dark.xaml",
                     "src/Muralis.App/UI/Tokens/Colors.Light.xaml",
                     "src/Muralis.App/UI/Tokens/Typography.xaml",
                     "src/Muralis.App/UI/Tokens/Spacing.xaml",
                     "src/Muralis.App/UI/Tokens/Radius.xaml",
                     "src/Muralis.App/UI/Tokens/Elevation.xaml",
                     "src/Muralis.App/UI/Tokens/Motion.xaml",
                     "src/Muralis.App/UI/Materials/GlassSurface.cs",
                     "src/Muralis.App/UI/Controls/MuralisButton.cs",
                     "src/Muralis.App/UI/Controls/MuralisIconButton.cs",
                     "src/Muralis.App/UI/Controls/MuralisCard.cs",
                     "src/Muralis.App/UI/Controls/MuralisNavigationItem.cs",
                     "src/Muralis.App/UI/Controls/DockIcon.xaml",
                     "src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml",
                 })
        {
            Assert.True(File.Exists(Absolute(path)), $"foundation asset is missing: {path}");
        }
    }

    [Fact]
    public void ThemeAccentAndMotionAreCentralResources()
    {
        var colors = Source("src/Muralis.App/UI/Tokens/Colors.xaml");
        foreach (var token in new[] { "MuralisAccentPrimaryColor", "MuralisAccentSecondaryColor", "MuralisSurfaceTintColor", "MuralisGlowTintColor", "MuralisWallpaperMoodColor" })
        {
            Assert.Contains(token, colors, StringComparison.Ordinal);
        }

        var motionXaml = Source("src/Muralis.App/UI/Tokens/Motion.xaml");
        Assert.Contains("0:0:0.120", motionXaml, StringComparison.Ordinal);
        Assert.Contains("0:0:0.180", motionXaml, StringComparison.Ordinal);
        Assert.Contains("0:0:0.280", motionXaml, StringComparison.Ordinal);

        var motionCode = Source("src/Muralis.App/UI/Motion/InteractionMotion.cs");
        Assert.Contains("FromMilliseconds(120)", motionCode, StringComparison.Ordinal);
        Assert.Contains("FromMilliseconds(180)", motionCode, StringComparison.Ordinal);
        Assert.Contains("FromMilliseconds(280)", motionCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedControlResourcesDoNotEmbedRgbHexColors()
    {
        foreach (var path in new[]
                 {
                     "src/Muralis.App/UI/Controls/Controls.xaml",
                     "src/Muralis.App/UI/Materials/Materials.xaml",
                     "src/Muralis.App/Controls/WallpaperCard.xaml",
                 })
        {
            var matches = Regex.Matches(Source(path), "#[0-9A-Fa-f]{6,8}");
            Assert.True(matches.Count == 0, $"{path} embeds a color instead of using a semantic token");
        }
    }

    [Fact]
    public void ScalarSpacingTokensAreNotAssignedToThicknessProperties()
    {
        var xamlFiles = Directory.EnumerateFiles(
            Absolute("src/Muralis.App"),
            "*.xaml",
            SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        var unsafeAssignment = new Regex(
            "(?:Margin|Padding)=\"\\{StaticResource MuralisSpace[0-9]+\\}\"",
            RegexOptions.CultureInvariant);

        var offenders = xamlFiles
            .Where(path => unsafeAssignment.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepoRoot(), path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"scalar spacing tokens cannot be assigned to Margin or Padding: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void HomeIsAReferenceMigrationAndPlaygroundHasAHiddenRoute()
    {
        var home = Source("src/Muralis.App/Views/HomePage.xaml");
        Assert.Contains("ui:MuralisButton", home, StringComparison.Ordinal);
        Assert.Contains("MuralisPageInset", home, StringComparison.Ordinal);
        Assert.Contains("MuralisSurfaceSelectedBrush", home, StringComparison.Ordinal);

        var navigation = Source("src/Muralis.App/Services/NavigationService.cs");
        Assert.Contains("DesignPlaygroundPage", navigation, StringComparison.Ordinal);
        Assert.Contains("design-playground", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundationV11KeepsMaterialDepthAndMotionPolicyCentralized()
    {
        var colors = Source("src/Muralis.App/UI/Tokens/Colors.xaml");
        foreach (var token in new[]
                 {
                     "MuralisGlassLowBrush",
                     "MuralisGlassMediumBrush",
                     "MuralisGlassHighBrush",
                     "MuralisPrimaryButtonBrush",
                     "MuralisAppBackgroundBrush",
                 })
        {
            Assert.Contains(token, colors, StringComparison.Ordinal);
        }

        var materials = Source("src/Muralis.App/UI/Materials/Materials.xaml");
        Assert.Contains("MuralisEdgeHighlightLowBrush", materials, StringComparison.Ordinal);
        Assert.Contains("MuralisEdgeHighlightMediumBrush", materials, StringComparison.Ordinal);
        Assert.Contains("MuralisEdgeHighlightHighBrush", materials, StringComparison.Ordinal);

        var controls = Source("src/Muralis.App/UI/Controls/Controls.xaml");
        Assert.Contains("MuralisPrimaryButtonBrush", controls, StringComparison.Ordinal);
        Assert.DoesNotContain("Background\" Value=\"{ThemeResource MuralisAccentPrimaryBrush}", controls, StringComparison.Ordinal);

        var motion = Source("src/Muralis.App/UI/Motion/InteractionMotion.cs");
        Assert.Contains("UISettings().AnimationsEnabled", motion, StringComparison.Ordinal);
        Assert.Contains("AnimationsEnabledOverride", motion, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticColorsHaveMatchingDarkAndLightThemeDictionaries()
    {
        var root = Source("src/Muralis.App/UI/Tokens/Colors.xaml");
        Assert.Contains("ResourceDictionary.ThemeDictionaries", root, StringComparison.Ordinal);
        Assert.Contains("Colors.Dark.xaml", root, StringComparison.Ordinal);
        Assert.Contains("Colors.Light.xaml", root, StringComparison.Ordinal);

        var dark = Source("src/Muralis.App/UI/Tokens/Colors.Dark.xaml");
        var light = Source("src/Muralis.App/UI/Tokens/Colors.Light.xaml");
        var keyPattern = new Regex("x:Key=\"(?<key>Muralis[^\"]+)\"", RegexOptions.CultureInvariant);
        var darkKeys = keyPattern.Matches(dark).Select(match => match.Groups["key"].Value).Order().ToArray();
        var lightKeys = keyPattern.Matches(light).Select(match => match.Groups["key"].Value).Order().ToArray();
        Assert.Equal(darkKeys, lightKeys);

        foreach (var key in new[]
                 {
                     "MuralisBackground0Color",
                     "MuralisSidebarColor",
                     "MuralisTextPrimaryColor",
                     "MuralisSurfaceLowColor",
                     "MuralisGlassLowTopColor",
                     "MuralisGlassMediumTopColor",
                     "MuralisGlassHighTopColor",
                     "MuralisBorderNormalColor",
                     "MuralisSurfaceSelectedColor",
                     "MuralisPrimaryButtonTopColor",
                 })
        {
            Assert.Contains(key, darkKeys);
        }

        foreach (var path in new[]
                 {
                     "src/Muralis.App/MainWindow.xaml",
                     "src/Muralis.App/Views/HomePage.xaml",
                     "src/Muralis.App/Controls/WallpaperCard.xaml",
                     "src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml",
                 })
        {
            Assert.DoesNotMatch("\\{StaticResource Muralis[A-Za-z0-9]+Color\\}", Source(path));
        }
    }

    [Fact]
    public void SidebarSelectionFollowsTheActualRoute()
    {
        var mainWindow = Source("src/Muralis.App/MainWindow.xaml.cs");
        Assert.Contains("ApplyNavigationSelection(e.PageKey)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RootNavigationView.SelectedItem = matchingItem", mainWindow, StringComparison.Ordinal);
        Assert.Contains("item.IsSelected = ReferenceEquals(item, matchingItem)", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("matchingItem is not null", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaygroundUsesProductLikeNightscapeAndCompleteDockStates()
    {
        var playground = Source("src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml");
        Assert.DoesNotContain("<Ellipse", playground, StringComparison.Ordinal);
        Assert.Contains("Glass Low", playground, StringComparison.Ordinal);
        Assert.Contains("Glass Medium", playground, StringComparison.Ordinal);
        Assert.Contains("Glass High", playground, StringComparison.Ordinal);
        Assert.Contains("IsRunning=\"True\"", playground, StringComparison.Ordinal);
        Assert.Contains("IsPressedPreview=\"True\"", playground, StringComparison.Ordinal);
        Assert.Contains("IsSelected=\"True\"", playground, StringComparison.Ordinal);
    }

    private static string Source(string relativePath) => File.ReadAllText(Absolute(relativePath));

    private static string Absolute(string relativePath) =>
        Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Muralis.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Muralis.slnx was not found above {AppContext.BaseDirectory}");
    }
}
