using System.Text.RegularExpressions;
using Xunit;

namespace Muralis.Desktop.Tests.Architecture;

/// <summary>
/// Guards for the dock's pointer source: how it registers, what it must not change, and where it is allowed to
/// sit in the architecture.
/// </summary>
/// <remarks>
/// Every one of these is a rule that is easy to break by accident and expensive to notice: a registration that
/// changes what other windows receive, a resize on the pointer path, or a dependency back into the retired
/// phases would each look like a small local choice.
/// </remarks>
public sealed class Phase4DArchitectureTests
{
    private const string RawInputFile = "src/Muralis.Desktop/Input/RawInputRegistry.cs";
    private const string RawPointerBroker = "src/Muralis.Desktop/Input/RawPointerBroker.cs";
    private const string DesktopPointerRouter = "src/Muralis.Desktop/Input/DesktopPointerRouter.cs";
    private const string DockCoordinator = "src/Muralis.App/UI/Dock/DockMotionCoordinator.cs";
    private const string DockHostCode = "src/Muralis.App/UI/Dock/DockHost.xaml.cs";
    private const string DockHostXaml = "src/Muralis.App/UI/Dock/DockHost.xaml";
    private const string DesktopDockHost = "src/Muralis.App/UI/Dock/DesktopDockHost.cs";
    private const string PointerMath = "src/Muralis.Core/Motion/DockPointerMath.cs";

    [Fact]
    public void RawInputIsRegisteredPassivelyAndOnlyForTheMouse()
    {
        var source = Code(RawInputFile);

        // The mouse, and the generic-desktop usage page it lives on.
        Assert.Contains("HidUsagePageGenericDesktop", source, StringComparison.Ordinal);
        Assert.Contains("HidUsageMouse", source, StringComparison.Ordinal);

        // Passive: reports arrive even while the window is in the background, which is the whole reason the
        // dock can use raw input at all.
        Assert.Contains("RidevInputSink", source, StringComparison.Ordinal);

        // The two flags that would change what the rest of Windows sees. Neither may appear, not even in a
        // comment that happens to name them, because the next person to edit this file will not know which.
        Assert.DoesNotContain("RidevNoLegacy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RidevCaptureMouse", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RawInputNeverReplacesAnExistingProcessOwner()
    {
        var source = Code(RawInputFile);

        Assert.Contains("RawInputRegistry.Current().HasMouseOwner", source, StringComparison.Ordinal);
        Assert.Contains("RawMouseAlreadyOwned", source, StringComparison.Ordinal);
        Assert.Contains("if (_registered)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DockAndDesktopConsumeOneNeutralProcessBroker()
    {
        var broker = Code(RawPointerBroker);
        var dock = Code(DockCoordinator);
        var desktop = Code(DesktopPointerRouter);

        Assert.Contains("class RawPointerBroker", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopCanvas", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("Wallpaper", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("Dock", broker, StringComparison.Ordinal);
        Assert.Contains("_pointerBroker?.Subscribe(", dock, StringComparison.Ordinal);
        Assert.Contains("_broker?.Subscribe(", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterRawInputDevices", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterRawInputDevices", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePointerSourceNeverHooksTheInputPath()
    {
        // A passive registration can be replaced by a hook by accident; a hook cannot be un-chosen by accident.
        var offenders = SourceFiles("src")
            .Where(file => new[] { "SetWindowsHookEx", "WH_MOUSE_LL", "WH_KEYBOARD_LL", "WS_EX_TRANSPARENT", "HTTRANSPARENT" }
                .Any(token => Code(file).Contains(token, StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, $"the dock's pointer source must stay passive, but a hook or a click-through hack appears in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheDockPointerSourceOwesNothingToTheRetiredPhases()
    {
        // The dock's motion is part of the current product, not of the Phase 3 canvas. Raw input is a technique
        // it borrows; the services of that phase are not.
        foreach (var file in new[] { RawInputFile, DockCoordinator })
        {
            var source = Code(file);
            Assert.DoesNotContain("DesktopCanvas", source, StringComparison.Ordinal);
            Assert.DoesNotContain("FullTakeover", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerW", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DesktopPointerRouter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IDesktopShell", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePointerPathNeverResizesTheWindow()
    {
        var coordinator = Code(DockCoordinator);

        // Whatever else the pointer path does, it does not touch the window: a resize per move would be a
        // layout storm and a visible hitch. Nothing that can move or size a window may appear, and the dock
        // reports what it wants instead of taking it.
        Assert.DoesNotContain("MoveAndResize", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("AppWindow", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowPos", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Move(", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain(".Margin =", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("SetValue(FrameworkElement.MarginProperty", coordinator, StringComparison.Ordinal);

        // The one place the dock asks for its bounds is the event the host listens to.
        Assert.Contains("PointerInsideChanged?.Invoke(", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void RawPointerReportsAreQueuedIndividuallyRatherThanFrameCoalesced()
    {
        var coordinator = Code(DockCoordinator);

        Assert.Contains("PointerSample[]", coordinator, StringComparison.Ordinal);
        Assert.Contains("while (TryDequeuePointer(out var sample))", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingX", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("FlushPointer", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheHostSpendsTheMotionBounds()
    {
        var host = Code(DesktopDockHost);

        // The window is the host's business, and it changes size only where it is told to.
        Assert.Contains("MotionBoundsChanged", host, StringComparison.Ordinal);
        Assert.Contains("DockMotionBounds.Resting(", host, StringComparison.Ordinal);
        Assert.Contains("DockMotionBounds.Expanded(", host, StringComparison.Ordinal);
        Assert.Contains("GetDpiForWindow", host, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment.Stretch", host, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment.Stretch", host, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRestingSizeIsNotTheExpandedOne()
    {
        var geometry = Code("src/Muralis.Core/Motion/DockStripeGeometry.cs");
        var bounds = Code("src/Muralis.App/UI/Dock/DockMotionBounds.cs");

        // The resting window must not be the expanded one: a window that permanently carried the reserve
        // would leave a band of transparent input area across the bottom of the desktop for as long as the
        // dock was up. The two sizes are separate entry points for exactly that reason.
        Assert.Contains("public static DockStripe Resting(", geometry, StringComparison.Ordinal);
        Assert.Contains("public static DockStripe Expanded(", geometry, StringComparison.Ordinal);

        // Both sizes come from the motion layer, not from numbers chosen in the host.
        Assert.Contains("VerticalReserveFor(IconHostDip)", geometry, StringComparison.Ordinal);
        Assert.Contains("DockMotionEngine.ReserveFor(", geometry, StringComparison.Ordinal);

        // And the window's height is the same in both, which is what keeps the dock's baseline still while
        // the window grows sideways around it.
        Assert.Contains("VerticalReserveDip + IconHostDip + PlatePaddingY", geometry, StringComparison.Ordinal);

        // The app layer is one adapter across the API boundary and holds no geometry of its own.
        Assert.Contains("DockStripeGeometry.Resting(", bounds, StringComparison.Ordinal);
        Assert.Contains("DockStripeGeometry.Expanded(", bounds, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dock is as wide as the apps on it. A hardcoded resting width is what made three pinned apps
    /// reserve a strip sized for twenty, and it is the one regression this test exists to catch.
    /// </summary>
    [Fact]
    public void TheRestingWidthFollowsTheContentRatherThanAConstant()
    {
        var geometry = Code("src/Muralis.Core/Motion/DockStripeGeometry.cs");

        Assert.DoesNotContain("RestingWidthDip =", geometry, StringComparison.Ordinal);
        Assert.Contains("contentWidthDip", geometry, StringComparison.Ordinal);
        Assert.Contains("public static DockStripe Resting(double contentWidthDip", geometry, StringComparison.Ordinal);
        Assert.Contains("public static DockStripe Expanded(double contentWidthDip", geometry, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePointerIsJudgedByGeometryRatherThanByTheWindowUnderIt()
    {
        var coordinator = Code(DockCoordinator);

        // Asking Windows which window owns a pixel has been measured answering "the desktop" while the pointer
        // was drawn over the dock, so it is not a question the dock may ask.
        Assert.DoesNotContain("WindowFromPoint", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAncestor", coordinator, StringComparison.Ordinal);

        // It decides from its own geometry instead.
        Assert.Contains("DockPointerMath.RestingRegion(", coordinator, StringComparison.Ordinal);
        Assert.Contains("DockPointerMath.ExpandedRegion(", coordinator, StringComparison.Ordinal);
        Assert.Contains("_restingScreenRegion", coordinator, StringComparison.Ordinal);
        Assert.Contains("_expandedScreenRegion", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePointerMathKeepsTheCompositionOut()
    {
        var math = Code(PointerMath);

        // The coordinate conversion and the region test are the two things worth testing without a window, so
        // they must stay free of XAML, composition and pointer types.
        Assert.DoesNotContain("Microsoft.UI", math, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows.Foundation", math, StringComparison.Ordinal);
        Assert.DoesNotContain("IntPtr", math, StringComparison.Ordinal);
        Assert.Contains("pixelsPerDip", math, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMagnificationIsStillNotNeighbourTranslation()
    {
        var coordinator = Code(DockCoordinator);

        // Stage B is Scale and Lift. The engine computes a translation, and the coordinator is allowed to spend
        // it, but the wave must not have grown a second motion system of its own.
        Assert.Contains("ApplyMagnification(", coordinator, StringComparison.Ordinal);
        Assert.Contains("DockIconMotion.Release(", coordinator, StringComparison.Ordinal);

        var motion = Code("src/Muralis.App/UI/Motion/DockIconMotion.cs");

        // The policy contains both operations, but callers provide separate visual layers.
        Assert.Contains("layer.Scale = expectedScale", motion, StringComparison.Ordinal);
        Assert.Contains("layer.Translation = expectedOffset", motion, StringComparison.Ordinal);
        Assert.Contains("TransformMotion.Animate(", motion, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDockStillDoesNotOwnTheIconsTransformForDragging()
    {
        // MotionHost's RenderTransform belongs to PinnedZoneDrag and the engine may never write to it.
        var icon = Code("src/Muralis.App/UI/Controls/DockIcon.xaml.cs");
        var motion = Code("src/Muralis.App/UI/Motion/DockIconMotion.cs");

        Assert.Contains("public FrameworkElement MotionTarget => NexusMotionHost;", icon, StringComparison.Ordinal);
        Assert.Contains("MotionHost", icon, StringComparison.Ordinal);

        // The magnification layer is named separately from the host layer, and only the layer is driven.
        Assert.DoesNotContain("MotionHost", motion, StringComparison.Ordinal);
    }

    [Fact]
    public void NexusAndInteractionTransformsHaveDifferentVisualOwners()
    {
        var xaml = File.ReadAllText(Absolute("src/Muralis.App/UI/Controls/DockIcon.xaml"));
        var icon = Code("src/Muralis.App/UI/Controls/DockIcon.xaml.cs");

        Assert.Contains("x:Name=\"MotionHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NexusMotionHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InteractionHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PrepareOrigin(NexusMotionHost)", icon, StringComparison.Ordinal);
        Assert.Contains("SetLocalScale(InteractionHost", icon, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLocalScale(NexusMotionHost", icon, StringComparison.Ordinal);
        Assert.Contains("if (!_nexusMotionActive)", icon, StringComparison.Ordinal);
        Assert.Contains("ResetLocalScale(InteractionHost)", icon, StringComparison.Ordinal);
    }

    /// <summary>
    /// No caller anywhere hands the magnification layer to an interaction motion helper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard for a defect that shipped: <c>PinnedZoneDrag.Begin</c> called
    /// <c>HoverMotion.Exit(_carried.MotionTarget)</c>, and <c>MotionTarget</c> is <c>NexusMotionHost</c>,
    /// which already carries <c>CenterPoint</c> for the magnification. WinUI refuses <c>RenderTransform</c>
    /// on such an element, so the call threw <c>UnauthorizedAccessException</c> out of a <c>PointerMoved</c>
    /// handler on every drag. It was logged 1709 times on this machine and no test noticed.
    /// </para>
    /// <para>
    /// <b>The impact was not a crash, and the earlier wording here said it was.</b> The application catches a
    /// XAML unhandled exception, logs it fatally and continues (<c>App.OnXamlUnhandledException</c> sets
    /// <c>Handled</c>), and that session ran on for a thousand more log lines afterwards. What the user got
    /// was a fatal log entry and a crash dialog per drag, and a gesture that aborted before the drop
    /// indicator was placed. The fix is still correct; the claim that it prevented a process exit was not.
    /// </para>
    /// <para>
    /// The match is on the <em>argument expression</em>, not on a spelling of the call. An earlier version of
    /// this guard listed eight literal <c>…(MotionTarget</c> strings and therefore caught nothing at all: the
    /// real call was <c>HoverMotion.Exit(_carried.MotionTarget)</c>, and <c>HoverMotion.Exit(icon.MotionTarget)</c>
    /// or one routed through a local would have passed too. Any argument expression that names the
    /// magnification layer inside an interaction-motion call is now an offender, whatever it is called on.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoInteractionMotionIsEverDrivenOnTheMagnificationLayer()
    {
        // The name of the receiver that must never be driven by an interaction motion.
        const string MagnificationLayer = "MotionTarget";

        var offenders = new List<string>();
        foreach (var file in SourceFiles("src/Muralis.App"))
        {
            foreach (Match call in InteractionMotionCall.Matches(Code(file)))
            {
                var argument = call.Groups["arg"].Value;
                if (argument.Contains(MagnificationLayer, StringComparison.Ordinal))
                {
                    offenders.Add($"{Relative(file)}: {call.Value.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{MagnificationLayer} is the Nexus layer and carries CenterPoint; an interaction motion on it "
            + "throws UnauthorizedAccessException at runtime. Offender(s): " + string.Join(" | ", offenders));

        // And the element that may be driven that way is still the one exposed for it.
        var icon = Code("src/Muralis.App/UI/Controls/DockIcon.xaml.cs");
        Assert.Contains("public FrameworkElement InteractionLayer => InteractionHost;", icon, StringComparison.Ordinal);

        var drag = Code("src/Muralis.App/UI/Dock/PinnedZoneDrag.cs");
        Assert.Contains("HoverMotion.Exit(_carried.InteractionLayer)", drag, StringComparison.Ordinal);
    }

    /// <summary>
    /// Any interaction-motion invocation, with its first argument captured.
    /// </summary>
    /// <remarks>
    /// Deliberately not tied to a helper's name: <c>SetLocalScale</c> and <c>ResetLocalScale</c> are the
    /// dock's own, the rest are the shared motion helpers, and a new one added tomorrow is covered by the
    /// shape of the call rather than by being listed here.
    /// </remarks>
    private static readonly Regex InteractionMotionCall = new(
        @"\b\w*(?:HoverMotion|PressMotion|SpringMotion|EntranceMotion|TransformMotion)?\.?"
        + @"(?:Enter|Exit|Down|Release|Settle|Run|SetLocalScale|ResetLocalScale|Animate|Set)\s*\(\s*(?<arg>[^;{}]*?)\s*[,)]",
        RegexOptions.Compiled);

    [Fact]
    public void TheDockKeepsItsOwnPointerSurfaceInTheHostLayout()
    {
        var xaml = File.ReadAllText(Absolute(DockHostXaml));

        // The tracker exists so the pointer has somewhere to be seen; the dock's own content is inside the
        // element the motion is drawn in.
        Assert.Contains("x:Name=\"MotionTracker\"", xaml, StringComparison.Ordinal);

        // The stripe is anchored to the bottom, which is what keeps the dock still when the window grows
        // sideways for a wave, and its height is the reserve the motion needs rather than a local number.
        Assert.Contains("VerticalAlignment=\"Bottom\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"{x:Bind DockHeight", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dock's resting width is recomputed from a content change, never from the pointer and never from
    /// a window resize feeding itself. The feedback loop this guards against is
    /// <c>layout → width → SetWindowPos → layout</c>, so the two structural triggers are named explicitly.
    /// </summary>
    [Fact]
    public void TheDockIsResizedByContentChangesAndNotByThePointerPath()
    {
        var host = Code(DesktopDockHost);

        Assert.Contains("ContentSizeChanged", host, StringComparison.Ordinal);
        Assert.Contains("MotionBoundsChanged", host, StringComparison.Ordinal);
        Assert.Contains("DockMotionBounds.Resting(", host, StringComparison.Ordinal);
        Assert.Contains("DockMotionBounds.Expanded(", host, StringComparison.Ordinal);

        // Reading the width is a layout-pass job in the surface, and writing the window is this host's;
        // neither may read a measurement back out of the window it just moved.
        var surface = Code(DockHostCode);
        var report = surface.IndexOf("private void ReportContentWidth()", StringComparison.Ordinal);
        Assert.True(report >= 0, "the dock's measured width must be reported from one named place");
        Assert.Contains("ContentWidthTolerance", surface, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles(string relativeDirectory)
    {
        var directory = Path.Combine(RepoRoot(), relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(directory), $"source directory not found: {directory}");

        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>File contents without comments, so explaining a rule is still allowed.</summary>
    private static string Code(string relativePath)
    {
        var text = File.ReadAllText(Absolute(relativePath));
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\r\n]*", string.Empty);
    }

    private static string Relative(string file) => Path.GetRelativePath(RepoRoot(), file);

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
