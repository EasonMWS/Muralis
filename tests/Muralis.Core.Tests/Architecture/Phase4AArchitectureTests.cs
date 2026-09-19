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

    /// <summary>
    /// The bottom Dock's presentation is the pinned applications and nothing else. The Desktop Shelf and
    /// the utility items are live services — 4A's zones still exist as contracts — but they are no longer
    /// part of this visual tree, so nothing here may bring them back onto the desktop strip.
    /// </summary>
    [Fact]
    public void DockShell_PresentsPinnedAppsAndNoOtherZone()
    {
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml");

        // No scroller at all: the Shelf's own viewport was the only one, and it has been detached.
        Assert.Equal(0, Count(host, "<ScrollViewer"));
        Assert.DoesNotContain("ShelfScroller", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ShelfItems", host, StringComparison.Ordinal);
        Assert.DoesNotContain("UtilityItems", host, StringComparison.Ordinal);

        // The pinned zone is still a real launcher, fed by its presenter.
        Assert.Contains("ItemsSource=\"{x:Bind PinnedApps.Items", host, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PinnedZone\"", host, StringComparison.Ordinal);
    }

    /// <summary>
    /// Detaching the Shelf from the bottom strip did not delete the Shelf.
    /// </summary>
    /// <remarks>
    /// This preserves the rule the replaced <c>DockShell_HasFixedOuterZonesAndOneScrollableShelf</c> carried:
    /// the Desktop Shelf is one of the product's surfaces. It is no longer drawn on the bottom strip, but it
    /// still projects the real desktop, and nothing else in the suite would notice if it were deleted — the
    /// only other place it is named is a service-existence check that says nothing about its shell
    /// integration. The seam a future floating Shelf would be built on is asserted here, so removing it is a
    /// deliberate act with a failing test attached rather than a silent one.
    /// </remarks>
    [Fact]
    public void DockShell_DetachedFromTheShelfWithoutDeletingIt()
    {
        // The contract and its implementation are both still here.
        Assert.True(File.Exists(Absolute("src/Muralis.Core/Abstractions/IDesktopShelfService.cs")));
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Shelf/DesktopShelfService.cs")));

        // It still projects real desktop content: the shell scan, the folder watcher that keeps it live, and
        // the launch path. Those three are what "a Shelf" means, and none of them is presentation.
        var shelf = Source("src/Muralis.Desktop/Shelf/DesktopShelfService.cs");
        Assert.Contains("DesktopContentScanner", shelf, StringComparison.Ordinal);
        Assert.Contains("FileSystemWatcher", shelf, StringComparison.Ordinal);
        Assert.Contains("DebounceDelay", shelf, StringComparison.Ordinal);
        Assert.Contains("public Task OpenAsync(", shelf, StringComparison.Ordinal);

        // And it is still the one the application builds, so the surface can be re-attached without first
        // resurrecting a service.
        Assert.Contains(
            "services.AddSingleton<IDesktopShelfService, DesktopShelfService>();",
            Source("src/Muralis.App/Infrastructure/AppHost.cs"),
            StringComparison.Ordinal);

        // The dock itself names none of it, which is the product decision this pair of tests records.
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml");
        Assert.DoesNotContain("DesktopShelf", host, StringComparison.Ordinal);
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
    public void Phase3Takeover_RemainsPresentButIsNoLongerAProductMode()
    {
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Modes/DesktopModeService.cs")));
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Takeover/DesktopTakeoverService.cs")));
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Surfaces/CanvasSurfaceContent.cs")));

        // The frozen layer keeps the one job it still has: a desktop that owes Windows its icons back.
        var coordinator = Source("src/Muralis.Core/Services/DesktopExperienceService.cs");
        Assert.Contains("DesktopMode.Native", coordinator, StringComparison.Ordinal);

        // The retired modes are not choices. The converter still reads them so an old file is not
        // stranded, but the enum the product exposes holds the two modes and nothing else.
        var modes = Source("src/Muralis.Core/Models/DesktopExperienceSettings.cs");
        var enumBody = modes[..modes.IndexOf('}')];
        Assert.Contains("Native", enumBody, StringComparison.Ordinal);
        Assert.Contains("Muralis", enumBody, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanDesktop", enumBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FullTakeoverExperimental", enumBody, StringComparison.Ordinal);
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
