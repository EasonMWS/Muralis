using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muralis.App.UI.Controls;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Muralis.App.UI.Dock;

/// <summary>
/// Composition-ready shell for the new Dock. It has no dependency on DesktopCanvas or the Phase 3
/// takeover; consumers provide a presenter for the pinned zone, items for the Shelf, and a future
/// motion engine can drive every <see cref="DockIcon.MotionTarget"/> exposed by
/// <see cref="MotionTargets"/>.
/// </summary>
/// <remarks>
/// The pinned zone is the only zone the dock lets the user change, and even here the dock owns no
/// state: every click is handed to the presenter, and the zone is redrawn from the saved list. A
/// launch, a pin and an unpin all go through a contract, so no window of this app ever builds a
/// command line or writes a settings file.
/// </remarks>
public sealed partial class DockHost : UserControl
{
    public static readonly DependencyProperty PinnedAppsProperty = DependencyProperty.Register(
        nameof(PinnedApps), typeof(PinnedAppsPresenter), typeof(DockHost), new PropertyMetadata(null, OnPinnedAppsChanged));
    public static readonly DependencyProperty ShelfItemsProperty = DependencyProperty.Register(
        nameof(ShelfItems), typeof(IEnumerable), typeof(DockHost), new PropertyMetadata(Array.Empty<object>()));
    public static readonly DependencyProperty UtilityItemsProperty = DependencyProperty.Register(
        nameof(UtilityItems), typeof(IEnumerable), typeof(DockHost), new PropertyMetadata(Array.Empty<object>()));

    private readonly List<FrameworkElement> _motionTargets = [];
    private PinnedAppsPresenter? _observed;
    private PinnedZoneDrag? _drag;

    public DockHost() => InitializeComponent();

    /// <summary>The Pinned Apps zone: the saved applications and everything the user can do to them.</summary>
    public PinnedAppsPresenter? PinnedApps
    {
        get => (PinnedAppsPresenter?)GetValue(PinnedAppsProperty);
        set => SetValue(PinnedAppsProperty, value);
    }

    public IEnumerable ShelfItems
    {
        get => (IEnumerable)GetValue(ShelfItemsProperty);
        set => SetValue(ShelfItemsProperty, value);
    }

    public IEnumerable UtilityItems
    {
        get => (IEnumerable)GetValue(UtilityItemsProperty);
        set => SetValue(UtilityItemsProperty, value);
    }

    /// <summary>
    /// Targets reserved for pointer-distance magnification, neighbor influence, spring return,
    /// auto-hide/edge reveal, and surface expansion. Phase 4B intentionally applies none of them.
    /// </summary>
    public IReadOnlyList<FrameworkElement> MotionTargets => _motionTargets;

    /// <summary>The Shelf asks to open an item.</summary>
    public event EventHandler<DesktopShelfItem>? ShelfItemInvoked;

    /// <summary>The user asked to pin something. Opening the picker is the host's business, not the dock's.</summary>
    public event EventHandler? PinnedAddRequested;

    /// <summary>Something the user should be told about a pin, in the user's language. Never a crash.</summary>
    public event EventHandler<string>? PinnedNotice;

    private static void OnPinnedAppsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var host = (DockHost)dependencyObject;
        host.DetachFromPresenter(args.OldValue as PinnedAppsPresenter);
        host.AttachToPresenter(args.NewValue as PinnedAppsPresenter);
        host.UpdatePinnedZone();
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
        _drag?.OnProjected();
        UpdatePinnedZone();
    }

    /// <summary>The add slot tells the truth about the zone: hidden when there is no presenter to add to,
    /// dimmed once the zone is full, so a dock that cannot take another pin looks like one.</summary>
    private void UpdatePinnedZone()
    {
        AddAppTile.Visibility = PinnedApps is null ? Visibility.Collapsed : Visibility.Visible;
        AddAppTile.Opacity = PinnedApps is { IsFull: true } ? 0.5 : 1;
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

    private void OnShelfItemTapped(object sender, TappedRoutedEventArgs args)
    {
        if (sender is not DockIcon { DataContext: DesktopShelfViewItem selected })
        {
            return;
        }

        foreach (var item in ShelfItems.OfType<DesktopShelfViewItem>())
        {
            item.IsSelected = ReferenceEquals(item, selected);
        }
    }

    private void OnShelfItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is DockIcon { DataContext: DesktopShelfViewItem selected })
        {
            ShelfItemInvoked?.Invoke(this, selected.Item);
            args.Handled = true;
        }
    }

    /// <summary>
    /// Mouse wheels over the Shelf become immediate horizontal movement. Precision touchpads keep the
    /// ScrollViewer's native direct-manipulation path; no lerp or catch-up animation is introduced.
    /// </summary>
    private void OnShelfPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var delta = args.GetCurrentPoint(ShelfScroller).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var target = Math.Clamp(
            ShelfScroller.HorizontalOffset - delta,
            0,
            ShelfScroller.ScrollableWidth);
        ShelfScroller.ChangeView(target, null, null, disableAnimation: true);
        args.Handled = true;
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

    private void OnPinnedAddRequested(object sender, EventArgs args) =>
        PinnedAddRequested?.Invoke(this, EventArgs.Empty);

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
