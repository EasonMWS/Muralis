using System.Text.RegularExpressions;
using Muralis.Core.Models;
using Muralis.Desktop.Shell;
using Xunit;

namespace Muralis.Desktop.Tests.Architecture;

/// <summary>
/// Locks the Phase 1 ownership split in place: the desktop shell owns the thread, the window and the
/// wallpaper layer; the surface host is the only place that creates desktop windows; the video
/// content only renders. These guards are deliberately small - they check for the absence of the
/// legacy session and of cross-layer concepts, not for a particular code shape.
/// </summary>
public sealed class ArchitectureGuardTests
{
    [Fact]
    public void NoLegacySessionTypeExists()
    {
        foreach (var assembly in new[] { typeof(DesktopShell).Assembly, typeof(MonitorRef).Assembly })
        {
            var legacy = assembly.GetTypes().Where(t => t.Name.Contains("DesktopHostSession", StringComparison.Ordinal)).ToList();
            Assert.True(legacy.Count == 0, $"{assembly.GetName().Name} still contains {string.Join(", ", legacy)}");
        }
    }

    [Fact]
    public void NoSourceFileMentionsTheLegacySession()
    {
        var offenders = SourceFiles("src")
            .Where(file => Code(file).Contains("DesktopHostSession", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0, $"the legacy session is still referenced by: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyTheSurfaceHostCreatesDesktopWindows()
    {
        AssertOnlyUses(
            "src/Muralis.Desktop",
            "NativeMethods.CreateWindowExW(",
            "desktop windows are created by the surface host; the shell event source and the pointer router each have a hidden window of their own",
            "src/Muralis.Desktop/Surfaces/Win32SurfaceHost.cs",
            "src/Muralis.Desktop/Shell/ShellEventSource.cs",
            "src/Muralis.Desktop/Input/DesktopPointerRouter.cs");
        AssertOnlyUses(
            "src/Muralis.Desktop",
            "NativeMethods.SetParent(",
            "only the surface host may attach a window to the desktop layer",
            "src/Muralis.Desktop/Surfaces/Win32SurfaceHost.cs");
    }

    [Fact]
    public void OnlyThePointerRouterRegistersRawInput()
    {
        AssertOnlyUses(
            "src",
            "NativeMethods.RegisterRawInputDevices(",
            "raw input is the pointer router's mechanism and no other component may register for it",
            "src/Muralis.Desktop/Input/DesktopPointerRouter.cs");
    }

    [Fact]
    public void NothingInstallsGlobalHooks()
    {
        // Phase 3A chose a passive raw-input registration over a hook: Muralis never inserts itself
        // into the input path of other applications, so a global hook must never appear.
        var offenders = SourceFiles("src")
            .Where(file => new[] { "SetWindowsHookEx", "WH_MOUSE_LL", "WH_KEYBOARD_LL", "UnhookWindowsHookEx" }
                .Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0, $"the desktop must never install a global hook, but hooking appears in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyOneComponentRendersVideo()
    {
        AssertOnlyUses(
            "src/Muralis.Desktop",
            "MediaPlayer",
            "video playback belongs to VideoSurfaceContent and nothing else in the desktop layer",
            "src/Muralis.Desktop/Surfaces/VideoSurfaceContent.cs");
    }

    [Fact]
    public void OnlyOneComponentFindsTheDesktopLayer()
    {
        const string reason = "the wallpaper worker is discovered in exactly one place";
        const string workerWindow = "src/Muralis.Desktop/Interop/DesktopWorkerWindow.cs";
        AssertOnlyUses("src/Muralis.Desktop", "NativeMethods.EnumWindows(", reason, workerWindow);
        AssertOnlyUses("src/Muralis.Desktop", "NativeMethods.FindWindowExW(", reason, workerWindow);
        AssertOnlyUses("src/Muralis.Desktop", "\"WorkerW\"", reason, workerWindow);
        AssertOnlyUses("src/Muralis.Desktop", "SHELLDLL_DefView", reason, workerWindow);
    }

    [Fact]
    public void OnlyTheShellEventSourceNoticesExplorerRestarts()
    {
        AssertOnlyUses(
            "src/Muralis.Desktop",
            "TaskbarCreated",
            "Explorer restart discovery belongs to ShellEventSource",
            "src/Muralis.Desktop/Shell/ShellEventSource.cs");
    }

    [Fact]
    public void VideoContentDoesNotTouchTheDesktopLayer()
    {
        var file = Path.Combine(RepoRoot(), "src", "Muralis.Desktop", "Surfaces", "VideoSurfaceContent.cs");
        Assert.True(File.Exists(file), $"VideoSurfaceContent.cs is missing at {file}");

        var code = Code(file);
        foreach (var token in new[] { "WorkerW", "SHELLDLL_DefView", "CreateWindowEx", "SetParent", "TaskbarCreated", "ShellRestarted" })
        {
            Assert.True(
                !code.Contains(token, StringComparison.Ordinal),
                $"VideoSurfaceContent must not know about the desktop layer, but mentions {token}");
        }
    }

    [Fact]
    public void TheShellDoesNotOwnVideoRendering()
    {
        var offenders = SourceFiles("src/Muralis.Desktop/Shell")
            .Where(file => new[] { "MediaPlayer", "IDXGISwapChain", "VideoFrameAvailable", "CopyFrameToVideoSurface" }
                .Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0, $"video rendering must stay out of the shell, but appears in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void CanvasContentDoesNotTouchTheDesktopLayer()
    {
        // Phase 2: the canvas draws items, lays out and takes input. Finding the desktop layer,
        // creating the window and recovering from Explorer restarts stay the shell's business.
        var code = Code(Path.Combine(RepoRoot(), "src", "Muralis.Desktop", "Surfaces", "CanvasSurfaceContent.cs"));
        foreach (var token in new[]
                 {
                     "WorkerW", "SHELLDLL_DefView", "CreateWindowEx", "SetParent",
                     "TaskbarCreated", "ShellRestarted", "EnumWindows", "FindWindowExW",
                 })
        {
            Assert.True(
                !code.Contains(token, StringComparison.Ordinal),
                $"the desktop canvas must not know about the desktop layer, but mentions {token}");
        }
    }

    [Fact]
    public void OnlyTheIconLayersKnowHowToReadAShellIcon()
    {
        // Phase 3B: pulling an icon out of the shell is one job, done in one place behind the
        // interop declarations. Everything else in the desktop layer sees pixels, not HICONs.
        const string reason = "shell icon extraction belongs to the icon reader and the interop surface it sits on";
        foreach (var token in new[] { "SHGetFileInfoW", "SHGetImageList", "IImageList", "GetDIBits", "GetIconInfo" })
        {
            AssertOnlyUses(
                "src/Muralis.Desktop",
                token,
                reason,
                "src/Muralis.Desktop/Icons/ShellIconReader.cs",
                "src/Muralis.Desktop/Interop/NativeMethods.cs",
                "src/Muralis.Desktop/Interop/IImageList.cs");
        }
    }

    [Fact]
    public void TheCanvasDoesNotUnderstandIconExtraction()
    {
        // The canvas shows an icon; how one is obtained is the icon layer's business, so no shell
        // icon call may appear in the content itself.
        var code = Code(Path.Combine(RepoRoot(), "src", "Muralis.Desktop", "Surfaces", "CanvasSurfaceContent.cs"));
        foreach (var token in new[] { "SHGetFileInfo", "SHGetImageList", "IImageList", "GetDIBits", "DestroyIcon", "HICON" })
        {
            Assert.True(
                !code.Contains(token, StringComparison.Ordinal),
                $"the desktop canvas must not extract icons itself, but mentions {token}");
        }
    }

    [Fact]
    public void OnlyTheTakeoverNamesTheDesktopIconList()
    {
        // Phase 3D changed the rule the canvas prototype was built on: hiding the desktop icons is now
        // exactly what a takeover is, so the window that draws them is named — but only once, in the
        // interop layer that the desktop layer lookup already goes through. Everything else in the app
        // works with the shell's view, not with the window.
        AssertOnlyUses(
            "src",
            "SysListView32",
            "the desktop's icon list is the takeover's last resort and is named in exactly one place",
            "src/Muralis.Desktop/Interop/NativeMethods.cs");
    }

    [Fact]
    public void NoForbiddenWayOfHidingTheDesktopIcons()
    {
        // The takeover has to be reversible, verified, and leave nothing behind, which rules out every
        // well known shortcut: re-arranging or moving the list view's items, the undocumented system
        // parameter, the registry value that hides the icons, and reaching into Explorer's process to
        // change it from the inside. None of them can be undone from a record, so none of them is used.
        var forbidden = new[]
        {
            "LVM_", "SPI_SETICONS", "FFlags", "WriteProcessMemory", "ReadProcessMemory",
            "CreateRemoteThread", "VirtualAllocEx", "OpenProcess",
        };

        var offenders = SourceFiles("src")
            .Where(file => forbidden.Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"the desktop icons may only be hidden through the shell's own view, but a forbidden way appears in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheTakeoverNeverWritesTheRegistry()
    {
        // The setting that hides the desktop icons is a registry value, and changing it is exactly the
        // shortcut this phase rejected: it is not reversible from the state the shell reports, and it
        // outlives the process that wrote it with nothing recording what it was.
        var offenders = SourceFiles("src/Muralis.Desktop/Takeover")
            .Concat(SourceFiles("src/Muralis.Core/Desktop/Takeover"))
            .Where(file => new[] { "Registry", "RegistryKey", "SetValue", "OpenSubKey" }
                .Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, $"the takeover must not touch the registry, but does in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheTakeoverNeverRestartsExplorer()
    {
        // Restarting Explorer is how a user gets their desktop back when it is broken; it is not how
        // Muralis switches a mode. The takeover works while Explorer keeps running.
        var offenders = SourceFiles("src/Muralis.Desktop/Takeover")
            .Where(file => new[] { "Process.Start", "Process.Kill", "Process.GetProcesses", "explorer.exe" }
                .Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, $"the takeover must not restart or end Explorer, but appears in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheAppDoesNotTouchTheDesktopLayer()
    {
        var offenders = SourceFiles("src/Muralis.App")
            .Where(file => new[] { "WorkerW", "SHELLDLL_DefView", "MuralisDesktopHostWindow", "Win32SurfaceHost", "DesktopLayerHost", "SetParent(" }
                .Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0, $"the desktop layer belongs to Muralis.Desktop, but the App mentions it in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void CoreDoesNotDependOnWindowsNativeOrDesktop()
    {
        var references = typeof(MonitorRef).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        foreach (var forbidden in new[] { "Muralis.Desktop", "Microsoft.Windows.SDK.NET", "WinRT.Runtime", "Microsoft.UI.Xaml" })
        {
            Assert.True(!references.Contains(forbidden), $"Muralis.Core must not reference {forbidden}");
        }
    }

    [Fact]
    public void DesktopDoesNotDependOnTheUiFrameworkOrApp()
    {
        var references = typeof(DesktopShell).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        var offenders = references
            .Where(name => name.StartsWith("Microsoft.UI", StringComparison.Ordinal)
                        || name.StartsWith("Microsoft.WindowsAppSDK", StringComparison.Ordinal)
                        || name == "Muralis.App")
            .ToList();
        Assert.True(offenders.Count == 0, $"Muralis.Desktop must not reference the UI layer, but references: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<string> SourceFiles(string relativeDirectory)
    {
        var directory = Path.Combine(RepoRoot(), relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(directory), $"source directory not found: {directory}");

        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Fails when any file below <paramref name="relativeDirectory"/> uses <paramref name="token"/> outside the allowed files.</summary>
    private static void AssertOnlyUses(string relativeDirectory, string token, string reason, params string[] allowedFiles)
    {
        var allowed = allowedFiles.Select(Slash).ToArray();
        var offenders = SourceFiles(relativeDirectory)
            .Where(file => Code(file).Contains(token, StringComparison.Ordinal))
            .Select(Relative)
            .Where(file => !allowed.Contains(Slash(file), StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0, $"{reason}; {token} appears in: {string.Join(", ", offenders)}");
    }

    private static string Slash(string path) => path.Replace('\\', '/');

    /// <summary>File contents without comments, so explaining a rule is still allowed.</summary>
    private static string Code(string file)
    {
        var text = File.ReadAllText(file);
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\r\n]*", string.Empty);
    }

    private static string Relative(string file) => Path.GetRelativePath(RepoRoot(), file);

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
