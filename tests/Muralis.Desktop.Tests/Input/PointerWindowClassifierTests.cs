using Muralis.Desktop.Input;
using Muralis.Desktop.Surfaces;
using Xunit;

namespace Muralis.Desktop.Tests.Input;

/// <summary>
/// The context walk is what keeps the desktop from reacting to the pointer while it is over an
/// ordinary application window, so the cases here are the ones the live behaviour depends on:
/// our own surfaces win over everything, the shell's desktop classes count as the desktop, and a
/// foreign window anywhere up the chain makes the whole thing foreign.
/// </summary>
public sealed class PointerWindowClassifierTests
{
    private static readonly nint Desktop = unchecked((nint)0x1000);
    private static readonly nint Foreign = unchecked((nint)0x2000);
    private static readonly nint Worker = unchecked((nint)0x3000);
    private static readonly nint IconView = unchecked((nint)0x4000);
    private static readonly nint Canvas = unchecked((nint)0x5000);

    [Fact]
    public void AHandedOutSurfaceWindowIsOurs()
    {
        var probe = new FakeProbe { DesktopWindow = Desktop };
        probe.Classes[Canvas] = Win32SurfaceHost.WindowClassName;
        probe.Parents[Canvas] = IconView;

        Assert.Equal(DesktopPointerContext.Surface, PointerWindowClassifier.Classify(Canvas, probe));
    }

    [Fact]
    public void TheDesktopLayerIsTheDesktop()
    {
        var probe = new FakeProbe { DesktopWindow = Desktop };
        probe.Classes[IconView] = "SHELLDLL_DefView";
        probe.Classes[Worker] = "WorkerW";
        probe.Parents[IconView] = Worker;
        probe.Parents[Worker] = Desktop;
        probe.Classes[Desktop] = "#32769";

        Assert.Equal(DesktopPointerContext.Desktop, PointerWindowClassifier.Classify(IconView, probe));
        Assert.Equal(DesktopPointerContext.Desktop, PointerWindowClassifier.Classify(Worker, probe));
    }

    [Fact]
    public void ACanvasIsOursEvenThoughItSitsInsideTheDesktopsChain()
    {
        // The canvas is a child of the icon host: without checking our own class first, the walk
        // would climb to the icon view and call our own window the desktop.
        var probe = new FakeProbe { DesktopWindow = Desktop };
        probe.Classes[IconView] = "SHELLDLL_DefView";
        probe.Classes[Canvas] = Win32SurfaceHost.WindowClassName;
        probe.Parents[Canvas] = IconView;
        probe.Parents[IconView] = Desktop;

        Assert.Equal(DesktopPointerContext.Surface, PointerWindowClassifier.Classify(Canvas, probe));
    }

    [Fact]
    public void AnOrdinaryApplicationWindowIsForeign()
    {
        var probe = new FakeProbe { DesktopWindow = Desktop };
        probe.Classes[Foreign] = "Chrome_WidgetWin_1";
        probe.Parents[Foreign] = Desktop;
        probe.Classes[Desktop] = "#32769";

        Assert.Equal(DesktopPointerContext.Foreign, PointerWindowClassifier.Classify(Foreign, probe));
    }

    [Fact]
    public void TheTaskbarIsForeignEvenThoughItIsParentedToTheDesktopWindow()
    {
        // The taskbar is a top-level child of the desktop window: without the class check the walk
        // would climb to the desktop window and make every taskbar hover count as desktop input.
        var probe = new FakeProbe { DesktopWindow = Desktop };
        probe.Classes[Foreign] = "Shell_TrayWnd";
        probe.Parents[Foreign] = Desktop;

        Assert.Equal(DesktopPointerContext.Foreign, PointerWindowClassifier.Classify(Foreign, probe));
    }

    [Fact]
    public void NothingUnderThePointerIsForeign()
    {
        var probe = new FakeProbe { DesktopWindow = Desktop };

        Assert.Equal(DesktopPointerContext.Foreign, PointerWindowClassifier.Classify(nint.Zero, probe));
    }

    [Fact]
    public void AChainThatReachesZeroWithoutReachingTheDesktopIsForeign()
    {
        var probe = new FakeProbe { DesktopWindow = Desktop };
        probe.Classes[Foreign] = "ApplicationFrameWindow";
        probe.Parents[Foreign] = nint.Zero;

        Assert.Equal(DesktopPointerContext.Foreign, PointerWindowClassifier.Classify(Foreign, probe));
    }

    [Fact]
    public void WindowsWithoutAClassAreSkippedAndTheWalkContinues()
    {
        var probe = new FakeProbe { DesktopWindow = Desktop };
        probe.Classes[Foreign] = "Chrome_WidgetWin_1";
        probe.Classes[Canvas] = Win32SurfaceHost.WindowClassName;
        probe.Parents[Canvas] = Foreign;
        probe.Parents[Foreign] = Desktop;

        Assert.Equal(DesktopPointerContext.Surface, PointerWindowClassifier.Classify(Canvas, probe));
    }

    [Fact]
    public void ALoopInTheParentChainStopsWithoutHanging()
    {
        var probe = new FakeProbe { DesktopWindow = Desktop };
        probe.Classes[Foreign] = "ApplicationFrameWindow";
        probe.Parents[Foreign] = Foreign;

        Assert.Equal(DesktopPointerContext.Foreign, PointerWindowClassifier.Classify(Foreign, probe));
    }

    private sealed class FakeProbe : IPointerWindowProbe
    {
        internal Dictionary<nint, string> Classes { get; } = [];

        internal Dictionary<nint, nint> Parents { get; } = [];

        public nint DesktopWindow { get; init; }

        public nint ParentOf(nint window) => Parents.TryGetValue(window, out var parent) ? parent : nint.Zero;

        public string? ClassNameOf(nint window) => Classes.TryGetValue(window, out var name) ? name : null;
    }
}
