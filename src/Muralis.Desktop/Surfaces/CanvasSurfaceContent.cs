using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Dock;
using Muralis.Core.Models;
using Muralis.Desktop.Icons;
using Muralis.Desktop.Input;
using Muralis.Desktop.Interop;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// The desktop canvas: a free-form layer of items above the desktop icons that the pointer can
/// reach, and the edge dock the user put some of those items in. It owns what the desktop layer
/// cannot know — where items sit, how they grow as the pointer comes close, how the dock reveals
/// itself and magnifies, how items are dragged between the two — and nothing about the desktop
/// itself: no window creation, no Explorer lifecycle, no display discovery. The shell mounts it on a
/// window it placed above the icons and hands over a target; Explorer restarts simply look like a
/// fresh mount.
/// </summary>
/// <remarks>
/// <para>
/// Input: the window gets a region that covers exactly the pixels the canvas can draw into, so
/// everywhere else the desktop keeps the mouse to itself — clicks on native icons around the
/// canvas are never swallowed, and the cursor crossing the region boundary is what starts and
/// ends hovering. Dragging lifts the region so the item being moved is not clipped.
/// </para>
/// <para>
/// Hovering follows the <see cref="DesktopPointerRouter"/> while one is available: the window region
/// is only as large as what the canvas draws, so on its own the canvas would stop seeing the pointer
/// the moment it crossed a gap between items. The router follows the pointer across the whole
/// desktop layer instead, while the window messages keep working as they always did for the pixels
/// inside the region and for the fallback when there is no router. The router only publishes what is
/// over the desktop layer or over a surface of ours, so a pointer over an ordinary window never
/// reaches the dock at all.
/// </para>
/// <para>
/// The dock: a rail on one display edge that holds items by reference, magnifies the ones near the
/// pointer, and reveals itself from its edge when the pointer comes to it. Its geometry, its
/// magnification and its reveal are all computed in <see cref="Muralis.Core.Dock"/> from the layout's
/// dock options; what happens here is the visuals, the springs and the gestures.
/// </para>
/// <para>
/// Threads: every method runs on the shell thread — mount, unmount, geometry changes, the window
/// messages forwarded by the surface host, the router's events (raised on the same thread) and the
/// dock's one-shot timer. Everything the pointer does is event-driven: springs are handed to the
/// composition engine, nothing polls, and a canvas that is not being touched does no work at all.
/// </para>
/// <para>
/// Icons: an item starts as its tile and glyph and gains the real icon as soon as the icon cache
/// has one. Reading an icon is the cache's business and happens off this thread; what the canvas
/// decides is which items have an icon of their own and at what size it is read.
/// </para>
/// <para>
/// Gestures: on the canvas one click picks an item out and a second one close enough in place and
/// time opens it; the dock opens an item on a single click, because a rail is a launcher and
/// waiting for a second click there would only make it feel slow. A press that ever wanders beyond
/// the system's drag rectangle is a drag for good — a drag never opens anything, and what it moves
/// depends on where the item lives: a canvas item can be dropped on the dock and a dock item can be
/// reordered along it or dragged out onto the canvas.
/// </para>
/// </remarks>
internal sealed class CanvasSurfaceContent : ISurfaceContent, ISurfaceMessageSink
{
    /// <summary>The dock's one-shot timer on the surface window; the shell's own timer lives on no window.</summary>
    private const int DockTimerId = 2;

    /// <summary>How much of itself an item shows when its target is gone; the badge says the rest.</summary>
    private const float MissingOpacity = 0.5f;

    /// <summary>Pixel margin around an item's largest possible extent in the window region.</summary>
    private const int RegionSlackPixels = 2;

    /// <summary>An offset change smaller than this is not worth a new animation.</summary>
    private const double OffsetEpsilonDip = 0.25;

    /// <summary>A hover change smaller than this is not worth a new animation.</summary>
    private const double HoverEpsilon = 0.0005;

    /// <summary>How long, in dock spring periods, the rail is treated as still moving.</summary>
    private const double RailSettlePeriods = 2.5;

    private static readonly Color RailFill = Color.FromArgb(150, 28, 32, 40);

    private readonly DesktopLayout _layout;
    private readonly DesktopLayoutStore _store;
    private readonly ILogger _logger;
    private readonly DockAutoHide _dock;
    private readonly DesktopPointerRouter? _pointer;
    private readonly IDesktopItemLauncher? _launcher;
    private readonly IconBitmapCache _iconCache;
    private readonly DesktopGestureRecognizer _gestures;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly List<ItemView> _freeViews = [];
    private readonly List<ItemView> _dockViews = [];
    private readonly CanvasUpdateRate _updates = new();

    private nint _window;
    private double _scaleFactor = 1.0;
    private PixelRect _displayBounds;
    private int _mountCount;

    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private IconSurfaceDevice? _iconDevice;
    private ContainerVisual? _root;
    private ContainerVisual? _itemsLayer;
    private ContainerVisual? _rail;
    private ShapeVisual? _railBackdrop;
    private SpringVector3NaturalMotionAnimation? _railSpring;
    private double? _dockSettlesAt;

    /// <summary>The pointer's position along the rail, or null when it is nowhere near the dock.</summary>
    private double? _dockPointerAlongDip;

    private double? _pointerXDip;
    private double? _pointerYDip;
    private bool _trackingLeave;

    private ItemView? _drag;
    private ItemView? _dockDrag;
    private ItemView? _pressed;
    private double _grabXDip;
    private double _grabYDip;
    private double _dockGrabAlongDip;
    private double _dockGrabDepthDip;

    /// <summary>Where the item being carried along the dock would land; null when nothing is being carried.</summary>
    private int? _dockInsertIndex;

    private string? _selectedId;
    private string? _lastLaunchId;
    private string? _lastLaunchOutcome;

    /// <summary>The layout as copies, republished on every change; a reader on another thread sees a whole list.</summary>
    private volatile IReadOnlyList<DesktopItem> _items = [];

    /// <summary>The ids of the items whose targets are gone; null when there are none.</summary>
    private volatile IReadOnlyList<string>? _missingIds;

    private volatile CanvasDiagnosticsSnapshot? _snapshot;

    internal CanvasSurfaceContent(
        DesktopLayout layout,
        DesktopLayoutStore store,
        ILogger logger,
        DesktopPointerRouter? pointer = null,
        IDesktopItemLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _layout = layout;
        _store = store;
        _logger = logger;
        _pointer = pointer;
        _launcher = launcher;
        _dock = new DockAutoHide(layout.Dock);
        _iconCache = new IconBitmapCache(logger);
        _iconCache.BitmapArrived += OnIconArrived;

        // The user's own thresholds, read once: the drag rectangle and the double-click rules that
        // the rest of Windows honours.
        _gestures = DesktopGestureRecognizer.ForThisSystem();
    }

    public SurfaceKind Kind => SurfaceKind.InteractiveOverlay;

    public SurfaceInteraction Interaction => SurfaceInteraction.Pointer;

    public SurfaceActivation Activation => SurfaceActivation.OnClick;

    /// <summary>The canvas as it looked at the last change; readable from any thread. Null while unmounted.</summary>
    internal CanvasDiagnosticsSnapshot? Snapshot() => _snapshot;

    /// <summary>How many times the canvas has been put on the desktop; a fresh mount follows every Explorer restart.</summary>
    internal int MountCount => _mountCount;

    public Task MountAsync(ISurfaceTarget target, CancellationToken cancellationToken)
    {
        if (target is not IWin32SurfaceTarget win32)
        {
            throw new ArgumentException($"The desktop canvas needs a Win32 surface target, not {target.GetType().Name}.", nameof(target));
        }

        UnmountCore();

        // The generation this mount's own work is tagged with: answers and openings that arrive
        // after the next mount belong to views that no longer exist.
        _mountCount++;

        _window = win32.WindowHandle;
        _scaleFactor = target.ScaleFactor > 0 ? target.ScaleFactor : 1.0;
        _displayBounds = target.PixelBounds;

        try
        {
            _compositor = CompositionBootstrap.CreateCompositor();
            _target = CompositionBootstrap.CreateTarget(_compositor, _window);
            BuildTree();
            ApplyLayout();
            UpdateRegion();

            // Icons already resolved come straight back; anything new is asked for and arrives
            // through the work message.
            EnsureIcons();
        }
        catch
        {
            // A mount that failed never got a canvas of its own, so it leaves no generation behind.
            _mountCount--;
            UnmountCore();
            throw;
        }

        _logger.LogInformation(
            "The desktop canvas is showing {Count} items on {Width}x{Height} at {Scale:0.##}x (mount {Mount})",
            _freeViews.Count + _dockViews.Count,
            _displayBounds.Width,
            _displayBounds.Height,
            _scaleFactor,
            _mountCount);

        SubscribeToPointer();

        // The pointer may already be resting on the desktop: one read now starts hover from what is
        // really there instead of waiting for the first move.
        _pointer?.SampleOnce();

        Bump();

        // Work asked for while nothing was mounted — an item added with the canvas off — runs now
        // that there is a canvas to run it against.
        DrainWork();
        RefreshItems();
        return Task.CompletedTask;
    }

    public Task UnmountAsync()
    {
        UnmountCore();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // Order matters: the mount releases its icon surfaces while the compositor is still there,
        // the device goes once no chain is left to belong to it, and the cache outlives them both so
        // a remount never re-reads an icon it already has.
        UnmountAsync().GetAwaiter().GetResult();
        _iconDevice?.Dispose();
        _iconDevice = null;
        _iconCache.BitmapArrived -= OnIconArrived;
        _iconCache.Dispose();
        return ValueTask.CompletedTask;
    }

    public void OnGeometryChanged(MonitorGeometry geometry, double scale)
    {
        if (_window == nint.Zero || _root is null)
        {
            return;
        }

        if (_drag is not null)
        {
            // The display changed under the drag: keep the item where it was put, then follow.
            CommitCanvasDrag(_drag, _drag.CenterXDip, _drag.CenterYDip);
            EndDragCapture();
        }

        _displayBounds = geometry.Bounds;
        _scaleFactor = scale > 0 ? scale : 1.0;
        _pointerXDip = null;
        _pointerYDip = null;
        _dockPointerAlongDip = null;

        ApplyLayout();
        UpdateHover();
        UpdateRegion();
        _logger.LogInformation(
            "The desktop canvas follows the display to {Width}x{Height} at {Scale:0.##}x",
            _displayBounds.Width,
            _displayBounds.Height,
            _scaleFactor);
        Bump();
    }

    /// <summary>Releases the mount. The window itself belongs to the surface host.</summary>
    private void UnmountCore()
    {
        UnsubscribeFromPointer();

        if (_window != nint.Zero)
        {
            NativeMethods.KillTimer(_window, DockTimerId);
        }

        if (_drag is not null)
        {
            CommitCanvasDrag(_drag, _drag.CenterXDip, _drag.CenterYDip);
            EndDragCapture();
        }

        _target?.Dispose();
        _target = null;
        ReleaseMountIcons();
        _compositor?.Dispose();
        _compositor = null;
        _root = null;
        _itemsLayer = null;
        _rail = null;
        _railBackdrop = null;
        _railSpring = null;
        _dockSettlesAt = null;
        _dockPointerAlongDip = null;
        _freeViews.Clear();
        _dockViews.Clear();
        _pressed = null;
        _drag = null;
        _dockDrag = null;
        _dockInsertIndex = null;
        _gestures.Cancel();
        _pointerXDip = null;
        _pointerYDip = null;
        _trackingLeave = false;
        _snapshot = null;
        _window = nint.Zero;
        _missingIds = null;

        // The work queue outlives the mount: everything in it is about the layout or about items by
        // id, both of which survive, and the next mount runs what is left.
    }

    /// <summary>
    /// Frees what the icons of this mount took: each item's surface and the brush drawn from it. The
    /// resolved icons stay in the cache, so the next mount of the same layout is instant.
    /// </summary>
    private void ReleaseMountIcons()
    {
        foreach (var view in _freeViews.Concat(_dockViews))
        {
            view.IconSurface?.Dispose();
            view.IconSurface = null;
            view.IconVisual = null;
        }
    }

    private void BuildTree()
    {
        var compositor = _compositor!;

        _root = compositor.CreateContainerVisual();
        _root.RelativeSizeAdjustment = new Vector2(1, 1);
        _target!.Root = _root;

        _rail = compositor.CreateContainerVisual();
        _railBackdrop = compositor.CreateShapeVisual();
        _rail.Children.InsertAtTop(_railBackdrop);
        _root.Children.InsertAtTop(_rail);

        _itemsLayer = compositor.CreateContainerVisual();
        _itemsLayer.RelativeSizeAdjustment = new Vector2(1, 1);
        _root.Children.InsertAtTop(_itemsLayer);

        _railSpring = Spring(compositor, _layout.Dock.Spring);

        foreach (var item in _layout.Items)
        {
            if (item.IsVisible)
            {
                CreateView(item);
            }
        }

        OrderDockViews();
        OrderFreeViews();
        ApplySelection();

        // Fresh views carry no marks, so the reported set starts empty and follows the answers that
        // come back from the pool.
        RefreshMissingIds();

        // Whether a target still exists is the file system's answer, and asking it is exactly the kind
        // of call the shell thread must not wait for: the items that are gone are marked from the pool.
        CheckEveryItem();
    }

    /// <summary>Builds one item's visuals and puts them where its placement says they belong.</summary>
    private ItemView? CreateView(DesktopItem item)
    {
        var compositor = _compositor;
        var itemsLayer = _itemsLayer;
        var rail = _rail;
        if (compositor is null || itemsLayer is null || rail is null)
        {
            return null;
        }

        var design = CanvasIconLibrary.DesignSize;
        var icon = CanvasIconLibrary.Create(compositor, item.IconKey, design);
        var visual = compositor.CreateContainerVisual();
        visual.Size = new Vector2(design, design);
        visual.CenterPoint = new Vector3(design / 2f, design / 2f, 0);
        visual.Children.InsertAtTop(icon);

        var docked = _layout.IsDocked(item.Id);
        var baseScale = docked ? _layout.Dock.ItemSizeDip / design : item.SizeDip / design;
        var view = new ItemView(item, visual, icon, Spring(compositor, _layout.Motion.Hover), Spring(compositor, _layout.Dock.Spring), baseScale);

        // The authored box is always 96 units; the base scale is what turns it into the item's size.
        visual.Scale = new Vector3((float)baseScale, (float)baseScale, 1);

        if (docked)
        {
            rail.Children.InsertAtTop(visual);
            _dockViews.Add(view);
        }
        else
        {
            itemsLayer.Children.InsertAtTop(visual);
            _freeViews.Add(view);
        }

        RequestIcon(
            view,
            docked ? _layout.Dock.ItemSizeDip : item.SizeDip,
            docked ? _layout.Dock.MaxScale : _layout.Proximity.MaxScale);
        return view;
    }

    /// <summary>
    /// Asks the file system, off the shell thread, which items' targets are gone, and marks them when
    /// the answers come back. A canvas that has just mounted has not got a generation yet, so the
    /// answers are checked against the mount they were asked for.
    /// </summary>
    private void CheckEveryItem()
    {
        if (_freeViews.Count + _dockViews.Count == 0)
        {
            return;
        }

        CheckMissing([.. _freeViews.Concat(_dockViews).Select(view => view.Item)]);
    }

    /// <summary>The same question for the one item the user is touching right now.</summary>
    private void CheckMissingOne(ItemView view) => CheckMissing([view.Item]);

    private void CheckMissing(IReadOnlyList<DesktopItem> items)
    {
        var mount = _mountCount;

        // Copies, so the disk is asked about what the items were while the canvas keeps moving them.
        var copies = items.Select(item => item.Clone()).ToList();
        _ = Task.Run(() =>
        {
            var missing = copies.Where(item => item.IsMissing()).Select(item => item.Id).ToList();
            Post(() =>
            {
                // A mount that came and went while the disk was being asked is not the canvas that
                // asked, and the items of that mount are already gone.
                if (mount == _mountCount)
                {
                    ApplyMissing([.. copies.Select(item => item.Id)], missing);
                }
            });
        });
    }

    /// <summary>
    /// Applies the file system's answer to the items that were asked about: the ones whose targets
    /// are gone are marked, and the ones that are back lose their mark. Only the asked-about items
    /// are touched, so an item nobody asked about keeps what it was showing. A missing target only
    /// ever marks its item — never removes it, never moves it and never rewrites it.
    /// </summary>
    private void ApplyMissing(IReadOnlyCollection<string> checkedIds, IReadOnlyCollection<string> missingIds)
    {
        var changed = false;
        foreach (var view in _freeViews.Concat(_dockViews))
        {
            if (!checkedIds.Contains(view.Item.Id))
            {
                continue;
            }

            if (missingIds.Contains(view.Item.Id))
            {
                if (!view.MissingShown)
                {
                    ShowMissing(view);
                    changed = true;
                }
            }
            else if (view.MissingShown)
            {
                ClearMissing(view);
                changed = true;
            }
        }

        if (changed)
        {
            RefreshMissingIds();
            Bump();
        }
    }

    /// <summary>Dims the item and hangs the warning badge on it; nothing else about it changes.</summary>
    private void ShowMissing(ItemView view)
    {
        view.MissingShown = true;
        view.Visual.Opacity = MissingOpacity;

        var compositor = _compositor;
        if (compositor is not null && view.MissingBadge is null)
        {
            view.MissingBadge = CanvasIconLibrary.MissingBadge(compositor, CanvasIconLibrary.DesignSize);
            view.Visual.Children.InsertAtTop(view.MissingBadge);
        }
    }

    /// <summary>Puts the item back the way it was: full opacity, no badge.</summary>
    private void ClearMissing(ItemView view)
    {
        view.MissingShown = false;
        view.Visual.Opacity = 1.0f;

        if (view.MissingBadge is not null)
        {
            view.Visual.Children.Remove(view.MissingBadge);
            view.MissingBadge = null;
        }
    }

    /// <summary>
    /// Adds an item to the layout and shows it. Called from any thread — the layout and the visuals
    /// belong to the shell thread, so the work travels there through the work queue.
    /// </summary>
    internal void AddItem(DesktopItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Post(() => AddItemCore(item));
    }

    /// <summary>
    /// Adds a whole import at once, in one pass over the layout. Called from any thread; the work
    /// travels to the shell thread like every other change. The items arrive already knowing where
    /// they came from, so each one is placed and shown the same way a single import is, but the
    /// layout is saved and the display region updated once rather than once per item.
    /// </summary>
    internal void AdoptItems(IReadOnlyList<DesktopItem> items)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        var arriving = items.ToArray();
        Post(() => AdoptItemsCore(arriving));
    }

    /// <summary>The display this content is mounted on, in device independent pixels.</summary>
    internal (double WidthDip, double HeightDip) DisplaySizeDip =>
        _displayBounds.Width <= 0 || _scaleFactor <= 0
            ? (0, 0)
            : (_displayBounds.Width / _scaleFactor, _displayBounds.Height / _scaleFactor);

    /// <summary>Removes an item from the layout. The target on disk is not touched; the item is.</summary>
    internal void RemoveItem(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        Post(() => RemoveItemCore(id));
    }

    /// <summary>
    /// Takes on new dock settings — which edge, whether it hides, whether it is on, and what it holds —
    /// and shows them. Called from any thread; the visuals belong to the shell thread.
    /// </summary>
    internal void UpdateDockOptions(DockOptions dock)
    {
        ArgumentNullException.ThrowIfNull(dock);
        var copy = dock.Clone();
        Post(() => UpdateDockOptionsCore(copy));
    }

    /// <summary>
    /// The items as they are right now, copies the caller may keep. Published on the shell thread
    /// whenever the layout changes, so a reader on another thread sees a whole list and never a
    /// halfway-edited one.
    /// </summary>
    internal IReadOnlyList<DesktopItem> Items => _items;

    private void UpdateDockOptionsCore(DockOptions dock)
    {
        if (_root is null)
        {
            // Nothing is mounted: the next mount reads the layout, so there is nothing to show yet.
            return;
        }

        // The same options object the state machine was built with, changed in place: the dock
        // machine and the geometry both read it, so they cannot end up disagreeing about the edge.
        _layout.Dock.CopyFrom(dock);

        ReloadDockMembership();
        ApplyLayout();
        UpdateHover();
        UpdateRegion();
        SaveLayout();
        Bump();
        _logger.LogInformation(
            "The dock now holds {Count} items on the {Edge} edge ({State})",
            _dockViews.Count,
            _layout.Dock.Edge,
            _layout.Dock.Enabled ? "switched on" : "switched off");
    }

    /// <summary>
    /// Puts every item's visuals where the dock says it lives: in the rail for the items the dock
    /// names, on the canvas layer for the rest, each drawn at the size its new home uses. The one
    /// place membership is read from is the layout's own dock, so a hand-edited document and a
    /// settings change end up in exactly the same state.
    /// </summary>
    private void ReloadDockMembership()
    {
        if (_rail is null || _itemsLayer is null)
        {
            return;
        }

        foreach (var view in _freeViews.Concat(_dockViews).ToList())
        {
            var docked = _layout.IsDocked(view.Item.Id);
            var wanted = docked ? _layout.Dock.ItemSizeDip : view.Item.SizeDip;
            if (Math.Abs(wanted - (view.BaseScale * CanvasIconLibrary.DesignSize)) > 0.5)
            {
                view.BaseScale = wanted / CanvasIconLibrary.DesignSize;
                StartScale(view, view.Hover);
            }

            if (docked == _dockViews.Contains(view))
            {
                continue;
            }

            view.Visual.Parent?.Children.Remove(view.Visual);
            if (docked)
            {
                _freeViews.Remove(view);
                _rail.Children.InsertAtTop(view.Visual);
                _dockViews.Add(view);
            }
            else
            {
                _dockViews.Remove(view);
                _itemsLayer.Children.InsertAtTop(view.Visual);
                _freeViews.Add(view);
            }
        }

        OrderDockViews();
        OrderFreeViews();
    }

    private void AddItemCore(DesktopItem item)
    {
        if (_layout.Items.Any(existing => string.Equals(existing.Id, item.Id, StringComparison.Ordinal)))
        {
            return;
        }

        // A new item that is not in the dock gets the free spot closest to the middle of the display,
        // so imports do not land on top of each other. The placer answers in anchor offsets, which is
        // exactly what the layout saves.
        if (!_layout.IsDocked(item.Id) && _displayBounds.Width > 0)
        {
            var (offsetX, offsetY) = DesktopItemPlacer.NextFreeSpot(
                _layout.Items,
                _layout.Dock.DockedItemIds(),
                _displayBounds.Width / _scaleFactor,
                _displayBounds.Height / _scaleFactor,
                item.SizeDip);
            item.OffsetXDip = offsetX;
            item.OffsetYDip = offsetY;
        }

        _layout.Items.Add(item);

        if (item.IsVisible && _root is not null)
        {
            var view = CreateView(item);
            if (view is not null)
            {
                CheckMissingOne(view);
            }
        }

        // A new item may be docked, which moves the rail and every dock slot with it.
        OrderDockViews();
        ApplyLayout();
        RefreshItems();
        SaveLayout();
        UpdateRegion();
        Bump();
    }

    /// <summary>
    /// The bulk of a whole import, done the way one item is: an item already held by id or by the file
    /// it came from is skipped, everything else is placed and shown. The one difference is that the
    /// layout is saved, the region updated and the diagnostics bumped once at the end, because a first
    /// run can bring a hundred items across at the same time.
    /// </summary>
    private void AdoptItemsCore(IReadOnlyList<DesktopItem> items)
    {
        var added = 0;

        foreach (var item in items)
        {
            if (_layout.Items.Any(existing => string.Equals(existing.Id, item.Id, StringComparison.Ordinal)))
            {
                continue;
            }

            // The import is compared by the file each item came from, so a desktop entry that is
            // already on the canvas under a different caption is not put there a second time.
            if (item.SourcePath.Length > 0
                && _layout.Items.Any(existing => string.Equals(existing.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!_layout.IsDocked(item.Id) && _displayBounds.Width > 0)
            {
                var (offsetX, offsetY) = DesktopItemPlacer.NextFreeSpot(
                    _layout.Items,
                    _layout.Dock.DockedItemIds(),
                    _displayBounds.Width / _scaleFactor,
                    _displayBounds.Height / _scaleFactor,
                    item.SizeDip);
                item.OffsetXDip = offsetX;
                item.OffsetYDip = offsetY;
            }

            _layout.Items.Add(item);
            added++;

            if (item.IsVisible && _root is not null)
            {
                var view = CreateView(item);
                if (view is not null)
                {
                    CheckMissingOne(view);
                }
            }
        }

        if (added == 0)
        {
            return;
        }

        OrderDockViews();
        ApplyLayout();
        RefreshItems();
        SaveLayout();
        UpdateRegion();
        Bump();
    }

    private void RemoveItemCore(string id)
    {
        var item = _layout.Items.FirstOrDefault(existing => string.Equals(existing.Id, id, StringComparison.Ordinal));
        var view = _freeViews.Concat(_dockViews).FirstOrDefault(v => string.Equals(v.Item.Id, id, StringComparison.Ordinal));

        if (item is null && view is null)
        {
            return;
        }

        if (item is not null)
        {
            _layout.Items.Remove(item);
        }

        // An item taken away cannot stay named by the dock: a document that names an item it does not
        // hold is not valid, and the next load would set the whole layout aside.
        _layout.Dock.Entries.RemoveAll(entry => string.Equals(entry.ItemId, id, StringComparison.Ordinal));

        if (view is not null)
        {
            DetachView(view);
        }

        if (_selectedId == id)
        {
            _selectedId = null;
        }

        // A docked item takes its slot with it, so the rail is re-laid out either way.
        ApplyLayout();
        RefreshMissingIds();
        RefreshItems();
        SaveLayout();
        UpdateRegion();
        Bump();
    }

    /// <summary>Takes one item off the desktop and releases what its icon took.</summary>
    private void DetachView(ItemView view)
    {
        _freeViews.Remove(view);
        _dockViews.Remove(view);

        if (view.Visual.Parent is { } parent)
        {
            parent.Children.Remove(view.Visual);
        }

        view.IconSurface?.Dispose();
        view.IconSurface = null;
        view.IconVisual = null;
    }

    private void RefreshItems() => _items = _layout.Items.Select(item => item.Clone()).ToList();

    private void RefreshMissingIds()
    {
        List<string>? missing = null;
        foreach (var view in _freeViews.Concat(_dockViews))
        {
            if (view.MissingShown)
            {
                (missing ??= []).Add(view.Item.Id);
            }
        }

        _missingIds = missing;
    }

    /// <summary>Recomputes every item's place from the display bounds; nothing pixel-shaped is stored.</summary>
    private void ApplyLayout()
    {
        if (_root is null || _rail is null)
        {
            return;
        }

        var design = (double)CanvasIconLibrary.DesignSize;

        foreach (var view in _freeViews)
        {
            var placed = CanvasAnchorMath.PlaceItem(view.Item, _displayBounds, _scaleFactor);
            var (centerX, centerY) = CanvasAnchorMath.CenterOf(placed);
            var (dipX, dipY) = ToCanvasDip(centerX, centerY);
            view.CenterXDip = dipX;
            view.CenterYDip = dipY;
            view.RenderedXDip = dipX;
            view.RenderedYDip = dipY;
            view.Visual.Offset = new Vector3((float)(dipX - design / 2), (float)(dipY - design / 2), 0);
        }

        ApplyDockVisuals(animate: false);

        // The rail is where it belongs for the state the dock is in right now, without animating from
        // wherever the last mount left it.
        PlaceRail(animate: false);
    }

    /// <summary>The dock's own frame: which edge, on which display, at which scale.</summary>
    private DockFrame Frame() => new(_layout.Dock.Edge, _displayBounds, _scaleFactor);

    /// <summary>The dock's rail length for the layout it is showing right now.</summary>
    private double RailLengthDip(double runLengthDip) =>
        DockGeometry.RailLengthDip(_layout.Dock, runLengthDip);

    /// <summary>
    /// Where every dock item should be drawn for the current pointer position, and how large. Free
    /// items only change size; dock items slide apart so magnified neighbours do not overlap, the run
    /// stays centred on the rail, and an item being carried along the dock follows the pointer.
    /// </summary>
    private void UpdateHover()
    {
        var pointerX = _pointerXDip;
        var pointerY = _pointerYDip;
        var hasPointer = pointerX is not null && pointerY is not null;

        foreach (var view in _freeViews)
        {
            var hover = hasPointer
                ? CanvasProximity.ScaleForItem(pointerX!.Value, pointerY!.Value, view.CenterXDip, view.CenterYDip, _layout.Proximity)
                : 1.0;
            StartScale(view, hover);
        }

        ApplyDockVisuals(animate: true);
    }

    /// <summary>
    /// Lays the dock's items out for where the pointer is: the magnification comes from the core's
    /// own layout, and the rail is resized to the run it is showing.
    /// </summary>
    private void ApplyDockVisuals(bool animate)
    {
        if (_rail is null || _dockViews.Count == 0)
        {
            return;
        }

        var dock = _layout.Dock;
        var frame = Frame();
        var resting = DockGeometry.RestingCentres(dock, frame.AlongCentreDip, _dockViews.Count);
        var pointerAlong = _dockPointerAlongDip;

        // An item being carried is not part of the run: it is under the pointer, and the others make
        // room for where it would land.
        var carried = _dockDrag is null ? -1 : _dockViews.IndexOf(_dockDrag);
        var scales = new double[_dockViews.Count];
        var centres = new double[_dockViews.Count];
        if (carried >= 0)
        {
            var preview = DockReorder.PreviewCentres(resting, carried, _dockInsertIndex ?? carried);
            for (var i = 0; i < _dockViews.Count; i++)
            {
                scales[i] = 1.0;
                centres[i] = preview[i];
            }
        }
        else
        {
            var layout = DockMagnification.Compute(pointerAlong, resting, frame.AlongCentreDip, dock);
            for (var i = 0; i < _dockViews.Count; i++)
            {
                scales[i] = layout.Items[i].Scale;
                centres[i] = layout.Items[i].CenterAlongDip;
            }
        }

        var runLength = carried >= 0
            ? DockGeometry.RestingRunDip(dock, _dockViews.Count)
            : RunLengthOf(dock, scales);
        var railLength = RailLengthDip(runLength);
        var railRect = DockGeometry.RailRect(dock, _displayBounds, _scaleFactor, railLength, _dock.RevealTarget);
        var (railX, railY) = ToCanvasDip(railRect.X, railRect.Y);
        var depth = DockGeometry.ItemCentreDepthDip(dock);

        for (var i = 0; i < _dockViews.Count; i++)
        {
            var view = _dockViews[i];
            var place = frame.PointAt(centres[i], depth);

            // The item in the hand is placed by the drag itself, not by the run.
            if (view == _dockDrag)
            {
                continue;
            }

            var moved = Math.Abs(place.X - view.RenderedXDip) + Math.Abs(place.Y - view.RenderedYDip) > OffsetEpsilonDip;
            view.RenderedXDip = place.X;
            view.RenderedYDip = place.Y;
            StartScale(view, scales[i]);
            PlaceDockVisual(view, railX, railY, (double)CanvasIconLibrary.DesignSize, animate && moved);
        }

        // The rail grows and shrinks with its run, so a magnified dock is not drawn against a rail
        // it no longer fits in. Its length follows the pointer continuously, which is smoother than
        // any animation of it would be.
        SizeRail(railRect);
    }

    /// <summary>The length of a run of items at the given scales, gaps included.</summary>
    private static double RunLengthOf(DockOptions dock, IReadOnlyList<double> scales)
    {
        var length = 0.0;
        for (var i = 0; i < scales.Count; i++)
        {
            length += dock.ItemSizeDip * Math.Max(1.0, scales[i]);
            if (i + 1 < scales.Count)
            {
                length += dock.SpacingDip * (Math.Max(1.0, scales[i]) + Math.Max(1.0, scales[i + 1])) / 2.0;
            }
        }

        return length;
    }

    private void StartScale(ItemView view, double hover)
    {
        if (Math.Abs(hover - view.Hover) < HoverEpsilon)
        {
            return;
        }

        view.Hover = hover;
        var scale = (float)view.Scale;
        var spring = view.ScaleSpring;
        spring.InitialValue = view.Visual.Scale;
        spring.FinalValue = new Vector3(scale, scale, 1);
        view.Visual.StartAnimation("Scale", spring);
    }

    private static void PlaceDockVisual(ItemView view, double railX, double railY, double design, bool animate)
    {
        var offset = new Vector3(
            (float)(view.RenderedXDip - railX - design / 2),
            (float)(view.RenderedYDip - railY - design / 2),
            0);

        if (!animate)
        {
            view.Visual.Offset = offset;
            return;
        }

        var spring = view.MoveSpring;
        spring.InitialValue = view.Visual.Offset;
        spring.FinalValue = offset;
        view.Visual.StartAnimation("Offset", spring);
    }

    /// <summary>
    /// Puts the rail where the dock's reveal puts it — against the edge when it is out, pushed off
    /// past the edge to leave only its peek when it is away — and springs it there.
    /// </summary>
    private void PlaceRail(bool animate)
    {
        if (_rail is null || _railBackdrop is null)
        {
            return;
        }

        var runLength = DockGeometry.RestingRunDip(_layout.Dock, _dockViews.Count);
        var railLength = RailLengthDip(runLength);
        var rect = DockGeometry.RailRect(_layout.Dock, _displayBounds, _scaleFactor, railLength, _dock.RevealTarget);

        SizeRail(rect);

        var (dipX, dipY) = ToCanvasDip(rect.X, rect.Y);
        var offset = new Vector3((float)dipX, (float)dipY, 0);
        if (!animate || _railSpring is null)
        {
            _rail.Offset = offset;
            return;
        }

        _railSpring.InitialValue = _rail.Offset;
        _railSpring.FinalValue = offset;
        _rail.StartAnimation("Offset", _railSpring);
    }

    /// <summary>Resizes the rail and its backdrop to the run it is showing.</summary>
    private void SizeRail(PixelRect rect)
    {
        if (_rail is null || _railBackdrop is null || _compositor is null)
        {
            return;
        }

        var (_, _, width, height) = ToDipRect(rect);
        var size = new Vector2((float)Math.Max(1, width), (float)Math.Max(1, height));
        _rail.Size = size;

        // The backdrop is rebuilt only when its size really changed: a pointer event that does not
        // change the run costs nothing here.
        if (_railBackdrop.Size != size || _railBackdrop.Shapes.Count == 0)
        {
            _railBackdrop.Size = size;
            _railBackdrop.Shapes.Clear();
            _railBackdrop.Shapes.Add(CanvasIconLibrary.Panel(_compositor, size.X, size.Y, 22, RailFill));
        }
    }

    /// <summary>Puts the dock's views in the order the dock holds them.</summary>
    private void OrderDockViews()
    {
        if (_rail is null)
        {
            return;
        }

        var ordered = _layout.Dock.Entries
            .Select(entry => _dockViews.FirstOrDefault(view => string.Equals(view.Item.Id, entry.ItemId, StringComparison.Ordinal)))
            .Where(view => view is not null)
            .Select(view => view!)
            .ToList();

        // A view whose entry has gone missing stays, at the end, rather than vanishing from the dock.
        foreach (var view in _dockViews)
        {
            if (!ordered.Contains(view))
            {
                ordered.Add(view);
            }
        }

        _dockViews.Clear();
        _dockViews.AddRange(ordered);
    }

    private void OrderFreeViews()
    {
        _itemsLayer!.Children.RemoveAll();
        foreach (var view in _freeViews.OrderBy(v => v.Item.Z))
        {
            _itemsLayer.Children.InsertAtTop(view.Visual);
        }
    }

    /// <summary>
    /// Asks for the item's real icon, if it can have one: an address has no file behind it to read
    /// an icon from, and a target that is gone has none of its own — both keep their glyph. The
    /// request is only a question, answered later through the work message.
    /// </summary>
    private void RequestIcon(ItemView view, double sizeDip, double maxScale)
    {
        if (view.Item.Target is UrlTarget || view.Item.IsMissing())
        {
            return;
        }

        // Read at the size the item will really be drawn at, magnification included, so a magnified
        // item is not a blown-up smaller picture.
        view.IconPath = view.Item.Location;
        view.IconPixels = (int)Math.Ceiling(sizeDip * _scaleFactor * maxScale);
        _iconCache.Request(view.IconPath, view.IconPixels);
    }

    /// <summary>
    /// Puts a real icon on every item whose icon has arrived, and asks again for the ones still
    /// being read. Idempotent, and only ever runs on the shell thread: from the mount, and from the
    /// work message the icon reader's arrival posts.
    /// </summary>
    private void EnsureIcons()
    {
        if (_compositor is null || _root is null)
        {
            return;
        }

        var attached = false;
        foreach (var view in _freeViews.Concat(_dockViews))
        {
            if (view.IconVisual is not null || view.IconPath.Length == 0)
            {
                continue;
            }

            if (_iconCache.TryGet(view.IconPath, view.IconPixels) is { } bitmap)
            {
                AttachIcon(view, bitmap);
                attached = true;
            }
            else
            {
                _iconCache.Request(view.IconPath, view.IconPixels);
            }
        }

        if (attached)
        {
            Bump();
        }
    }

    /// <summary>Draws a resolved icon over the item's tile and retires the glyph for good.</summary>
    private void AttachIcon(ItemView view, IconBitmap bitmap)
    {
        var compositor = _compositor;
        if (compositor is null)
        {
            return;
        }

        // The device is only worth having once there is a real icon to carry, and it stays for the
        // life of the content so a remount does not build a second one.
        _iconDevice ??= IconSurfaceDevice.TryCreate(_logger);
        if (_iconDevice is null)
        {
            return;
        }

        var icon = IconSurface.TryCreate(compositor, _iconDevice, bitmap, _logger);
        if (icon is null)
        {
            return;
        }

        var visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(CanvasIconLibrary.DesignSize, CanvasIconLibrary.DesignSize);
        visual.Brush = icon.Brush;

        // A real icon brings its own shape and its own alpha, so the tile and glyph step aside.
        view.GlyphVisual.IsVisible = false;
        view.Visual.Children.InsertAtTop(visual);
        view.IconVisual = visual;
        view.IconSurface = icon;
    }

    private void OnIconArrived() => Post(EnsureIcons);

    /// <summary>
    /// Hands work back to the shell thread: the queue keeps it, the window's work message runs it.
    /// Calls with no mount behind them are dropped by the next unmount.
    /// </summary>
    private void Post(Action work)
    {
        _work.Enqueue(work);
        if (_window != nint.Zero)
        {
            NativeMethods.PostMessageW(_window, NativeMethods.WmCanvasWork, nint.Zero, nint.Zero);
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var work))
        {
            work();
        }
    }

    /// <summary>Moves an item to the front of the free items and remembers it in the layout.</summary>
    private void Raise(ItemView view)
    {
        var top = _freeViews.Count == 0 ? 0 : _freeViews.Max(v => v.Item.Z);
        view.Item.Z = top + 1;
        OrderFreeViews();
    }

    /// <summary>Puts the pointer (in canvas DIP) into the hover math and keeps the leave notice armed.</summary>
    private void SetPointer(double x, double y)
    {
        _pointerXDip = x;
        _pointerYDip = y;

        if (!_trackingLeave)
        {
            var options = new NativeMethods.TrackMouseEventOptions
            {
                Size = Marshal.SizeOf<NativeMethods.TrackMouseEventOptions>(),
                Flags = NativeMethods.TmeLeave,
                Track = _window,
            };
            _trackingLeave = NativeMethods.TrackMouseEvent(ref options);
        }
    }

    /// <summary>
    /// Starts following the shared pointer router. Called on every mount, after the visuals exist:
    /// the router is the canvas' window to the pointer beyond its own region.
    /// </summary>
    private void SubscribeToPointer()
    {
        if (_pointer is null)
        {
            return;
        }

        _pointer.PointerMoved += OnPointerMoved;
        _pointer.EnteredDesktopRegion += OnPointerEnteredDesktopRegion;
        _pointer.LeftDesktopRegion += OnPointerLeftDesktopRegion;
    }

    private void UnsubscribeFromPointer()
    {
        if (_pointer is null)
        {
            return;
        }

        _pointer.PointerMoved -= OnPointerMoved;
        _pointer.EnteredDesktopRegion -= OnPointerEnteredDesktopRegion;
        _pointer.LeftDesktopRegion -= OnPointerLeftDesktopRegion;
    }

    private void OnPointerMoved(object? sender, DesktopPointerEventArgs e) => ApplyPointer(e.State);

    private void OnPointerEnteredDesktopRegion(object? sender, DesktopPointerEventArgs e) => ApplyPointer(e.State);

    /// <summary>The pointer moved onto an ordinary application window: the desktop stops reacting.</summary>
    private void OnPointerLeftDesktopRegion(object? sender, DesktopPointerEventArgs e)
    {
        if (_drag is not null)
        {
            // A drag belongs to the captured window messages and keeps running over any window.
            return;
        }

        ClearPointer();
    }

    /// <summary>Feeds a router reading into the hover and dock math, exactly like a window message would.</summary>
    private void ApplyPointer(DesktopPointerState state)
    {
        if (_drag is not null || _dockDrag is not null || _root is null || _target is null)
        {
            return;
        }

        var (x, y) = ToCanvasDip(state.X, state.Y);
        SetPointer(x, y);
        UpdateDockPointer();
        UpdateHover();
        UpdateDock(interactionLocked: false);
        Bump();
    }

    /// <summary>The pointer is no longer over the desktop: hover decays back to rest.</summary>
    private void ClearPointer()
    {
        if (_pointerXDip is null && _pointerYDip is null)
        {
            return;
        }

        _pointerXDip = null;
        _pointerYDip = null;
        _dockPointerAlongDip = null;
        UpdateHover();
        UpdateDock(interactionLocked: false);
        Bump();
    }

    /// <summary>
    /// Reads the pointer against the dock's own edge: the position along the rail, and whether it is
    /// anywhere near it. A pointer that is not within the dock's own band of the display counts as
    /// nowhere near it, which is what keeps a dock on the left edge from magnifying while the pointer
    /// crosses the middle of the screen.
    /// </summary>
    private void UpdateDockPointer()
    {
        _dockPointerAlongDip = null;

        var dock = _layout.Dock;
        if (!dock.Enabled || !_dock.IsOut)
        {
            // A rail that is away magnifies nothing: its items are off the display, and a dock that is
            // not being used should not be doing work either.
            return;
        }

        if (_pointerXDip is not { } x || _pointerYDip is not { } y)
        {
            return;
        }

        var (along, depth) = DockGeometry.ToAlongAndDepth(Frame(), x, y);
        if (depth >= 0 && depth <= dock.EdgeMarginDip + dock.RailThicknessDip)
        {
            _dockPointerAlongDip = along;
        }
    }

    /// <summary>Whether the pointer is on the dock's own band of the display, rail or strip.</summary>
    private bool IsPointerOnDock(double x, double y)
    {
        var dock = _layout.Dock;
        if (!dock.Enabled)
        {
            return false;
        }

        var (_, depth) = DockGeometry.ToAlongAndDepth(Frame(), x, y);
        return depth >= 0 && depth <= dock.EdgeMarginDip + dock.RailThicknessDip;
    }

    /// <summary>
    /// Whether the pointer wants the rail out: inside the trigger strip, or on a rail that is already
    /// out. The dock is the only thing that can answer this, which is why the router's own
    /// discrimination between the desktop, a surface of ours and an ordinary window comes first: a
    /// pointer over someone else's window never reaches here at all.
    /// </summary>
    private bool WantsDock(double x, double y)
    {
        var dock = _layout.Dock;
        if (!dock.Enabled || !IsPointerOnDock(x, y))
        {
            return false;
        }

        if (DockGeometry.InTriggerBand(dock, _displayBounds, _scaleFactor, x, y))
        {
            return true;
        }

        if (_dockViews.Count == 0 || !_dock.IsOut)
        {
            // An empty dock has nothing to reveal; a retracted one answers from its strip alone.
            return false;
        }

        return DockGeometry.RailContains(
            dock,
            _displayBounds,
            _scaleFactor,
            CurrentRailLengthDip(),
            reveal: 1.0,
            x,
            y);
    }

    /// <summary>The rail's length for the run it is showing right now.</summary>
    private double CurrentRailLengthDip() =>
        RailLengthDip(_dockViews.Count == 0 ? 0 : DockGeometry.RestingRunDip(_layout.Dock, _dockViews.Count));

    /// <summary>
    /// Where an item let go at this point would land in the dock, or null when the pointer is too far
    /// from the dock's edge for the drop to mean the dock at all.
    /// </summary>
    private int? DockDropIndex(double x, double y)
    {
        var dock = _layout.Dock;
        if (!dock.Enabled || dock.Entries.Count == 0 || !IsPointerOnDock(x, y))
        {
            // An empty dock takes the item as its first entry: the run it is dropped into is empty.
            return dock.Enabled && IsPointerOnDock(x, y) ? 0 : null;
        }

        var (along, _) = DockGeometry.ToAlongAndDepth(Frame(), x, y);
        var centres = DockGeometry.RestingCentres(dock, Frame().AlongCentreDip, _dockViews.Count);
        return DockReorder.TargetIndex(along, centres, draggedIndex: -1);
    }

    /// <summary>
    /// Advances the dock's own state machine and, when it changed, moves the rail. The machine owns
    /// the delays and the five states; what it is told here is only what the pointer is doing and
    /// whether the rail has finished moving.
    /// </summary>
    private void UpdateDock(bool interactionLocked)
    {
        var now = Environment.TickCount64;
        var wants = _pointerXDip is { } x && _pointerYDip is { } y && WantsDock(x, y);

        if (_dock.Advance(now, wants, interactionLocked, DockRevealSettled(now)))
        {
            ApplyDockState(now);
        }
        else if (_dockSettlesAt is { } settles && now >= settles)
        {
            // The rail has come to rest: the region can shrink back to what is really there.
            _dockSettlesAt = null;
            UpdateRegion();
        }

        ArmDockTimer(now);
    }

    /// <summary>Whether the rail has stopped moving towards wherever the dock last sent it.</summary>
    private bool DockRevealSettled(long now) => _dockSettlesAt is null || now >= _dockSettlesAt;

    private void ApplyDockState(long now)
    {
        _dockSettlesAt = now + (long)(_layout.Dock.Spring.PeriodSeconds * RailSettlePeriods * 1000);

        PlaceRail(animate: true);
        UpdateRegion();
        _logger.LogInformation("The desktop dock {State}", _dock.State.ToString().ToLowerInvariant());
    }

    /// <summary>Arms the one-shot timer for the next thing the dock will need on its own, if anything.</summary>
    private void ArmDockTimer(long now)
    {
        if (_window == nint.Zero)
        {
            return;
        }

        var delay = _dock.PendingChangeDelayMilliseconds(now);
        if (delay is null && _dockSettlesAt is { } settles)
        {
            delay = Math.Max(0, settles - now);
        }

        if (delay is null)
        {
            NativeMethods.KillTimer(_window, DockTimerId);
            return;
        }

        if (NativeMethods.SetTimer(_window, DockTimerId, (uint)Math.Max(1.0, Math.Ceiling(delay.Value)), nint.Zero) == nint.Zero)
        {
            _logger.LogDebug("The dock timer could not be armed ({Error})", Marshal.GetLastWin32Error());
        }
    }

    private void OnDockTimer()
    {
        UpdateDock(interactionLocked: _drag is not null || _dockDrag is not null);

        // The dock may have decided something with no input behind it: the diagnostics must not wait
        // for the next pointer message to tell that story.
        Bump();
    }

    /// <summary>
    /// Rebuilds the window region: the union of everything the canvas can draw — item extents at
    /// their largest possible scale, the rail while it is out, and the dock's trigger band.
    /// Outside it the window is not hit at all, so the desktop below keeps its clicks.
    /// </summary>
    /// <remarks>
    /// The region is the visual area's upper bound, not its current shape: hover only ever scales an
    /// item up to <c>Proximity.MaxScale</c>, so an item's pixels always stay inside the box the
    /// region was built from, at every point of the animation. That is what keeps the region static —
    /// it is rebuilt on mounts, drops, dock phases and display changes, never per frame — and it is
    /// why an enlargement never gets clipped by the window and never needs a region of its own.
    /// </remarks>
    private void UpdateRegion()
    {
        if (_window == nint.Zero || !NativeMethods.IsWindow(_window))
        {
            return;
        }

        if (_drag is not null)
        {
            // While an item is dragged the whole window must be hittable and nothing may be
            // clipped; the mouse capture routes the input anyway.
            ClearRegion();
            return;
        }

        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        try
        {
            foreach (var view in _freeViews)
            {
                AddItemExtent(region, view, _layout.Proximity.MaxScale);
            }

            var dock = _layout.Dock;
            if (dock.Enabled && _dockViews.Count > 0 && (_dock.IsOut || _dockSettlesAt is not null))
            {
                // Everything the dock can draw, at its largest, in one rectangle: the region never has
                // to change while the pointer moves along the rail, so a magnified item is never
                // clipped and no error can leave a hole in the dock's own strip.
                AddRect(region, DockGeometry.Extent(dock, _displayBounds, _scaleFactor, _dockViews.Count));
            }

            if (dock.Enabled)
            {
                // The strip stays hittable in every state, including while the rail is away: it is the
                // only way the pointer can summon it, and the only way an item can be dropped on an
                // empty dock.
                AddRect(region, DockGeometry.TriggerBand(dock, _displayBounds, _scaleFactor));
            }

            if (NativeMethods.SetWindowRgn(_window, region, false) == 0)
            {
                _logger.LogWarning("The canvas window region could not be applied ({Error})", Marshal.GetLastWin32Error());
                NativeMethods.DeleteObject(region);
            }
        }
        catch
        {
            NativeMethods.DeleteObject(region);
            throw;
        }
    }

    private void ClearRegion()
    {
        if (_window != nint.Zero && NativeMethods.IsWindow(_window))
        {
            NativeMethods.SetWindowRgn(_window, nint.Zero, false);
        }
    }

    private void AddItemExtent(nint region, ItemView view, double maxScale)
    {
        var half = (CanvasIconLibrary.DesignSize * view.BaseScale * maxScale * _scaleFactor / 2.0) + RegionSlackPixels;
        var (centerX, centerY) = ToDisplayPixels(view.RenderedXDip, view.RenderedYDip);
        AddRect(region, new PixelRect(
            (int)Math.Floor(centerX - half),
            (int)Math.Floor(centerY - half),
            (int)Math.Ceiling(half * 2),
            (int)Math.Ceiling(half * 2)));
    }

    /// <summary>Adds a rectangle, given in display pixels, to a window region given in window pixels.</summary>
    private void AddRect(nint region, PixelRect rect)
    {
        var local = new PixelRect(rect.X - _displayBounds.X, rect.Y - _displayBounds.Y, rect.Width, rect.Height);
        var piece = NativeMethods.CreateRectRgn(local.X, local.Y, local.X + local.Width, local.Y + local.Height);
        NativeMethods.CombineRgn(region, region, piece, NativeMethods.RgnOr);
        NativeMethods.DeleteObject(piece);
    }

    /// <summary>
    /// The item under the pointer, front to back. Every centre here is canvas DIP. A rail that is away
    /// is not under the pointer at all: its items are off the display, and a click there belongs to
    /// whatever the desktop has underneath.
    /// </summary>
    private ItemView? HitTest(double x, double y)
    {
        if (_dock.IsOut && _dockViews.Count > 0)
        {
            foreach (var view in _dockViews)
            {
                if (InsideItem(view, x, y))
                {
                    return view;
                }
            }
        }

        foreach (var view in _freeViews.OrderByDescending(v => v.Item.Z))
        {
            if (InsideItem(view, x, y))
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a point is inside the item's clickable box. The box follows the item's full target
    /// scale — base size times the hover factor it is animating towards — which is the size the
    /// visual converges on, so the clickable area always covers the pixels being drawn and a
    /// magnified item is clickable across its whole enlarged face, not just its resting box.
    /// </summary>
    private static bool InsideItem(ItemView view, double x, double y)
    {
        var half = CanvasIconLibrary.DesignSize * view.Scale / 2.0;
        return Math.Abs(x - view.RenderedXDip) <= half
            && Math.Abs(y - view.RenderedYDip) <= half;
    }

    private void OnLeftDown(double x, double y)
    {
        var (pixelX, pixelY) = ToDisplayPixels(x, y);
        _gestures.Press(pixelX, pixelY);

        _pressed = HitTest(x, y);
        if (_pressed is null)
        {
            return;
        }

        // A press is the moment to ask whether the target is still there, so an item whose file was
        // deleted while the canvas was showing stops pretending the moment the user touches it.
        if (!_pressed.MissingShown)
        {
            CheckMissingOne(_pressed);
        }

        // Held from the press: the release then always finds its way back here, however far the
        // pointer travels first.
        NativeMethods.SetCapture(_window);

        // A press never moves anything by itself. Whether it was a click or the start of a drag is
        // decided by what happens next — the release, or travel past the system's drag rectangle —
        // which is what keeps a slightly shaky click from being taken for a drag.
        _grabXDip = x - _pressed.RenderedXDip;
        _grabYDip = y - _pressed.RenderedYDip;

        if (_layout.IsDocked(_pressed.Item.Id))
        {
            // Where the press landed inside the item, measured along the rail and across its depth:
            // a dock item is carried by the point that was grabbed, so it never jumps under the hand.
            var frame = Frame();
            var (along, depth) = DockGeometry.ToAlongAndDepth(frame, x, y);
            var (itemAlong, itemDepth) = DockGeometry.ToAlongAndDepth(frame, _pressed.RenderedXDip, _pressed.RenderedYDip);
            _dockGrabAlongDip = along - itemAlong;
            _dockGrabDepthDip = depth - itemDepth;
        }

        Bump();
    }

    /// <summary>
    /// Moves the held item, from the moment the press has travelled further than the system's drag
    /// rectangle allows. Until then the item stays where it is, so a slightly shaky click is still
    /// a click.
    /// </summary>
    private void DragTo(double x, double y)
    {
        var held = _pressed;
        if (held is null)
        {
            return;
        }

        var wasDragging = _gestures.IsDragging;
        var (pixelX, pixelY) = ToDisplayPixels(x, y);
        if (!_gestures.Move(pixelX, pixelY))
        {
            return;
        }

        if (!wasDragging)
        {
            _logger.LogInformation(
                "{Place} drag started on {Id}",
                _layout.IsDocked(held.Item.Id) ? "A dock" : "A canvas",
                held.Item.Id);
        }

        if (_layout.IsDocked(held.Item.Id))
        {
            DragDockItem(held, x, y);
            return;
        }

        if (_drag is null)
        {
            _drag = held;
            Raise(held);

            // Everything calms down while one item is being moved: the drag is the only motion.
            foreach (var other in _freeViews)
            {
                StartScale(other, 1.0);
            }

            // The item may travel anywhere on the display before the button comes up, so while it is
            // held the window takes the whole display and nothing is clipped.
            ClearRegion();
        }

        // The item stays fully on the display, which is also what a saved offset reproduces later.
        var design = (double)CanvasIconLibrary.DesignSize;
        var half = held.Item.SizeDip / 2.0;
        var maxX = (_displayBounds.Width / _scaleFactor) - half;
        var maxY = (_displayBounds.Height / _scaleFactor) - half;
        held.CenterXDip = Math.Clamp(x - _grabXDip, Math.Min(half, maxX), Math.Max(half, maxX));
        held.CenterYDip = Math.Clamp(y - _grabYDip, Math.Min(half, maxY), Math.Max(half, maxY));
        held.RenderedXDip = held.CenterXDip;
        held.RenderedYDip = held.CenterYDip;
        held.Visual.Offset = new Vector3((float)(held.CenterXDip - design / 2), (float)(held.CenterYDip - design / 2), 0);

        // Carrying an item towards the edge is what brings the dock out to meet it.
        UpdateDock(interactionLocked: IsPointerOnDock(x, y));
        Bump();
    }

    /// <summary>
    /// Carries a dock item: along the rail it takes the place its neighbours are making room for, and
    /// away from the rail it is drawn under the pointer, ready to be let go on the canvas.
    /// </summary>
    private void DragDockItem(ItemView view, double x, double y)
    {
        var dock = _layout.Dock;
        var frame = Frame();
        var (along, depth) = DockGeometry.ToAlongAndDepth(frame, x, y);
        var index = _dockViews.IndexOf(view);

        _dockDrag = view;
        _dockInsertIndex = index >= 0
            ? DockReorder.TargetIndex(along, DockGeometry.RestingCentres(dock, frame.AlongCentreDip, _dockViews.Count), index)
            : null;

        // The item follows the pointer exactly, keeping the point that was grabbed under it: over the
        // rail it is the item being reordered, past the rail it is on its way to the canvas.
        var place = frame.PointAt(along - _dockGrabAlongDip, depth - _dockGrabDepthDip);
        view.RenderedXDip = place.X;
        view.RenderedYDip = place.Y;
        view.CenterXDip = place.X;
        view.CenterYDip = place.Y;

        // The rail is out for the whole drag, so a dock item can always be put back on it.
        UpdateDock(interactionLocked: true);
        UpdateHover();
        Bump();
    }

    private void OnLeftUp(double x, double y)
    {
        var view = _pressed;
        _pressed = null;

        var (pixelX, pixelY) = ToDisplayPixels(x, y);
        var gesture = _gestures.Release(pixelX, pixelY, Environment.TickCount64);

        if (_dockDrag is not null)
        {
            var carried = _dockDrag;
            _dockDrag = null;
            _dockInsertIndex = null;
            NativeMethods.ReleaseCapture();
            CommitDockDrag(carried, x, y);
        }
        else if (_drag is not null)
        {
            var dragged = _drag;
            _drag = null;

            // Released before the capture goes, so the capture-changed message does not look like a
            // second, unexpected end of the same drag.
            NativeMethods.ReleaseCapture();

            if (gesture == DesktopGesture.DragEnd)
            {
                CommitCanvasDrag(dragged, x, y);
            }

            // The whole display was hittable while the item was held; the region goes back to what
            // is really drawn.
            UpdateRegion();
        }
        else if (view is not null)
        {
            NativeMethods.ReleaseCapture();

            switch (gesture)
            {
                case DesktopGesture.Click:
                    // The dock is a launcher: one click opens what it holds. On the canvas the first
                    // click only picks the item out, and a second one opens it.
                    if (_layout.IsDocked(view.Item.Id))
                    {
                        _logger.LogInformation("The dock item {Id} ({Name}) was clicked", view.Item.Id, view.Item.Name);
                        Launch(view.Item);
                    }
                    else
                    {
                        Select(view);
                    }

                    break;

                case DesktopGesture.DoubleClick:
                    Launch(view.Item);
                    break;
            }
        }

        _trackingLeave = false;
        SetPointer(x, y);
        UpdateDockPointer();
        UpdateHover();
        UpdateDock(interactionLocked: false);
        Bump();
    }

    /// <summary>
    /// Marks the clicked item as the selected one, or clears the selection when the click landed on
    /// empty desktop. Selecting never opens anything; the badge and the outline are the whole answer.
    /// </summary>
    private void Select(ItemView? view)
    {
        var id = view?.Item.Id;
        if (id == _selectedId)
        {
            return;
        }

        _selectedId = id;
        ApplySelection();

        if (view is not null)
        {
            _logger.LogInformation("The desktop item {Id} ({Name}) was selected", view.Item.Id, view.Item.Name);
        }
    }

    /// <summary>Draws the outline on the selected item and takes it off every other one.</summary>
    private void ApplySelection()
    {
        var compositor = _compositor;
        foreach (var view in _freeViews.Concat(_dockViews))
        {
            var selected = view.Item.Id == _selectedId;
            if (selected && view.SelectionRing is null && compositor is not null)
            {
                view.SelectionRing = CanvasIconLibrary.SelectionRing(compositor, CanvasIconLibrary.DesignSize);
                view.Visual.Children.InsertAtTop(view.SelectionRing);
            }

            if (view.SelectionRing is not null)
            {
                view.SelectionRing.IsVisible = selected;
            }
        }
    }

    /// <summary>
    /// Opens an item. The dock opens on a single click and the canvas on a double one, so the caller
    /// has already decided; what this adds is that the item must be the canvas' selected one when the
    /// open came from a double click, which is what <paramref name="requireSelection"/> says. The
    /// launcher is the only thing that starts anything — the canvas never builds a command line — and
    /// it runs on the pool, with the outcome posted back to the shell thread.
    /// </summary>
    private void Launch(DesktopItem item, bool requireSelection = false)
    {
        if (requireSelection && item.Id != _selectedId)
        {
            return;
        }

        var launcher = _launcher;
        if (launcher is null)
        {
            _logger.LogDebug("The desktop item {Id} was clicked, but the canvas has no launcher", item.Id);
            return;
        }

        // A copy, so the launcher reads one item's facts while the canvas keeps editing its own.
        var copy = item.Clone();
        var mount = _mountCount;
        _lastLaunchId = copy.Id;
        _lastLaunchOutcome = "Opening";
        _logger.LogInformation("The desktop item {Id} ({Name}) is being opened", copy.Id, copy.Name);

        _ = OpenAsync(launcher, copy, mount);
    }

    private async Task OpenAsync(IDesktopItemLauncher launcher, DesktopItem item, int mount)
    {
        var result = await launcher.LaunchAsync(item).ConfigureAwait(false);
        Post(() =>
        {
            // A mount that came and went while the shell was opening something is not the canvas
            // that asked for it; its views are gone.
            if (mount == _mountCount)
            {
                OnLaunched(result);
            }
        });
    }

    private void OnLaunched(DesktopItemLaunchResult result)
    {
        _lastLaunchOutcome = result.Outcome.ToString();

        switch (result.Outcome)
        {
            case DesktopItemLaunchOutcome.Launched:
                _logger.LogInformation("The desktop item {Id} was opened by the shell", _lastLaunchId);
                break;

            case DesktopItemLaunchOutcome.Missing:
                _logger.LogWarning("The desktop item {Id} points at {Location}, which is no longer there", _lastLaunchId, result.Error);
                if (_lastLaunchId is { } id)
                {
                    ApplyMissing([id], [id]);
                }

                break;

            case DesktopItemLaunchOutcome.Failed:
                _logger.LogWarning("The desktop item {Id} could not be opened: {Error}", _lastLaunchId, result.Error);
                break;
        }

        Bump();
    }

    /// <summary>
    /// Ends a canvas drag: an item let go over the dock joins it, and an item let go anywhere else
    /// keeps the anchor offset the layout stores.
    /// </summary>
    private void CommitCanvasDrag(ItemView view, double x, double y)
    {
        if (DockDropIndex(x, y) is { } index)
        {
            MoveIntoDock(view, index);
            return;
        }

        var (centerX, centerY) = ToDisplayPixels(view.CenterXDip, view.CenterYDip);
        var (offsetX, offsetY) = CanvasAnchorMath.OffsetForCenter(
            _displayBounds,
            _scaleFactor,
            view.Item.Anchor,
            centerX,
            centerY,
            view.Item.SizeDip);
        view.Item.OffsetXDip = offsetX;
        view.Item.OffsetYDip = offsetY;
        SaveLayout();
        _logger.LogInformation(
            "The canvas item {Id} was dropped at {X:0} / {Y:0} DIP from its {Anchor} anchor",
            view.Item.Id,
            view.Item.OffsetXDip,
            view.Item.OffsetYDip,
            view.Item.Anchor);
    }

    /// <summary>
    /// Ends a dock drag: an item let go on the rail keeps its place in the dock, in whatever order the
    /// drag left it, and an item let go away from the rail is put down on the canvas where it was
    /// released. Nothing is written until this moment.
    /// </summary>
    private void CommitDockDrag(ItemView view, double x, double y)
    {
        if (!IsPointerOnDock(x, y))
        {
            MoveOutOfDock(view, x, y);
            return;
        }

        // Where the item was let go decides the place it takes, rather than whatever the last frame of
        // the drag had worked out: the release is the moment the order really changes.
        var dock = _layout.Dock;
        var frame = Frame();
        var (along, _) = DockGeometry.ToAlongAndDepth(frame, x, y);
        var from = _dockViews.IndexOf(view);
        var target = from >= 0
            ? DockReorder.TargetIndex(along, DockGeometry.RestingCentres(dock, frame.AlongCentreDip, _dockViews.Count), from)
            : 0;

        var ids = _layout.Dock.Entries.Select(entry => entry.ItemId).ToList();
        var current = ids.IndexOf(view.Item.Id);
        if (current >= 0)
        {
            ids.RemoveAt(current);
            ids.Insert(Math.Clamp(target, 0, ids.Count), view.Item.Id);
        }

        _layout.Dock.Entries.Clear();
        foreach (var id in ids)
        {
            _layout.Dock.Entries.Add(new DockEntry { ItemId = id });
        }

        OrderDockViews();
        SaveLayout();
        _logger.LogInformation("The dock item {Id} was moved to place {Place}", view.Item.Id, target);
    }

    /// <summary>
    /// Moves an item from the canvas into the dock: the same item, the same file behind it, and one
    /// entry more in the dock. Its visuals move from the canvas layer to the rail's.
    /// </summary>
    private void MoveIntoDock(ItemView view, int index)
    {
        var ids = _layout.Dock.Entries.Select(entry => entry.ItemId).ToList();
        if (!ids.Contains(view.Item.Id))
        {
            ids.Insert(Math.Clamp(index, 0, ids.Count), view.Item.Id);
        }

        _layout.Dock.Enabled = true;
        _layout.Dock.Entries.Clear();
        foreach (var id in ids)
        {
            _layout.Dock.Entries.Add(new DockEntry { ItemId = id });
        }

        // Nothing about the item itself changes: only where it is drawn, and how large it is drawn.
        ReloadDockMembership();
        ApplySelection();
        ApplyLayout();
        UpdateRegion();
        SaveLayout();
        _logger.LogInformation("The canvas item {Id} was dropped on the dock at place {Place}", view.Item.Id, index);
    }

    /// <summary>
    /// Moves an item out of the dock and back onto the canvas, where it was let go. The layout keeps
    /// the same item; only the dock stops naming it.
    /// </summary>
    private void MoveOutOfDock(ItemView view, double x, double y)
    {
        _layout.Dock.Entries.RemoveAll(entry => string.Equals(entry.ItemId, view.Item.Id, StringComparison.Ordinal));

        // Where it was let go is where it lands, and the anchor offset is what the layout stores.
        view.CenterXDip = x - _grabXDip;
        view.CenterYDip = y - _grabYDip;
        var (centerX, centerY) = ToDisplayPixels(view.CenterXDip, view.CenterYDip);
        var (offsetX, offsetY) = CanvasAnchorMath.OffsetForCenter(
            _displayBounds,
            _scaleFactor,
            view.Item.Anchor,
            centerX,
            centerY,
            view.Item.SizeDip);
        view.Item.OffsetXDip = offsetX;
        view.Item.OffsetYDip = offsetY;

        ReloadDockMembership();
        ApplySelection();
        ApplyLayout();
        UpdateRegion();
        SaveLayout();
        _logger.LogInformation("The dock item {Id} was dropped on the canvas", view.Item.Id);
    }

    private void EndDragCapture()
    {
        _drag = null;
        _dockDrag = null;
        _dockInsertIndex = null;
        _pressed = null;
        _gestures.Cancel();
        _trackingLeave = false;
        NativeMethods.ReleaseCapture();
        ClearRegion();
    }

    private void SaveLayout()
    {
        // Fire and forget, on the pool: the shell thread must never wait for the disk, and the
        // store serialises writes anyway. A copy keeps the write consistent while the canvas
        // keeps editing the live layout.
        var copy = _layout.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                await _store.SaveAsync(copy).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The canvas layout could not be saved");
            }
        });
    }

    private bool TrySetCursor(nint lParam, out nint result)
    {
        result = nint.Zero;
        if ((int)(lParam.ToInt64() & 0xFFFF) != NativeMethods.HtClient)
        {
            return false;
        }

        var hand = _drag is not null;
        if (!hand && NativeMethods.GetCursorPos(out var cursor))
        {
            var (x, y) = ToCanvasDip(cursor.X, cursor.Y);
            hand = HitTest(x, y) is not null;
        }

        var cursorHandle = NativeMethods.LoadCursorW(nint.Zero, hand ? NativeMethods.IdcHand : NativeMethods.IdcArrow);
        if (cursorHandle != nint.Zero)
        {
            NativeMethods.SetCursor(cursorHandle);
        }

        result = 1;
        return true;
    }

    public bool OnWindowMessage(nint window, uint message, nint wParam, nint lParam, out nint result)
    {
        result = nint.Zero;
        if (window != _window || _root is null || _target is null)
        {
            return false;
        }

        switch (message)
        {
            case NativeMethods.WmMouseMove:
                var (moveX, moveY) = ClientDip(lParam);
                if (_pressed is not null)
                {
                    // While a press is held the captured window messages are the drag's only driver:
                    // this is where a press that travels far enough becomes a drag.
                    DragTo(moveX, moveY);
                }
                else
                {
                    // The router already saw this movement, but these messages cost nothing inside
                    // the region and keep hover working if raw input ever stops arriving (injected
                    // input, a failed registration), so both sources stay in use. The epsilon guards
                    // in the hover math make the second one a no-op when nothing changed.
                    SetPointer(moveX, moveY);
                    UpdateDockPointer();
                    UpdateHover();
                    UpdateDock(interactionLocked: false);
                    Bump();
                }

                return true;

            case NativeMethods.WmMouseLeave:
                _trackingLeave = false;
                if (_pointer is not { IsAttached: true })
                {
                    ClearPointer();
                }

                // With a router, leaving the window's region is not leaving the desktop: the region
                // only covers what the canvas draws, so the pointer is usually still on the desktop
                // layer there and the router keeps following — a real leave arrives as LeftDesktopRegion.
                return true;

            case NativeMethods.WmLButtonDown:
                var (downX, downY) = ClientDip(lParam);
                OnLeftDown(downX, downY);
                return true;

            case NativeMethods.WmLButtonUp:
                var (upX, upY) = ClientDip(lParam);
                OnLeftUp(upX, upY);
                return true;

            case NativeMethods.WmCaptureChanged:
                // Capture taken away by someone else: the press is over and the item, if one was
                // held, is dropped where it stands. No gesture is reported — nobody released here.
                _pressed = null;
                _gestures.Cancel();
                if (_dockDrag is not null)
                {
                    var carried = _dockDrag;
                    _dockDrag = null;
                    _dockInsertIndex = null;

                    // Wherever it was when the capture went is where it stays: a reorder or a move
                    // out, decided by the same rule the release uses.
                    var (carriedX, carriedY) = (carried.RenderedXDip, carried.RenderedYDip);
                    CommitDockDrag(carried, carriedX, carriedY);
                    UpdateHover();
                    UpdateRegion();
                    Bump();
                }
                else if (_drag is not null)
                {
                    var dropped = _drag;
                    _drag = null;
                    CommitCanvasDrag(dropped, dropped.CenterXDip, dropped.CenterYDip);
                    ClearRegion();
                    UpdateRegion();
                    UpdateHover();
                    Bump();
                }

                return true;

            case NativeMethods.WmSetCursor:
                return TrySetCursor(lParam, out result);

            case NativeMethods.WmCanvasWork:
                // Something the canvas asked another thread for is ready: icons, in this round.
                DrainWork();
                return true;

            case NativeMethods.WmTimer:
                if ((int)wParam == DockTimerId)
                {
                    OnDockTimer();
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private void Bump()
    {
        var now = Environment.TickCount64;
        _updates.Bump(now);
        RefreshSnapshot(now);
    }

    private void RefreshSnapshot(long now)
    {
        // The magnification factor, not the drawn size: the two items' natural sizes differ (the dock
        // draws its own smaller), so only the factor is comparable between them.
        var hovered = _freeViews
            .Concat(_dockViews)
            .Where(view => view.Hover > 1.0 + HoverEpsilon)
            .OrderByDescending(view => view.Hover)
            .FirstOrDefault();

        _snapshot = new CanvasDiagnosticsSnapshot(
            MountCount: _mountCount,
            LayoutPath: _store.FilePath,
            BoundsPixels: _displayBounds,
            BoundsWidthDip: _displayBounds.Width / _scaleFactor,
            BoundsHeightDip: _displayBounds.Height / _scaleFactor,
            ScaleFactor: _scaleFactor,
            Dpi: (int)Math.Round(_scaleFactor * 96),
            PointerXDip: _pointerXDip ?? 0,
            PointerYDip: _pointerYDip ?? 0,
            PointerInside: _pointerXDip is not null,
            ItemCount: _freeViews.Count + _dockViews.Count,
            HoveredItemId: hovered?.Item.Id,
            HoveredScale: hovered?.Hover ?? 1.0,
            DockPhase: _dock.State.ToString(),
            DockScale: _dock.RevealTarget,
            DockItemCount: _dockViews.Count,
            DockEdge: _layout.Dock.Edge.ToString(),
            DockEnabled: _layout.Dock.Enabled,
            UpdatesPerSecond: _updates.PerSecond(now),
            Updates: _updates.Total,
            MissingItemIds: _missingIds,
            SelectedItemId: _selectedId,
            LastLaunchId: _lastLaunchId,
            LastLaunchOutcome: _lastLaunchOutcome,
            IconCacheEntries: _iconCache.EntryCount,
            IconCacheBytes: _iconCache.ByteCount);
    }

    private (double X, double Y) ClientDip(nint lParam) => (
        (short)(lParam.ToInt64() & 0xFFFF) / _scaleFactor,
        (short)((lParam.ToInt64() >> 16) & 0xFFFF) / _scaleFactor);

    /// <summary>Display pixels to canvas DIP: the canvas' own coordinates start at the display's corner.</summary>
    private (double X, double Y) ToCanvasDip(double pixelX, double pixelY) => (
        (pixelX - _displayBounds.X) / _scaleFactor,
        (pixelY - _displayBounds.Y) / _scaleFactor);

    private (double X, double Y) ToDisplayPixels(double dipX, double dipY) => (
        _displayBounds.X + (dipX * _scaleFactor),
        _displayBounds.Y + (dipY * _scaleFactor));

    private (double X, double Y, double Width, double Height) ToDipRect(PixelRect rect) => (
        (rect.X - _displayBounds.X) / _scaleFactor,
        (rect.Y - _displayBounds.Y) / _scaleFactor,
        rect.Width / _scaleFactor,
        rect.Height / _scaleFactor);

    private static SpringVector3NaturalMotionAnimation Spring(Compositor compositor, CanvasSpring spring)
    {
        var animation = compositor.CreateSpringVector3Animation();
        animation.Period = TimeSpan.FromSeconds(spring.PeriodSeconds);
        animation.DampingRatio = (float)spring.DampingRatio;
        return animation;
    }

    /// <summary>One item's visual and the springs that move it.</summary>
    private sealed class ItemView
    {
        internal ItemView(
            DesktopItem item,
            ContainerVisual visual,
            ShapeVisual glyphVisual,
            SpringVector3NaturalMotionAnimation scaleSpring,
            SpringVector3NaturalMotionAnimation moveSpring,
            double baseScale)
        {
            Item = item;
            Visual = visual;
            GlyphVisual = glyphVisual;
            ScaleSpring = scaleSpring;
            MoveSpring = moveSpring;
            BaseScale = baseScale;
        }

        internal DesktopItem Item { get; }

        internal ContainerVisual Visual { get; }

        /// <summary>The tile and glyph the item starts with; hidden once a real icon arrives.</summary>
        internal ShapeVisual GlyphVisual { get; }

        /// <summary>The target its icon is read from; empty when the item has no icon of its own.</summary>
        internal string IconPath { get; set; } = string.Empty;

        /// <summary>The size the icon was asked for, in device pixels — the cache answers by both.</summary>
        internal int IconPixels { get; set; }

        /// <summary>The real icon, once it has arrived.</summary>
        internal SpriteVisual? IconVisual { get; set; }

        /// <summary>The surface the icon was loaded into, the brush drawing it, and its stream.</summary>
        internal IconSurface? IconSurface { get; set; }

        internal SpringVector3NaturalMotionAnimation ScaleSpring { get; }

        internal SpringVector3NaturalMotionAnimation MoveSpring { get; }

        /// <summary>The item's resting size in design units; the hover factor sits on top of it.</summary>
        internal double BaseScale { get; set; }

        /// <summary>Where the item rests: the distance math for magnification measures against this.</summary>
        internal double CenterXDip { get; set; }

        internal double CenterYDip { get; set; }

        /// <summary>Where the item is actually drawn; the same as the resting centre for free items.</summary>
        internal double RenderedXDip { get; set; }

        internal double RenderedYDip { get; set; }

        /// <summary>The hover factor being animated towards, 1 when nothing is near.</summary>
        internal double Hover { get; set; } = 1.0;

        /// <summary>The full scale the item is aiming for.</summary>
        internal double Scale => BaseScale * Hover;

        /// <summary>Whether the item is drawn as missing; dimming and the badge follow from it.</summary>
        internal bool MissingShown { get; set; }

        /// <summary>The outline the selected item wears; made once and then only shown or hidden.</summary>
        internal ShapeVisual? SelectionRing { get; set; }

        /// <summary>The warning badge a missing item wears; made when the item is first found missing.</summary>
        internal ShapeVisual? MissingBadge { get; set; }
    }
}
