using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Canvas;
using Muralis.Core.Models;
using Muralis.Desktop.Interop;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// The desktop canvas: a free-form layer of items above the desktop icons that the pointer can
/// reach. It owns what the desktop layer cannot know — where items sit, how they grow as the
/// pointer comes close, how they are dragged and how the edge dock retracts — and nothing about the
/// desktop itself: no window creation, no Explorer lifecycle, no display discovery. The shell
/// mounts it on a window it placed above the icons and hands over a target; Explorer restarts
/// simply look like a fresh mount.
/// </summary>
/// <remarks>
/// <para>
/// Input: the window gets a region that covers exactly the pixels the canvas can draw into, so
/// everywhere else the desktop keeps the mouse to itself — clicks on native icons around the
/// canvas are never swallowed, and the cursor crossing the region boundary is what starts and
/// ends hovering. Dragging lifts the region so the item being moved is not clipped.
/// </para>
/// <para>
/// Threads: every method runs on the shell thread — mount, unmount, geometry changes, the window
/// messages forwarded by the surface host and the dock's one-shot timer. Everything the pointer
/// does is event-driven: springs are handed to the composition engine, nothing polls, and a canvas
/// that is not being touched does no work at all.
/// </para>
/// </remarks>
internal sealed class CanvasSurfaceContent : ISurfaceContent, ISurfaceMessageSink
{
    /// <summary>The dock's one-shot timer on the surface window; the shell's own timer lives on no window.</summary>
    private const int DockTimerId = 2;

    /// <summary>A press that moves less than this is a click, not a drag.</summary>
    private const double DragThresholdDip = 4.0;

    /// <summary>Pixel margin around an item's largest possible extent in the window region.</summary>
    private const int RegionSlackPixels = 2;

    /// <summary>Below this the rail counts as invisible: it neither draws nor takes input.</summary>
    private const double VisibleEpsilon = 0.01;

    /// <summary>An offset change smaller than this is not worth a new animation.</summary>
    private const double OffsetEpsilonDip = 0.25;

    /// <summary>A hover change smaller than this is not worth a new animation.</summary>
    private const double HoverEpsilon = 0.0005;

    /// <summary>How long, in dock spring periods, the rail is treated as still moving.</summary>
    private const double RailSettlePeriods = 2.5;

    private static readonly Color RailFill = Color.FromArgb(150, 28, 32, 40);

    private readonly CanvasLayout _layout;
    private readonly CanvasLayoutStore _store;
    private readonly ILogger _logger;
    private readonly CanvasDockAutoHide _dock;
    private readonly List<ItemView> _freeViews = [];
    private readonly List<ItemView> _dockViews = [];
    private readonly CanvasUpdateRate _updates = new();

    private nint _window;
    private double _scaleFactor = 1.0;
    private PixelRect _displayBounds;
    private int _mountCount;

    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private ContainerVisual? _itemsLayer;
    private ContainerVisual? _rail;
    private ShapeVisual? _railBackdrop;
    private SpringVector3NaturalMotionAnimation? _railSpring;
    private double? _railSettlesAt;

    private double? _pointerXDip;
    private double? _pointerYDip;
    private bool _trackingLeave;

    private ItemView? _drag;
    private double _pressXDip;
    private double _pressYDip;
    private double _grabXDip;
    private double _grabYDip;
    private bool _dragMoved;

    private volatile CanvasDiagnosticsSnapshot? _snapshot;

    internal CanvasSurfaceContent(CanvasLayout layout, CanvasLayoutStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _layout = layout;
        _store = store;
        _logger = logger;
        _dock = new CanvasDockAutoHide(layout.Dock);
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
        }
        catch
        {
            UnmountCore();
            throw;
        }

        _mountCount++;
        _logger.LogInformation(
            "The desktop canvas is showing {Count} items on {Width}x{Height} at {Scale:0.##}x (mount {Mount})",
            _freeViews.Count + _dockViews.Count,
            _displayBounds.Width,
            _displayBounds.Height,
            _scaleFactor,
            _mountCount);
        Bump();
        return Task.CompletedTask;
    }

    public Task UnmountAsync()
    {
        UnmountCore();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        UnmountAsync().GetAwaiter().GetResult();
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
            CommitDrag(_drag);
            EndDragCapture();
        }

        _displayBounds = geometry.Bounds;
        _scaleFactor = scale > 0 ? scale : 1.0;
        _pointerXDip = null;
        _pointerYDip = null;

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
        if (_window != nint.Zero)
        {
            NativeMethods.KillTimer(_window, DockTimerId);
        }

        if (_drag is not null)
        {
            CommitDrag(_drag);
            EndDragCapture();
        }

        _target?.Dispose();
        _target = null;
        _compositor?.Dispose();
        _compositor = null;
        _root = null;
        _itemsLayer = null;
        _rail = null;
        _railBackdrop = null;
        _railSpring = null;
        _railSettlesAt = null;
        _freeViews.Clear();
        _dockViews.Clear();
        _pointerXDip = null;
        _pointerYDip = null;
        _trackingLeave = false;
        _snapshot = null;
        _window = nint.Zero;
    }

    private void BuildTree()
    {
        var compositor = _compositor!;
        var design = CanvasIconLibrary.DesignSize;

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

        _railSpring = Spring(compositor, _layout.Motion.Dock);

        var hoverBase = _layout.Dock.ItemSizeDip / design;
        foreach (var item in _layout.Items)
        {
            if (!item.IsVisible)
            {
                continue;
            }

            var icon = CanvasIconLibrary.Create(compositor, item.IconKey, design);
            var visual = compositor.CreateContainerVisual();
            visual.Size = new Vector2(design, design);
            visual.CenterPoint = new Vector3(design / 2f, design / 2f, 0);
            visual.Children.InsertAtTop(icon);

            var baseScale = item.Placement == CanvasItemPlacement.Dock
                ? hoverBase
                : item.SizeDip / design;
            var view = new ItemView(item, visual, Spring(compositor, _layout.Motion.Hover), Spring(compositor, _layout.Motion.Dock), baseScale);

            // The authored box is always 96 units; the base scale is what turns it into the item's size.
            visual.Scale = new Vector3((float)baseScale, (float)baseScale, 1);

            if (item.Placement == CanvasItemPlacement.Dock)
            {
                _rail.Children.InsertAtTop(visual);
                _dockViews.Add(view);
            }
            else
            {
                _itemsLayer.Children.InsertAtTop(visual);
                _freeViews.Add(view);
            }
        }

        OrderFreeViews();
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

        var railDip = ToDipRect(CanvasRailLayout.RailRect(_layout.Dock, _displayBounds, _scaleFactor, _dockViews.Count, expanded: true));
        var railSize = new Vector2((float)railDip.Width, (float)railDip.Height);
        _rail.Offset = new Vector3((float)railDip.X, (float)railDip.Y, 0);
        _rail.Size = railSize;
        _rail.CenterPoint = RailCenterPoint(_layout.Dock.Edge, railSize);
        _rail.Scale = new Vector3(
            (float)(_dock.Phase == CanvasDockPhase.Shown ? _layout.Dock.ExpandedScale : _layout.Dock.CollapsedScale),
            (float)(_dock.Phase == CanvasDockPhase.Shown ? _layout.Dock.ExpandedScale : _layout.Dock.CollapsedScale),
            1);

        _railBackdrop!.Size = railSize;
        _railBackdrop.Shapes.Clear();
        _railBackdrop.Shapes.Add(CanvasIconLibrary.Panel(_compositor!, railSize.X, railSize.Y, 24, RailFill));

        var slots = CanvasRailLayout.SlotCenters(_layout.Dock, _displayBounds, _scaleFactor, _dockViews.Count);
        for (var i = 0; i < _dockViews.Count; i++)
        {
            var view = _dockViews[i];
            var (dipX, dipY) = ToCanvasDip(slots[i].X, slots[i].Y);
            view.CenterXDip = dipX;
            view.CenterYDip = dipY;
            view.RenderedXDip = dipX;
            view.RenderedYDip = dipY;
            PlaceDockVisual(view, railDip.X, railDip.Y, design, animate: false);
        }
    }

    /// <summary>
    /// Where each item should be drawn for the current pointer position. Free items only change
    /// size; dock items also slide apart so magnified neighbours do not overlap, and the whole run
    /// stays centred on the rail.
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

        if (_dockViews.Count > 0)
        {
            var options = _layout.Dock.Proximity;
            var hoverScales = new double[_dockViews.Count];
            for (var i = 0; i < _dockViews.Count; i++)
            {
                var view = _dockViews[i];
                hoverScales[i] = hasPointer
                    ? CanvasProximity.ScaleForItem(pointerX!.Value, pointerY!.Value, view.CenterXDip, view.CenterYDip, options)
                    : 1.0;
                StartScale(view, hoverScales[i]);
            }

            // The slot centres stay the reference for the distance math — measuring against the
            // displaced centres would feed the packing back into the magnification.
            var railDip = ToDipRect(CanvasRailLayout.RailRect(_layout.Dock, _displayBounds, _scaleFactor, _dockViews.Count, expanded: true));
            var slots = CanvasRailLayout.SlotCenters(_layout.Dock, _displayBounds, _scaleFactor, _dockViews.Count);
            var packed = CanvasRailLayout.DisplacedCenters(_layout.Dock, slots, hoverScales, _scaleFactor);
            var design = (double)CanvasIconLibrary.DesignSize;

            for (var i = 0; i < _dockViews.Count; i++)
            {
                var view = _dockViews[i];
                var (dipX, dipY) = ToCanvasDip(packed[i].X, packed[i].Y);
                var moved = Math.Abs(dipX - view.RenderedXDip) + Math.Abs(dipY - view.RenderedYDip) > OffsetEpsilonDip;
                view.RenderedXDip = dipX;
                view.RenderedYDip = dipY;
                PlaceDockVisual(view, railDip.X, railDip.Y, design, animate: moved);
            }
        }
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

    private void OrderFreeViews()
    {
        _itemsLayer!.Children.RemoveAll();
        foreach (var view in _freeViews.OrderBy(v => v.Item.Z))
        {
            _itemsLayer.Children.InsertAtTop(view.Visual);
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

    /// <summary>Whether the pointer wants the rail out: inside the trigger band, or on the rail itself.</summary>
    private bool WantsDock(double x, double y)
    {
        if (_dockViews.Count == 0)
        {
            return false;
        }

        var dock = _layout.Dock;
        var width = _displayBounds.Width / _scaleFactor;
        var height = _displayBounds.Height / _scaleFactor;

        var inBand = dock.Edge switch
        {
            CanvasDockEdge.Left => x <= dock.TriggerSizeDip,
            CanvasDockEdge.Right => x >= width - dock.TriggerSizeDip,
            CanvasDockEdge.Top => y <= dock.TriggerSizeDip,
            _ => y >= height - dock.TriggerSizeDip,
        };

        if (inBand)
        {
            return true;
        }

        if (_dock.Phase != CanvasDockPhase.Shown)
        {
            return false;
        }

        var rail = ToDipRect(CanvasRailLayout.RailRect(dock, _displayBounds, _scaleFactor, _dockViews.Count, expanded: true));
        return x >= rail.X && x <= rail.X + rail.Width && y >= rail.Y && y <= rail.Y + rail.Height;
    }

    private void UpdateDock(bool wantsExpanded, bool interactionLocked)
    {
        var now = Environment.TickCount64;
        if (_dock.Advance(now, wantsExpanded, interactionLocked))
        {
            ApplyDockPhase(now);
        }
        else if (_railSettlesAt is { } settles && now >= settles)
        {
            // The rail has come to rest: the region can shrink back to what is really there.
            _railSettlesAt = null;
            UpdateRegion();
        }

        ArmDockTimer(now);
    }

    private void ApplyDockPhase(long now)
    {
        var expanded = _dock.Phase == CanvasDockPhase.Shown;
        var target = expanded ? _layout.Dock.ExpandedScale : _layout.Dock.CollapsedScale;
        _railSettlesAt = now + (long)(_layout.Motion.Dock.PeriodSeconds * RailSettlePeriods * 1000);

        if (_rail is not null)
        {
            _railSpring!.InitialValue = _rail.Scale;
            _railSpring.FinalValue = new Vector3((float)target, (float)target, 1);
            _rail.StartAnimation("Scale", _railSpring);
        }

        UpdateRegion();
        _logger.LogInformation("The desktop dock {Action}", expanded ? "expanded" : "retracted");
    }

    /// <summary>Arms the one-shot timer for the next thing the dock will need on its own, if anything.</summary>
    private void ArmDockTimer(long now)
    {
        if (_window == nint.Zero)
        {
            return;
        }

        var delay = _dock.PendingChangeDelayMilliseconds(now);
        if (delay is null && _railSettlesAt is { } settles)
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
        var now = Environment.TickCount64;
        var wants = _pointerXDip is { } x && _pointerYDip is { } y && WantsDock(x, y);

        if (_dock.Advance(now, wants, interactionLocked: _drag is not null))
        {
            ApplyDockPhase(now);

            // The phase changed with no input behind it: the diagnostics must not wait for the next
            // pointer message to tell that story.
            Bump();
        }

        if (_railSettlesAt is { } settles && now >= settles)
        {
            _railSettlesAt = null;
            UpdateRegion();
        }

        ArmDockTimer(now);
    }

    /// <summary>
    /// Rebuilds the window region: the union of everything the canvas can draw — item extents at
    /// their largest possible scale, the rail while it is out, and the dock's trigger band.
    /// Outside it the window is not hit at all, so the desktop below keeps its clicks.
    /// </summary>
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

            var railVisible = _dock.Phase == CanvasDockPhase.Shown
                || _railSettlesAt is not null
                || _layout.Dock.CollapsedScale > VisibleEpsilon;
            if (railVisible && _dockViews.Count > 0)
            {
                AddRect(region, RailRegionRect());
                foreach (var view in _dockViews)
                {
                    AddItemExtent(region, view, _layout.Dock.Proximity.MaxScale);
                }
            }

            AddRect(region, TriggerBandRect());

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

    /// <summary>The rail rectangle while it is out; generous for the time it takes to retract.</summary>
    private PixelRect RailRegionRect() =>
        CanvasRailLayout.RailRect(_layout.Dock, _displayBounds, _scaleFactor, _dockViews.Count, expanded: true);

    /// <summary>The strip along the dock's edge that summons the rail; always part of the region.</summary>
    private PixelRect TriggerBandRect()
    {
        var band = Math.Max(1, (int)Math.Round(_layout.Dock.TriggerSizeDip * _scaleFactor));
        return _layout.Dock.Edge switch
        {
            CanvasDockEdge.Left => new PixelRect(_displayBounds.X, _displayBounds.Y, band, _displayBounds.Height),
            CanvasDockEdge.Right => new PixelRect(_displayBounds.X + _displayBounds.Width - band, _displayBounds.Y, band, _displayBounds.Height),
            CanvasDockEdge.Top => new PixelRect(_displayBounds.X, _displayBounds.Y, _displayBounds.Width, band),
            _ => new PixelRect(_displayBounds.X, _displayBounds.Y + _displayBounds.Height - band, _displayBounds.Width, band),
        };
    }

    /// <summary>Adds a rectangle, given in display pixels, to a window region given in window pixels.</summary>
    private void AddRect(nint region, PixelRect rect)
    {
        var local = new PixelRect(rect.X - _displayBounds.X, rect.Y - _displayBounds.Y, rect.Width, rect.Height);
        var piece = NativeMethods.CreateRectRgn(local.X, local.Y, local.X + local.Width, local.Y + local.Height);
        NativeMethods.CombineRgn(region, region, piece, NativeMethods.RgnOr);
        NativeMethods.DeleteObject(piece);
    }

    /// <summary>The item under the pointer, front to back. Every centre here is canvas DIP.</summary>
    private ItemView? HitTest(double x, double y)
    {
        if (_dock.Phase == CanvasDockPhase.Shown && _dockViews.Count > 0)
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

    private static bool InsideItem(ItemView view, double x, double y)
    {
        var half = CanvasIconLibrary.DesignSize * view.Scale / 2.0;
        return Math.Abs(x - view.RenderedXDip) <= half
            && Math.Abs(y - view.RenderedYDip) <= half;
    }

    private void OnLeftDown(double x, double y)
    {
        _pressXDip = x;
        _pressYDip = y;
        _dragMoved = false;

        var view = HitTest(x, y);
        if (view is null)
        {
            return;
        }

        if (view.Item.Placement != CanvasItemPlacement.Free)
        {
            // Dock items are clicked, not dragged, in this prototype: the click is reported on release.
            return;
        }

        _drag = view;
        _grabXDip = x - view.CenterXDip;
        _grabYDip = y - view.CenterYDip;
        Raise(view);

        // Everything calms down while one item is being moved: the drag is the only motion.
        foreach (var other in _freeViews.Concat(_dockViews))
        {
            StartScale(other, 1.0);
        }

        NativeMethods.SetCapture(_window);

        // The item may travel anywhere on the display before the button comes up, so while it is
        // held the window takes the whole display and nothing is clipped.
        ClearRegion();
        _logger.LogDebug("Canvas drag started on {Id}", view.Item.Id);
        Bump();
    }

    private void DragTo(double x, double y)
    {
        var view = _drag!;
        if (!_dragMoved)
        {
            var dx = x - _pressXDip;
            var dy = y - _pressYDip;
            if ((dx * dx) + (dy * dy) < DragThresholdDip * DragThresholdDip)
            {
                return;
            }

            _dragMoved = true;
        }

        // The item stays fully on the display, which is also what a saved offset reproduces later.
        var design = (double)CanvasIconLibrary.DesignSize;
        var half = view.Item.SizeDip / 2.0;
        var maxX = (_displayBounds.Width / _scaleFactor) - half;
        var maxY = (_displayBounds.Height / _scaleFactor) - half;
        view.CenterXDip = Math.Clamp(x - _grabXDip, Math.Min(half, maxX), Math.Max(half, maxX));
        view.CenterYDip = Math.Clamp(y - _grabYDip, Math.Min(half, maxY), Math.Max(half, maxY));
        view.RenderedXDip = view.CenterXDip;
        view.RenderedYDip = view.CenterYDip;
        view.Visual.Offset = new Vector3((float)(view.CenterXDip - design / 2), (float)(view.CenterYDip - design / 2), 0);
        Bump();
    }

    private void OnLeftUp(double x, double y)
    {
        var view = _drag;
        if (view is not null)
        {
            var moved = _dragMoved;
            _drag = null;

            // Released before the capture does, so the capture-changed message does not look like
            // a second, unexpected end of the same drag.
            NativeMethods.ReleaseCapture();

            if (moved)
            {
                CommitDrag(view);
                _logger.LogInformation(
                    "The canvas item {Id} was dropped at {X:0} / {Y:0} DIP from its {Anchor} anchor",
                    view.Item.Id,
                    view.Item.OffsetXDip,
                    view.Item.OffsetYDip,
                    view.Item.Anchor);
            }
            else
            {
                _logger.LogInformation("The canvas item {Id} was clicked", view.Item.Id);
            }

            UpdateRegion();
            _trackingLeave = false;
            SetPointer(x, y);
            UpdateHover();
            UpdateDock(WantsDock(x, y), interactionLocked: false);
            Bump();
            return;
        }

        var hit = HitTest(x, y);
        if (hit is not null)
        {
            _logger.LogInformation("The canvas item {Id} was clicked", hit.Item.Id);
            Bump();
        }
    }

    /// <summary>Turns the drop point back into the anchor + DIP offsets the layout stores.</summary>
    private void CommitDrag(ItemView view)
    {
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
    }

    private void EndDragCapture()
    {
        _drag = null;
        _dragMoved = false;
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
                if (_drag is not null)
                {
                    DragTo(moveX, moveY);
                }
                else
                {
                    SetPointer(moveX, moveY);
                    UpdateHover();
                    UpdateDock(WantsDock(moveX, moveY), interactionLocked: false);
                    Bump();
                }

                return true;

            case NativeMethods.WmMouseLeave:
                _trackingLeave = false;
                _pointerXDip = null;
                _pointerYDip = null;
                UpdateHover();
                UpdateDock(wantsExpanded: false, interactionLocked: false);
                Bump();
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
                // Capture taken away by someone else: finish the drag where the item is.
                if (_drag is not null)
                {
                    var dropped = _drag;
                    _drag = null;
                    CommitDrag(dropped);
                    _dragMoved = false;
                    ClearRegion();
                    UpdateRegion();
                    UpdateHover();
                    Bump();
                }

                return true;

            case NativeMethods.WmSetCursor:
                return TrySetCursor(lParam, out result);

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
            HoveredScale: hovered?.Scale ?? 1.0,
            DockPhase: _dock.Phase.ToString(),
            DockScale: _dock.Phase == CanvasDockPhase.Shown ? _layout.Dock.ExpandedScale : _layout.Dock.CollapsedScale,
            UpdatesPerSecond: _updates.PerSecond(now),
            Updates: _updates.Total);
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

    private static Vector3 RailCenterPoint(CanvasDockEdge edge, Vector2 size) => edge switch
    {
        CanvasDockEdge.Right => new Vector3(size.X, size.Y / 2, 0),
        CanvasDockEdge.Top => new Vector3(size.X / 2, 0, 0),
        CanvasDockEdge.Bottom => new Vector3(size.X / 2, size.Y, 0),
        _ => new Vector3(0, size.Y / 2, 0),
    };

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
            CanvasItem item,
            ContainerVisual visual,
            SpringVector3NaturalMotionAnimation scaleSpring,
            SpringVector3NaturalMotionAnimation moveSpring,
            double baseScale)
        {
            Item = item;
            Visual = visual;
            ScaleSpring = scaleSpring;
            MoveSpring = moveSpring;
            BaseScale = baseScale;
        }

        internal CanvasItem Item { get; }

        internal ContainerVisual Visual { get; }

        internal SpringVector3NaturalMotionAnimation ScaleSpring { get; }

        internal SpringVector3NaturalMotionAnimation MoveSpring { get; }

        /// <summary>The item's resting size in design units; the hover factor sits on top of it.</summary>
        internal double BaseScale { get; }

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
    }
}
