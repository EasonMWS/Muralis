using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muralis.App.UI.Controls;
using Muralis.App.UI.Materials;
using Muralis.App.UI.Motion;
using Muralis.Core.Abstractions;
using Muralis.Core.Diagnostics;
using Muralis.Core.DockShell;
using Muralis.Core.Models;
using Muralis.Core.Motion;
using Muralis.Desktop.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Muralis.App.UI.Dock;

/// <summary>
/// The Muralis Dock's surface: pure transparent, icon-only, bottom-centred, and as wide as the apps
/// pinned to it.
/// </summary>
/// <remarks>
/// <para>
/// The dock draws the pinned applications and nothing else. The Desktop Shelf and the utility items are
/// live services elsewhere in the product, but they are not part of this presentation, so there is no
/// Shelf scroller, no separator, no add tile and no label in this tree.
/// </para>
/// <para>
/// It owns no state. Every click is handed to the presenter, and the zone is redrawn from the saved
/// list, so no window of this app ever builds a command line or writes a settings file.
/// </para>
/// <para>
/// It reports two things outward and takes nothing: the width of the icons it is actually drawing, as a
/// <see cref="ContentSizeChanged"/> event, and whether the pointer is on it, as
/// <see cref="MotionBoundsChanged"/>. Turning either into a window size is the host's business.
/// </para>
/// </remarks>
public sealed partial class DockHost : UserControl
{
    public static readonly DependencyProperty PinnedAppsProperty = DependencyProperty.Register(
        nameof(PinnedApps), typeof(PinnedAppsPresenter), typeof(DockHost), new PropertyMetadata(null, OnPinnedAppsChanged));
    public static readonly DependencyProperty BackgroundStyleProperty = DependencyProperty.Register(
        nameof(BackgroundStyle),
        typeof(DockBackgroundStyle),
        typeof(DockHost),
        new PropertyMetadata(DockBackgroundStyle.Transparent, OnBackgroundStyleChanged));

    /// <summary>
    /// How tall the dock's own box is: the room a magnified icon grows into, plus the icon box, plus the
    /// feet. Shared with the layer that sizes the window, because a stripe measured in two places is a
    /// stripe that disagrees with itself.
    /// </summary>
    public static readonly DependencyProperty DockHeightProperty = DependencyProperty.Register(
        nameof(DockHeight),
        typeof(double),
        typeof(DockHost),
        new PropertyMetadata(0d));

    /// <summary>A panel width below which nothing has changed enough to be worth a window resize, in DIP.</summary>
    private const double ContentWidthTolerance = 0.5;

    private readonly List<FrameworkElement> _motionTargets = [];
    private PinnedAppsPresenter? _observed;
    private PinnedZoneDrag? _drag;
    private DockLayoutMeter? _meter;
    private DockMotionCoordinator? _motion;
    private double _lastReportedWidth = double.NaN;
    private bool _reportedOnce;

    public DockHost()
    {
        InitializeComponent();

        // The stripe's height is a constant of the product, not a measurement: it is set before anything is
        // laid out so the dock never draws once at a height it will not keep.
        DockHeight = DockStripeGeometry.StripeHeightDip;

        Loaded += (_, _) => OnLoaded();
        Unloaded += (_, _) => OnUnloaded();

        // Nothing of the profiler exists unless this process was asked to record: no timeline, no
        // subscription to layout, and the drop path below is one bool read per stage.
        if (DropProfile.IsEnabled)
        {
            _meter = new DockLayoutMeter(this);
        }
    }

    /// <summary>How tall the dock's own box is. See <see cref="DockStripeGeometry.StripeHeightDip"/>.</summary>
    public double DockHeight
    {
        get => (double)GetValue(DockHeightProperty);
        private set => SetValue(DockHeightProperty, value);
    }

    /// <summary>
    /// The magnification, when it is running. Null means the dock is drawing plain static icons, which is
    /// what it does if the engine could not be started: the motion is an enhancement and never a
    /// precondition for the dock existing.
    /// </summary>
    internal DockMotionCoordinator? Motion
    {
        get
        {
            if (_motion is not null)
            {
                return _motion;
            }

            try
            {
                _motion = new DockMotionCoordinator(
                    this,
                    MotionTracker,
                    PinnedZone,
                    Drag,
                    () => WindowHandle,
                    RawPointerBroker);
            }
            catch (Exception ex)
            {
                // A dock without magnification is still a dock. Losing the motion must not lose the icons,
                // and the reason it was lost is recorded, because a motion that is silently absent looks
                // exactly like a pointer that never moved.
                if (DropProfile.IsEnabled)
                {
                    DropProfile.Event(
                        "motion.unavailable",
                        "\"error\":\"" + ex.GetType().Name + ": "
                        + ex.Message.Replace("\\", "/", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal)
                        + "\"");
                }

                return null;
            }

            return _motion;
        }
    }

    /// <summary>How many icons the magnification can drive at once.</summary>
    internal int MaxMotionIcons => PinnedApps?.MaximumCount ?? 0;

    /// <summary>
    /// The native window this dock is drawn in, or zero before it exists.
    /// </summary>
    /// <remarks>
    /// Handed in by the host that owns the window, because where the window is on screen is what turns the
    /// cursor's screen position into a position in the dock's own space -and the window's own answer is the
    /// only correct one for a surface that is not the top-level window of an application.
    /// </remarks>
    internal nint WindowHandle { get; set; }

    /// <summary>The neutral process-level pointer source supplied by the application host.</summary>
    internal RawPointerBroker? RawPointerBroker { get; set; }

    private void OnLoaded()
    {
        ApplyBackgroundStyle();

        // The pointer tells the dock when to hold the room the magnification needs, so the subscription comes
        // before the motion starts: the first move must not arrive with nobody listening.
        if (Motion is { } motion)
        {
            motion.PointerInsideChanged += OnPointerInsideChanged;
            motion.Start();
        }

        // The width the icons occupy is only known once they have been laid out. It is read from the layout
        // pass and reported only when it has actually changed, so a pass that changes nothing costs a
        // comparison and never a window resize.
        LayoutUpdated += OnContentLayoutUpdated;
        ReportContentWidth();
    }

    private void OnUnloaded()
    {
        LayoutUpdated -= OnContentLayoutUpdated;

        if (_motion is not null)
        {
            _motion.PointerInsideChanged -= OnPointerInsideChanged;
            _motion.Stop();
        }
    }

    /// <summary>
    /// Releases the magnification for good: the pointer subscription, the layout subscription and the
    /// dispatcher hand-off all go with the coordinator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OnUnloaded"/> stops the motion but deliberately keeps the coordinator, because a control that
    /// is unloaded can be loaded again — a re-parent moves it through the same events — and rebuilding the
    /// cached icon centres for that would be work thrown away. This is the other half: the window that owned
    /// the dock is going away, so the coordinator is disposed and dropped.
    /// </para>
    /// <para>
    /// Called by the host from the same place it unhooks its own handlers, and safe to call more than once and
    /// after the motion was never created: <see cref="DockMotionCoordinator.Dispose"/> and its <c>Stop</c> are
    /// both guarded, so a second call is a no-op rather than a double unsubscribe.
    /// </para>
    /// </remarks>
    internal void ReleaseMotion()
    {
        // The layout meter watches the same lifetime and is released with it, so nothing of the dock's
        // instrumentation outlives the window either.
        _meter?.Dispose();
        _meter = null;

        if (_motion is null)
        {
            return;
        }

        _motion.PointerInsideChanged -= OnPointerInsideChanged;
        _motion.Dispose();
        _motion = null;
    }

    private void OnContentLayoutUpdated(object? sender, object args)
    {
        try
        {
            ReportContentWidth();
        }
        catch (Exception)
        {
            // The width is what the window is sized from. A dock that cannot measure itself still draws
            // its icons, and a layout pass that throws would take the whole dock with it.
        }
    }

    /// <summary>
    /// The width of the pin run, in DIP, or zero when nothing is pinned. Zero is a real answer: the
    /// window is sized to fit it, so an empty dock collapses rather than leaving an empty plate on the
    /// desktop.
    /// </summary>
    private double ContentWidth()
    {
        var run = ContentRun;
        return run is null || !double.IsFinite(run.ActualWidth) || run.ActualWidth <= 0
            ? 0
            : run.ActualWidth;
    }

    /// <summary>The panel the icons live on, which is what the dock measures itself by.</summary>
    private FrameworkElement? ContentRun => PinnedZone.Children.Count > 0 ? PinnedZone.Children[0] as FrameworkElement : null;

    /// <summary>
    /// Reports the run's width when it has changed. Structural only: this is driven by layout, not by the
    /// pointer, and a value inside the tolerance is not a change -which is what keeps a window resize from
    /// feeding itself a slightly different measurement and resizing again.
    /// </summary>
    private void ReportContentWidth()
    {
        var width = ContentWidth();
        if (_reportedOnce && Math.Abs(width - _lastReportedWidth) < ContentWidthTolerance)
        {
            return;
        }

        _reportedOnce = true;
        _lastReportedWidth = width;

        // Also reported from the one event that always follows a pin being added, removed or reordered, so
        // the window is resized by the change itself rather than by whatever the layout pass happens to do.
        ContentSizeChanged?.Invoke(this, width);
    }

    /// <summary>
    /// The pointer came onto the dock, or left it. The host spends the bounds; this only reports.
    /// </summary>
    private void OnPointerInsideChanged(object? sender, bool inside)
    {
        _motionExpanded = inside;
        MotionBoundsChanged?.Invoke(this, inside);
    }

    /// <summary>Whether the dock is currently holding the room the magnification needs.</summary>
    internal bool IsMotionExpanded => _motionExpanded;

    /// <summary>
    /// Raised when the dock wants a different amount of room, with true for the expanded size. The only
    /// channel through which the pointer can change the window, and it fires on the two transitions only.
    /// </summary>
    internal event EventHandler<bool>? MotionBoundsChanged;

    /// <summary>Raised when the width of the pin run has changed, in DIP. Structural, never per pointer report.</summary>
    internal event EventHandler<double>? ContentSizeChanged;

    private bool _motionExpanded;

    /// <summary>The Pinned Apps zone: the saved applications and everything the user can do to them.</summary>
    public PinnedAppsPresenter? PinnedApps
    {
        get => (PinnedAppsPresenter?)GetValue(PinnedAppsProperty);
        set => SetValue(PinnedAppsProperty, value);
    }

    /// <summary>
    /// The dock's appearance variant. <see cref="DockBackgroundStyle.Transparent"/> is the product
    /// default and paints no dock-wide surface at all; <see cref="DockBackgroundStyle.Glass"/> paints the
    /// material plate behind the icons. Neither changes whether the dock exists, what is on it, or how it
    /// is sized.
    /// </summary>
    public DockBackgroundStyle BackgroundStyle
    {
        get => (DockBackgroundStyle)GetValue(BackgroundStyleProperty);
        set => SetValue(BackgroundStyleProperty, value);
    }

    /// <summary>
    /// Targets reserved for pointer-distance magnification, neighbor influence, spring return,
    /// auto-hide/edge reveal, and surface expansion. Phase 4B intentionally applies none of them.
    /// </summary>
    public IReadOnlyList<FrameworkElement> MotionTargets => _motionTargets;

    /// <summary>Something the user should be told about a pin, in the user's language. Never a crash.</summary>
    public event EventHandler<string>? PinnedNotice;

    private static void OnBackgroundStyleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((DockHost)dependencyObject).ApplyBackgroundStyle();

    /// <summary>
    /// Paints the background variant, or paints nothing.
    /// </summary>
    /// <remarks>
    /// Transparent is a branch and not a colour. The plate is given no brush, no border and no shadow, so
    /// the dock has no dock-wide surface in any theme: dark, light and system all resolve to the wallpaper
    /// showing through. Routing this through a nearly-transparent theme colour would leave the surface
    /// alive and one token change away from coming back.
    /// </remarks>
    private void ApplyBackgroundStyle()
    {
        if (DockPlate is null)
        {
            return;
        }

        if (BackgroundStyle == DockBackgroundStyle.Glass)
        {
            if (Application.Current.Resources["MuralisGlassLowBorderStyle"] is Style plate)
            {
                DockPlate.Style = plate;
            }

            // Kept alongside the style because the plate's padding is geometry, not appearance: the icons
            // must sit in the same place whichever variant is painted.
            DockPlate.Padding = new Thickness(
                DockStripeGeometry.PlatePaddingX,
                DockStripeGeometry.PlatePaddingY,
                DockStripeGeometry.PlatePaddingX,
                DockStripeGeometry.PlatePaddingY);
            return;
        }

        DockPlate.Style = null;
        DockPlate.Background = null;
        DockPlate.BorderBrush = null;
        DockPlate.BorderThickness = new Thickness(0);
        DockPlate.Shadow = null;
        DockPlate.Padding = new Thickness(
            DockStripeGeometry.PlatePaddingX,
            DockStripeGeometry.PlatePaddingY,
            DockStripeGeometry.PlatePaddingX,
            DockStripeGeometry.PlatePaddingY);
    }

    private static void OnPinnedAppsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var host = (DockHost)dependencyObject;
        host.DetachFromPresenter(args.OldValue as PinnedAppsPresenter);
        host.AttachToPresenter(args.NewValue as PinnedAppsPresenter);
    }

    private void AttachToPresenter(PinnedAppsPresenter? presenter)
    {
        if (presenter is null)
        {
            return;
        }

        _observed = presenter;
        presenter.Projected += OnPinnedAppsProjected;
    }

    private void DetachFromPresenter(PinnedAppsPresenter? presenter)
    {
        if (presenter is not null && ReferenceEquals(presenter, _observed))
        {
            presenter.Projected -= OnPinnedAppsProjected;
            _observed = null;
        }
    }

    private void OnPinnedAppsProjected(object? sender, EventArgs args)
    {
        using (DropProfile.Measure("host.projected"))
        {
            _drag?.OnProjected();

            // The list has just been rewritten, which is the structural change the dock's width follows.
            // Reported here rather than left to the next layout pass so the window is resized once, by the
            // change, and not by whatever the layout happens to do afterwards.
            ReportContentWidth();
        }

        // Last thing before the layout pass the new order forces, so this is where the outside timing of
        // that pass starts.
        _meter?.Arm();
    }

    /// <summary>
    /// The drag, built the first time one starts. It holds no state of its own beyond the pointer it
    /// is following, so there is nothing to keep alive between drags.
    /// </summary>
    private PinnedZoneDrag Drag => _drag ??= new PinnedZoneDrag(PinnedZone, PinnedDropIndicator, CommitMoveAsync);

    private Task CommitMoveAsync(PinnedAppViewItem item, int targetIndex) =>
        PinnedApps?.MoveAsync(item.Id, targetIndex) ?? Task.CompletedTask;

    private void OnPinnedPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is DockIcon icon)
        {
            Drag.Press(PinnedIcons(), icon, args);
        }
    }

    private void OnPinnedPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (Drag.Move(args))
        {
            args.Handled = true;
        }
    }

    private void OnPinnedPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_drag is { Travelled: true })
        {
            args.Handled = true;
        }

        _ = Drag.ReleaseAsync();
    }

    private void OnPinnedPointerCanceled(object sender, PointerRoutedEventArgs args) => _drag?.Cancel();

    /// <summary>
    /// The pinned icons, in the order the presenter holds them, which is the order the saved list is
    /// in. A zone whose containers are not all realized is not dragged at all rather than dragged
    /// against indices that do not line up with it.
    /// </summary>
    private IReadOnlyList<DockIcon> PinnedIcons()
    {
        if (PinnedApps is null)
        {
            return [];
        }

        var found = new Dictionary<PinnedAppViewItem, DockIcon>();
        CollectPinnedIcons(PinnedZone, found);
        if (found.Count != PinnedApps.Items.Count)
        {
            return [];
        }

        var icons = new List<DockIcon>(found.Count);
        foreach (var item in PinnedApps.Items)
        {
            if (!found.TryGetValue(item, out var icon))
            {
                return [];
            }

            icons.Add(icon);
        }

        return icons;
    }

    private static void CollectPinnedIcons(DependencyObject node, Dictionary<PinnedAppViewItem, DockIcon> found)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is DockIcon { Tag: PinnedAppViewItem item } icon)
            {
                found[item] = icon;
                continue;
            }

            CollectPinnedIcons(child, found);
        }
    }

    private void OnDockIconLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is DockIcon icon && !_motionTargets.Contains(icon.MotionTarget))
        {
            _motionTargets.Add(icon.MotionTarget);
            icon.Unloaded += OnDockIconUnloaded;
        }
    }

    private void OnDockIconUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is DockIcon icon)
        {
            _motionTargets.Remove(icon.MotionTarget);
            icon.Unloaded -= OnDockIconUnloaded;
        }
    }

    private void OnPinnedItemTapped(object sender, TappedRoutedEventArgs args)
    {
        // A tap is also how a drag ends, so the one the pointer was carrying must not launch.
        if (_drag is { Travelled: true })
        {
            args.Handled = true;
            return;
        }

        if (sender is not DockIcon { Tag: PinnedAppViewItem item } || PinnedApps is null)
        {
            return;
        }

        args.Handled = true;
        _ = LaunchAsync(item);
    }

    private async void OnPinnedOpen(object sender, RoutedEventArgs args)
    {
        if (ItemOf(sender) is { } item && PinnedApps is not null)
        {
            await LaunchAsync(item);
        }
    }

    private async void OnPinnedOpenLocation(object sender, RoutedEventArgs args)
    {
        if (ItemOf(sender) is not { } item || PinnedApps is null)
        {
            return;
        }

        if (!await PinnedApps.RevealAsync(item.Id))
        {
            PinnedNotice?.Invoke(this, item.Warning ?? item.LaunchTarget);
        }
    }

    private async void OnPinnedUnpin(object sender, RoutedEventArgs args)
    {
        if (ItemOf(sender) is not { } item || PinnedApps is null)
        {
            return;
        }

        if (await PinnedApps.UnpinAsync(item.Id) is { } notice)
        {
            PinnedNotice?.Invoke(this, notice);
        }
    }

    /// <summary>Accepting a drop is the same gesture as adding through the picker, and takes the same path.</summary>
    private void OnPinnedDragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = PinnedApps is not null && args.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        args.Handled = true;
    }

    private async void OnPinnedDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (PinnedApps is null)
        {
            return;
        }

        var result = await PinnedApps.AddAsync(await DroppedPathAsync(args) ?? string.Empty);
        if (PinnedApps.DescribeRefusal(result) is { } notice)
        {
            PinnedNotice?.Invoke(this, notice);
        }
    }

    private async Task LaunchAsync(PinnedAppViewItem item)
    {
        if (PinnedApps is null)
        {
            return;
        }

        var result = await PinnedApps.LaunchAsync(item.Id);
        if (result.Outcome != ApplicationLaunchOutcome.Launched)
        {
            PinnedNotice?.Invoke(this, item.Warning ?? result.Error ?? item.DisplayName);
        }
    }

    private static PinnedAppViewItem? ItemOf(object sender) =>
        (sender as FrameworkElement)?.Tag as PinnedAppViewItem;

    /// <summary>
    /// The first dropped file the dock knows how to pin. A drop can carry anything at all, so this
    /// returns null rather than picking something the user did not mean.
    /// </summary>
    private static async Task<string?> DroppedPathAsync(DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return null;
        }

        foreach (var item in await args.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFile file && PinnedAppTargets.IsSupported(file.Path))
            {
                return file.Path;
            }
        }

        return null;
    }
}
