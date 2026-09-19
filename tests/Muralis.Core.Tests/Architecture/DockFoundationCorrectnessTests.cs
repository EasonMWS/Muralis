using Xunit;

namespace Muralis.Core.Tests.Architecture;

public sealed class DockFoundationCorrectnessTests
{
    [Fact]
    public void DockWindow_IsPassiveAndNeverPermanentTopmost()
    {
        var host = Source("src/Muralis.App/UI/Dock/DesktopDockHost.cs");

        Assert.Contains("presenter.IsAlwaysOnTop = false", host, StringComparison.Ordinal);
        Assert.DoesNotContain("presenter.IsAlwaysOnTop = true", host, StringComparison.Ordinal);
        Assert.Contains("WsExNoActivate", host, StringComparison.Ordinal);
        Assert.Contains("WsExToolWindow", host, StringComparison.Ordinal);
        Assert.DoesNotContain("SetParent(", host, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopWorkerWindow", host, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeService_SynchronizesEveryWindowRoot()
    {
        var theme = Source("src/Muralis.App/Services/ThemeService.cs");
        var host = Source("src/Muralis.App/UI/Dock/DesktopDockHost.cs");

        Assert.Contains("RegisterRoot", theme, StringComparison.Ordinal);
        Assert.Contains("root.RequestedTheme = requested", theme, StringComparison.Ordinal);
        Assert.Contains("_theme.RegisterRoot(_windowRoot)", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground = new SolidColorBrush", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void Appearance_IsVisualPolicyAndDefaultsTransparent()
    {
        var settings = Source("src/Muralis.Core/Models/DockSettings.cs");
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml.cs");

        Assert.Contains("BackgroundStyle { get; set; } = DockBackgroundStyle.Transparent", settings, StringComparison.Ordinal);
        Assert.Contains("DockBackgroundStyle.Transparent", host, StringComparison.Ordinal);

        // Transparent is a presentation branch, not a colour: the plate is given no brush, no border and no
        // shadow at all, in every theme. Routing it through a nearly-transparent theme token would leave the
        // dock-wide surface alive and one token change away from coming back.
        Assert.Contains("DockPlate.Background = null", host, StringComparison.Ordinal);
        Assert.Contains("DockPlate.BorderBrush = null", host, StringComparison.Ordinal);
        Assert.Contains("DockPlate.Shadow = null", host, StringComparison.Ordinal);
        Assert.Contains("DockBackgroundStyle.Glass", host, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible = false", host, StringComparison.Ordinal);
        Assert.DoesNotContain("using Muralis.Core.Canvas", host, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hot path is the one the pointer drives, and since the dock became icon-only the only thing left
    /// on it is the magnification write. It may not measure, walk the tree, read settings or size anything.
    /// </summary>
    [Fact]
    public void PointerHotPath_DoesNotMeasurePersistOrResize()
    {
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml.cs");
        var start = host.IndexOf("private void OnPinnedPointerMoved", StringComparison.Ordinal);
        var end = host.IndexOf("private void OnPinnedPointerCanceled", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the dock's pointer handlers must stay explicit and reviewable");

        var hotPath = host[start..end];
        foreach (var forbidden in new[]
                 {
                     "RequestedTheme", "Settings", "GetAsync", "LoadIcon", "PinnedApps",
                     "ActualWidth", "ActualHeight", "MoveAndResize", "SetWindowPos", "Margin",
                 })
        {
            Assert.DoesNotContain(forbidden, hotPath, StringComparison.Ordinal);
        }

        // And the resting width is reported from the layout pass and from a projected list, never from a
        // pointer handler: a resize driven by the pointer is the feedback loop this dock must not have.
        Assert.Contains("OnContentLayoutUpdated", host, StringComparison.Ordinal);
        Assert.Contains("OnPinnedAppsProjected", host, StringComparison.Ordinal);
        Assert.Contains("ContentSizeChanged", host, StringComparison.Ordinal);
    }

    /// <summary>
    /// The magnification's lifetime ends when the window that owned it ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator holds three things that outlive it if nobody lets them go: the <c>LayoutUpdated</c>
    /// handler on the host, the process-wide raw-pointer subscription, and the queued dispatcher hand-off.
    /// Stopping it is not the same as disposing it — a control that unloads may load again — so the window
    /// closing is the moment the coordinator has to be released, and this records that the path exists and is
    /// reachable.
    /// </para>
    /// <para>
    /// Bounds, stated rather than implied: this is a source-level guard, because no test project in this
    /// repository references <c>Muralis.App</c> and the coordinator cannot be instantiated without a XAML
    /// window. It fails when the release path or its idempotence is removed; it cannot by itself prove the
    /// subscription count returned to zero at runtime.
    /// </para>
    /// </remarks>
    [Fact]
    public void MotionLifetime_IsReleasedWhenTheDockWindowCloses()
    {
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml.cs");
        var window = Source("src/Muralis.App/UI/Dock/DesktopDockHost.cs");
        var coordinator = Source("src/Muralis.App/UI/Dock/DockMotionCoordinator.cs");

        // The coordinator is disposable and actually disposes its subscriptions.
        Assert.Contains("class DockMotionCoordinator : IDisposable", coordinator, StringComparison.Ordinal);
        Assert.Contains("_host.LayoutUpdated -= _hooks.OnLayoutUpdated", coordinator, StringComparison.Ordinal);
        Assert.Contains("_pointerSubscription?.Dispose()", coordinator, StringComparison.Ordinal);

        // Disposing twice, or stopping an untracked coordinator, is a no-op rather than a double unsubscribe:
        // both entry points are guarded before they touch anything.
        Assert.Contains("public void Dispose()", coordinator, StringComparison.Ordinal);
        Assert.Contains("if (_disposed)", coordinator, StringComparison.Ordinal);
        Assert.Contains("public void Stop()", coordinator, StringComparison.Ordinal);
        Assert.Contains("if (!_isTracking)", coordinator, StringComparison.Ordinal);
        Assert.Contains("if (_isTracking || _disposed)", coordinator, StringComparison.Ordinal);

        // The dock exposes the release, and unhooks its own handler with it so the coordinator is dropped
        // rather than left reachable through the host.
        Assert.Contains("internal void ReleaseMotion()", host, StringComparison.Ordinal);
        Assert.Contains("_motion.PointerInsideChanged -= OnPointerInsideChanged;", host, StringComparison.Ordinal);
        Assert.Contains("_motion.Dispose();", host, StringComparison.Ordinal);
        Assert.Contains("_motion = null;", host, StringComparison.Ordinal);

        // And the window that owns the dock calls it while tearing down — from the same block that already
        // unhooks the window's own handlers, which is the place that runs exactly once per window lifetime.
        var close = window.IndexOf("public Task CloseAsync", StringComparison.Ordinal);
        Assert.True(close >= 0, "the dock host must keep one place that closes the window");
        var closeBody = window[close..];
        Assert.Contains("_dock.ReleaseMotion();", closeBody, StringComparison.Ordinal);
        Assert.Contains("_pinnedPresenter?.Detach();", closeBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every subscription the Dock's configuration surface makes is given back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The section's view model is transient and the settings page is not cached, so a subscription that is
    /// never released accumulates one more listener per visit. Each of the three is asserted as a pair here,
    /// because the failure mode is exactly a missing half: an <c>+=</c> with no <c>-=</c> compiles, passes
    /// every behavioural test, and leaks.
    /// </para>
    /// <para>
    /// Bounds: a source-level guard, for the same reason as the lifetime test above, plus one that is
    /// specific to it — a row's forwarding subscription can only be observed through an item that raises
    /// <c>PropertyChanged</c>, and building that item needs the shell icon provider.
    /// </para>
    /// </remarks>
    [Fact]
    public void DockConfiguration_EverySubscriptionIsReleased()
    {
        var viewModel = Source("src/Muralis.App/ViewModels/DockPinnedAppsViewModel.cs");
        var settings = Source("src/Muralis.App/ViewModels/SettingsViewModel.cs");

        // The row forwards its item's notifications and lets go of them when it is detached.
        Assert.Contains("Item.PropertyChanged += OnItemChanged;", viewModel, StringComparison.Ordinal);
        Assert.Contains("Item.PropertyChanged -= OnItemChanged;", viewModel, StringComparison.Ordinal);
        Assert.Contains("public void Detach()", viewModel, StringComparison.Ordinal);
        Assert.Contains("public void Dispose() => Detach();", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (_detached)", viewModel, StringComparison.Ordinal);

        // Rows are rebuilt on every projection, so the outgoing ones are detached first — otherwise each
        // projection orphans one row per pin, each still holding the view model.
        var projected = viewModel.IndexOf("private void OnProjected", StringComparison.Ordinal);
        Assert.True(projected >= 0);
        var projectedBody = viewModel[projected..viewModel.IndexOf("private async Task LoadIconAsync", projected, StringComparison.Ordinal)];
        Assert.Contains("previous.Detach();", projectedBody, StringComparison.Ordinal);
        Assert.True(
            projectedBody.IndexOf("previous.Detach();", StringComparison.Ordinal)
                < projectedBody.IndexOf("Rows.Clear();", StringComparison.Ordinal),
            "rows must be detached before the collection drops them");

        // The presenter's subscription to the pin service goes back when the page is left, and the settings
        // view model is the one that hands it back.
        Assert.Contains("_presenter.Projected -= OnProjected;", viewModel, StringComparison.Ordinal);
        Assert.Contains("_presenter.Detach();", viewModel, StringComparison.Ordinal);
        Assert.Contains("public override void DetachFromPage()", viewModel, StringComparison.Ordinal);

        Assert.Contains("_dockPins.Reported += OnDockPinsReported;", settings, StringComparison.Ordinal);
        Assert.Contains("_dockPins.Reported -= OnDockPinsReported;", settings, StringComparison.Ordinal);
        Assert.Contains("_dockPins.DetachFromPage();", settings, StringComparison.Ordinal);
    }

    /// <summary>
    /// Loading the dock's icons again does not subscribe the same handler twice.
    /// </summary>
    /// <remarks>
    /// A pinned icon's container is realized, released and realized again as the list changes, so
    /// <c>Loaded</c> repeats. The dock tracks one target per icon and removes it on <c>Unloaded</c>; without
    /// the containment check a re-realized icon would be added twice and its <c>Unloaded</c> handler attached
    /// twice, so the motion would drive the same layer twice per publish.
    /// </remarks>
    [Fact]
    public void RepeatedIconLoad_DoesNotDuplicateMotionTargets()
    {
        var host = Source("src/Muralis.App/UI/Dock/DockHost.xaml.cs");

        Assert.Contains("private void OnDockIconLoaded", host, StringComparison.Ordinal);
        Assert.Contains("!_motionTargets.Contains(icon.MotionTarget)", host, StringComparison.Ordinal);
        Assert.Contains("_motionTargets.Add(icon.MotionTarget);", host, StringComparison.Ordinal);

        Assert.Contains("private void OnDockIconUnloaded", host, StringComparison.Ordinal);
        Assert.Contains("_motionTargets.Remove(icon.MotionTarget);", host, StringComparison.Ordinal);
        Assert.Contains("icon.Unloaded -= OnDockIconUnloaded;", host, StringComparison.Ordinal);
        Assert.Contains("icon.Unloaded += OnDockIconUnloaded;", host, StringComparison.Ordinal);
    }

    private static string Source(string relativePath) => File.ReadAllText(Path.Combine(
        RepoRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Muralis.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Muralis.slnx was not found.");
    }
}
