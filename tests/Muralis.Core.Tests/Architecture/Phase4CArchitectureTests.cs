using System.Text.RegularExpressions;
using Xunit;

namespace Muralis.Core.Tests.Architecture;

/// <summary>
/// Locks Phase 4C's boundaries in place: the dock's pinned zone is a launcher rather than a second
/// desktop, it starts applications through the shell instead of through the UI, it is the dock's own
/// surface rather than a part of Clean Desktop, and a drag is still a drag rather than a save loop.
/// </summary>
public sealed class Phase4CArchitectureTests
{
    [Fact]
    public void TheUiNeverStartsAProcessItself()
    {
        // A launch is a request the shell answers, so it goes through IApplicationLauncher and comes
        // back as an outcome the dock can show. Nothing under the UI may reach for a process directly.
        var offenders = SourceFiles("src/Muralis.App/UI")
            .Where(file => new[] { "Process.Start", "ProcessStartInfo", "UseShellExecute", "ShellExecute" }
                .Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, $"the dock must start applications through the launcher, but starts one in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheDockUiNeverReadsOrWritesSettingsItself()
    {
        // Persistence is a Core concern behind IPinnedAppService and ISettingsService; a code-behind
        // that parsed the settings file would be a second, disagreeing copy of the dock.
        var offenders = SourceFiles("src/Muralis.App/UI")
            .Where(file => new[]
                {
                    "JsonSerializer", "JsonDocument", "JsonNode", "File.WriteAllText", "File.ReadAllText",
                    "File.OpenRead", "File.Create", "StreamReader", "StreamWriter",
                }
                .Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, $"the dock must not touch the settings file itself, but does in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyTheDockExperienceServiceShowsOrHidesTheDockWindow()
    {
        // One window, one owner. Two services showing and hiding the same window is how a mode change
        // ends up hiding the only thing left on the screen, so the window boundary is consumed in
        // exactly one place — Clean Desktop asks for the dock, it does not open it.
        var allowed = new[]
        {
            "src/Muralis.Core/Abstractions/IDesktopDockHost.cs",
            "src/Muralis.App/UI/Dock/DesktopDockHost.cs",
            "src/Muralis.Core/Services/DockExperienceService.cs",
            "src/Muralis.App/Infrastructure/AppHost.cs",
        };

        var offenders = SourceFiles("src")
            .Where(file => Code(file).Contains("IDesktopDockHost", StringComparison.Ordinal))
            .Select(Relative)
            .Where(file => !allowed.Contains(file, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0, $"IDesktopDockHost belongs to the dock's own service, but is used in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheDockNeedsNothingFromAnyDesktopMode()
    {
        // The dock is a product surface: it is on a fully native desktop, and Clean Desktop is only one
        // of the situations it happens to be useful in. The pin path therefore knows nothing about it.
        var pinPath = new[]
        {
            "src/Muralis.App/UI/Dock/DockHost.xaml",
            "src/Muralis.App/UI/Dock/DockHost.xaml.cs",
            "src/Muralis.App/UI/Dock/PinnedAppsPresenter.cs",
            "src/Muralis.App/UI/Dock/PinnedAppViewItem.cs",
            "src/Muralis.App/UI/Dock/PinnedZoneDrag.cs",
            "src/Muralis.App/UI/Dock/DesktopDockHost.cs",
        };

        foreach (var path in pinPath)
        {
            var code = Code(Absolute(path));
            foreach (var token in new[] { "CleanDesktop", "IDesktopExperienceService", "DesktopExperienceMode" })
            {
                Assert.True(
                    !code.Contains(token, StringComparison.Ordinal),
                    $"{path} must not depend on a desktop mode, but mentions {token}");
            }
        }

        // The one file under the dock that does name it is the hidden lab page, and only to print
        // whether the icons are currently hidden.
        var lab = Code(Absolute("src/Muralis.App/UI/Dock/DockLabPage.xaml.cs"));
        Assert.Contains("_cleanDesktop.IsNativeDesktopHidden", lab, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDockServiceDoesNotDependOnCleanDesktop()
    {
        // The other half of the same rule: the dock cannot ask a mode what to do, or the two would
        // depend on each other and the outcome would depend on which was created first.
        var contract = Code(Absolute("src/Muralis.Core/Abstractions/IDockExperienceService.cs"));
        Assert.DoesNotContain("CleanDesktop", contract, StringComparison.Ordinal);

        foreach (var method in new[] { "RestoreAsync", "SetVisibleAsync", "EnsureVisibleAsync", "ReleaseAsync" })
        {
            Assert.Contains(method, contract, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDockIsUpBeforeTheDesktopIconsAreHidden()
    {
        // Clean Desktop replaces Explorer's icons with the dock, so if the dock cannot be shown there is
        // nothing to show and the icons must stay. The order in the presentation is what guarantees it.
        var presentation = Code(Absolute("src/Muralis.Desktop/CleanDesktop/CleanDesktopPresentation.cs"));
        var shown = presentation.IndexOf("EnsureVisibleAsync", StringComparison.Ordinal);
        var hidden = presentation.IndexOf("RunAsync(HideIcons", StringComparison.Ordinal);

        Assert.True(shown >= 0, "Clean Desktop must ask for the dock before it takes the icons away");
        Assert.True(hidden > shown, "the icons may only hide once the dock is up");
    }

    [Fact]
    public void ThePinnedZoneCannotScrollAwayWithTheShelf()
    {
        // The Shelf scrolls; the pins must not. They are neighbours in the same row, and the only
        // ScrollViewer in the strip is the Shelf's own.
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml");
        var pinnedZone = host.IndexOf("x:Name=\"PinnedZone\"", StringComparison.Ordinal);
        var pinnedItems = host.IndexOf("ItemsSource=\"{x:Bind PinnedApps.Items", StringComparison.Ordinal);
        var indicator = host.IndexOf("x:Name=\"PinnedDropIndicator\"", StringComparison.Ordinal);
        var scroller = host.IndexOf("x:Name=\"ShelfScroller\"", StringComparison.Ordinal);

        Assert.True(pinnedZone >= 0 && pinnedItems > pinnedZone, "the pinned zone must exist and carry its own item list");
        Assert.True(indicator > pinnedItems && indicator < scroller, "the drop indicator must sit inside the pinned zone");
        Assert.True(pinnedItems < scroller, "the pinned zone must be outside the Shelf's scroller");
        Assert.Equal(1, Count(host, "<ScrollViewer"));
    }

    [Fact]
    public void TheDockPaintsWithTheFoundationRatherThanItsOwnTokens()
    {
        var xamlFiles = Directory.EnumerateFiles(
                Absolute("src/Muralis.App/UI/Dock"),
                "*.xaml",
                SearchOption.AllDirectories)
            .ToArray();

        foreach (var path in xamlFiles)
        {
            var xaml = File.ReadAllText(path);
            var name = Relative(path);

            // No new colours: a hex value in the dock would be a palette of its own.
            Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", xaml);

            // And no new tokens either: the dock consumes the foundation, it does not extend it.
            Assert.DoesNotContain("x:Key=\"Muralis", xaml, StringComparison.Ordinal);
        }

        // The insertion point is drawn in the product's own accent, at the shared pill radius.
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml");
        Assert.Contains("Background=\"{ThemeResource MuralisAccentPrimaryBrush}\"", host, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"{StaticResource MuralisRadiusPill}\"", host, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReorderHotPathStaysDirectManipulationOnly()
    {
        // §17: while the pointer is down the icon follows it one to one. No animation, no saving, no
        // layout work per frame — the commit happens once, on release.
        var drag = Code(Absolute("src/Muralis.App/UI/Dock/PinnedZoneDrag.cs"));
        var start = drag.IndexOf("public bool Move(", StringComparison.Ordinal);
        var release = drag.IndexOf("public async Task ReleaseAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0 && release > start, "the drag hot path must stay explicit and reviewable");

        var hotPath = drag[start..release];
        Assert.Contains("TransformMotion.SetTranslateX(_carried!", hotPath, StringComparison.Ordinal);
        Assert.Contains("DockReorder.TargetIndex(", hotPath, StringComparison.Ordinal);

        foreach (var token in new[] { "await", "MoveAsync", "SaveAsync", "Persist", "Animate", "Scale", "Glow" })
        {
            Assert.True(
                !hotPath.Contains(token, StringComparison.Ordinal),
                $"a drag must not {token} while the pointer is down");
        }

        var settling = drag[release..];
        Assert.Contains("AnimateTranslateXAsync", settling, StringComparison.Ordinal);
        Assert.Contains("_commit(", settling, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShellAndItsSurfacesAreStillThere()
    {
        // Phase 4C adds a zone to the dock; it does not replace the Shelf, the Clean Desktop path or the
        // frozen Phase 3 surfaces that the modes still use.
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Shelf/DesktopShelfService.cs")));
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/CleanDesktop/CleanDesktopPresentation.cs")));
        Assert.True(File.Exists(Absolute("src/Muralis.Desktop/Surfaces/CanvasSurfaceContent.cs")));

        var shelf = Code(Absolute("src/Muralis.Desktop/Shelf/DesktopShelfService.cs"));
        Assert.Contains("FileSystemWatcher", shelf, StringComparison.Ordinal);
        Assert.Contains("DebounceDelay", shelf, StringComparison.Ordinal);

        // The window boundary Clean Desktop used to hold is gone; there is only the dock's own.
        Assert.False(File.Exists(Absolute("src/Muralis.Core/Abstractions/ICleanDesktopShelfHost.cs")));
        Assert.True(File.Exists(Absolute("src/Muralis.Core/Abstractions/IDesktopDockHost.cs")));
    }

    private static int Count(string source, string text) =>
        source.Split(text, StringSplitOptions.None).Length - 1;

    private static IEnumerable<string> SourceFiles(string relativeDirectory)
    {
        var directory = Path.Combine(RepoRoot(), relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(directory), $"source directory not found: {directory}");

        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>File contents without comments, so explaining a rule is still allowed.</summary>
    private static string Code(string file)
    {
        var text = File.ReadAllText(file);
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\r\n]*", string.Empty);
    }

    private static string Relative(string file) => Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');

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
