using Xunit;

namespace Muralis.Core.Tests.Architecture;

public sealed class Phase4AArchitectureTests
{
    [Fact]
    public void DockHost_IsIndependentFromPhase3CanvasAndLegacyDock()
    {
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml.cs");

        Assert.DoesNotContain("using Muralis.Core.Canvas", host, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopModeService _", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Muralis.Core.Dock;", host, StringComparison.Ordinal);
        Assert.Contains("MotionTargets", host, StringComparison.Ordinal);
    }

    [Fact]
    public void DockShell_HasFixedOuterZonesAndOneScrollableShelf()
    {
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml");

        Assert.Equal(1, Count(host, "<ScrollViewer"));
        Assert.Contains("x:Name=\"ShelfScroller\"", host, StringComparison.Ordinal);

        // Phase 4C made the pinned zone a real launcher, so it is fed by its presenter instead of a
        // plain item list; the other two zones still bind the collections their host provides.
        Assert.Contains("ItemsSource=\"{x:Bind PinnedApps.Items", host, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind ShelfItems", host, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind UtilityItems", host, StringComparison.Ordinal);
    }

    [Fact]
    public void DockLab_IsHiddenFromFormalNavigation()
    {
        var routes = Source("src/Muralis.App/Services/NavigationService.cs");
        var shell = Source("src/Muralis.App/MainWindow.xaml");
        var playground = Source("src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml");

        Assert.Contains("DockLabPage", routes, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"dock-lab\"", shell, StringComparison.Ordinal);
        Assert.Contains("Open Dock Lab", playground, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanDesktopContract_ForbidsFileAndExplorerOwnership()
    {
        var contract = Source("src/Muralis.Core/Abstractions/ICleanDesktopPresentation.cs");

        Assert.Contains("must never move, delete, rename", contract, StringComparison.Ordinal);
        Assert.Contains("must never kill Explorer", contract, StringComparison.Ordinal);
        Assert.Contains("bool IsAvailable", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase3Takeover_RemainsPresentAsCompatibilityLayer()
    {
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Modes/DesktopModeService.cs")));
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Takeover/DesktopTakeoverService.cs")));
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Surfaces/CanvasSurfaceContent.cs")));

        var coordinator = Source("src/Muralis.Core/Services/DesktopExperienceService.cs");
        Assert.Contains("DesktopMode.Takeover", coordinator, StringComparison.Ordinal);
        Assert.Contains("FullTakeoverExperimental", coordinator, StringComparison.Ordinal);
    }

    private static int Count(string source, string text) =>
        source.Split(text, StringSplitOptions.None).Length - 1;

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
