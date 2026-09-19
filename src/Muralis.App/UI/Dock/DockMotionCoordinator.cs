using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Muralis.App.UI.Controls;
using Muralis.App.UI.Motion;
using Muralis.Core.Diagnostics;
using Muralis.Core.Motion;
using Muralis.Desktop.Input;

namespace Muralis.App.UI.Dock;

/// <summary>
/// Drives the Nexus motion engine from the pointer and the dock's layout, and writes the result to the icons'
/// magnification layers.
/// </summary>
/// <remarks>
/// <para>
/// The split of work is the point of this class. The engine in <c>Muralis.Core.Motion</c> is pure arithmetic:
/// it knows nothing about XAML, composition or pointers, and it can be unit-tested without a window. This
/// class is the only part that knows about all three, and it keeps the pointer path to the one shape the
/// design allows:
/// </para>
/// <code>
/// pointer position -> cached centres -> engine -> Scale / Translation on the icon's own layer
/// </code>
/// <para>
/// Nothing on that path measures, walks the visual tree, reads settings, extracts an icon, applies a theme,
/// sizes a window, or touches the layout. The cache is rebuilt only when the layout has actually changed,
/// which is what makes it safe to read hundreds of times a second.
/// </para>
/// <para>
/// The pointer arrives from <see cref="DockRawPointerSource"/> rather than from XAML, because the dock's
/// window is a passive one that is never activated and XAML pointer events do not reach it. Where the pointer
/// is, though, is decided by this class: it tests the position against two rectangles it computes from the
/// dock's own geometry, and never asks Windows which window owns the pixel 闂?that question has been shown to
/// answer "the desktop" even when the pointer is over the dock.
/// </para>
/// </remarks>
internal sealed class DockMotionCoordinator : IDisposable
{
    /// <summary>How much a cached position may drift before it counts as a change.</summary>
    private const double CacheTolerance = 0.01;

    /// <summary>A bounded hand-off; large enough for several seconds of a 1000 Hz mouse.</summary>
    private const int PointerQueueCapacity = 4096;

    private readonly DockHost _host;
    private readonly PinnedZoneDrag _drag;
    private readonly Func<nint> _windowHandle;
    private readonly DockMotionProfile _profile;
    private readonly MotionHooks _hooks;
    private readonly RawPointerBroker? _pointerBroker;
    private readonly DispatcherQueue? _dispatcher;
    private readonly DispatcherQueueHandler _drainPointers;
    private readonly object _pointerQueueGate = new();
    private readonly PointerSample[] _pointerQueue = new PointerSample[PointerQueueCapacity];
    private readonly long[] _latencyMicroseconds = new long[PointerQueueCapacity];

    private readonly List<FrameworkElement> _layers = [];

    /// <summary>
    /// Decides which icons share the dock's rail. See <see cref="DockRailMembership"/> for why membership may not
    /// be re-derived from a pose the motion has already written.
    /// </summary>
    private readonly DockRailMembership _rail = new();
    private readonly List<DockIcon> _icons = [];

    private DockMotionEngine _engine = new(0);
    private DockMotionLayout _layout = DockMotionLayout.Empty;
    private double _cachedOriginX;
    private double _cachedOriginY;
    private double _cachedWidth;
    private double _cachedHeight;
    private double _windowScale = 1;
    private double _pointerX;
    private int _lastScreenX;
    private int _lastScreenY;
    private DockPointerRegion _restingScreenRegion;
    private DockPointerRegion _expandedScreenRegion;
    private int _cacheRebuilds;
    private long _pointerUpdates;
    private long _pointerEventsQueued;
    private long _pointerEventsDropped;
    private long _latencyTotalMicroseconds;
    private long _latencyMaximumMicroseconds;
    private long _latencyObserved;
    private int _pointerQueueHead;
    private int _pointerQueueCount;
    private int _latencyCount;
    private int _latencyWrite;
    private bool _pointerDrainQueued;
    private IDisposable? _pointerSubscription;
    private bool _isTracking;
    private bool _isPointerInside;
    private bool _disposed;

    public DockMotionCoordinator(
        DockHost host,
        FrameworkElement tracker,
        FrameworkElement zone,
        PinnedZoneDrag drag,
        Func<nint> windowHandle,
        RawPointerBroker? pointerBroker)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(drag);
        ArgumentNullException.ThrowIfNull(windowHandle);

        _host = host;
        _drag = drag;
        _windowHandle = windowHandle;
        _pointerBroker = pointerBroker;
        _profile = DockMotionProfile.Default;
        _hooks = new MotionHooks(this, tracker);
        _drainPointers = DrainPointers;

        // The raw source reports on its own thread. A fixed queue preserves every report while the dispatcher
        // crosses it onto the UI thread; unlike frame coalescing, later positions never overwrite earlier ones.
        _dispatcher = host.DispatcherQueue;
    }

    /// <summary>The element the cached centres and the pointer are both expressed in.</summary>
    public FrameworkElement Tracker => _hooks.Tracker;

    /// <summary>How many icons the engine can drive at once.</summary>
    public int Capacity => _engine.Samples.Count;

    /// <summary>How many times the icon centres have been measured. The instrumentation reads this.</summary>
    public int CacheRebuilds => _cacheRebuilds;

    /// <summary>How many pointer positions have been applied. The instrumentation reads this.</summary>
    public long PointerUpdates => _pointerUpdates;

    /// <summary>Raw positions that could not be handed to the UI thread.</summary>
    public long PointerEventsDropped => _pointerEventsDropped;

    /// <summary>Mouse reports the raw input source has seen.</summary>
    public long RawReports => _pointerBroker?.Reports ?? 0;

    /// <summary>Whether the raw input registration is currently held.</summary>
    public bool IsRawPointerRegistered => _pointerBroker?.IsRegistered ?? false;

    /// <summary>Whether the pointer is currently inside the dock's interaction region.</summary>
    public bool IsPointerInside => _isPointerInside;

    /// <summary>
    /// How much room above the dock the magnification needs. Straight from the profile, so the window and the
    /// motion cannot disagree about it.
    /// </summary>
    public double VerticalReserveDip => _profile.VerticalReserve;

    /// <summary>
    /// How far the run reaches sideways at the worst pointer position for the dock as it stands now.
    /// </summary>
    public double HorizontalReachDip =>
        DockMotionEngine.ReserveFor(_layout.Count, _profile.BaseIconSize, _profile.SpacingDip, _profile);

    /// <summary>
    /// Raised when the pointer comes onto the dock and when it leaves, so the host can spend the motion bounds.
    /// </summary>
    public event EventHandler<bool>? PointerInsideChanged;

    /// <summary>Starts following the dock. Called when the host is loaded and has a presenter.</summary>
    public void Start()
    {
        if (_isTracking || _disposed)
        {
            return;
        }

        _isTracking = true;
        _startedAt = Environment.TickCount64;
        _host.LayoutUpdated += _hooks.OnLayoutUpdated;

        // The first measurement has to happen before the pointer can arrive, or the first move would have no
        // centres to work from.
        Rebuild(force: true);

        var before = RawInputRegistry.Current();
        _pointerSubscription = _pointerBroker?.Subscribe(OnPointerMovedOnScreen);

        if (DropProfile.IsEnabled)
        {
            var registered = RawInputRegistry.Current();
            DropProfile.Event(
                "motion.pointer.source",
                "\"opened\":" + (_pointerBroker?.IsRegistered ?? false ? "true" : "false")
                + ",\"brokerConsumers\":" + (_pointerBroker?.ConsumerCount ?? 0)
                + ",\"mouseBefore\":" + before.Mouse
                + ",\"mouseAfter\":" + registered.Mouse
                + ",\"dispatcher\":" + (_dispatcher is not null ? "true" : "false"));
        }
    }

    /// <summary>Stops following the dock and puts every icon back to rest.</summary>
    public void Stop()
    {
        if (!_isTracking)
        {
            return;
        }

        _isTracking = false;
        _host.LayoutUpdated -= _hooks.OnLayoutUpdated;
        _pointerSubscription?.Dispose();
        _pointerSubscription = null;
        ClearPointerQueue();
        TracePerformance("stop");
        SetPointerInside(false, "stop");
        Settle();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

    }

    /// <summary>
    /// A mouse report, in physical screen pixels. The raw source's only output, and it arrives on the raw
    /// source's own thread.
    /// </summary>
    /// <remarks>
    /// The position is queued rather than applied here only to cross threads. Every accepted report remains a
    /// separate sample and is applied in order; the queue does not smooth, throttle or collapse to frame rate.
    /// </remarks>
    private void OnPointerMovedOnScreen(int screenX, int screenY)
    {
        if (DropProfile.IsEnabled)
        {
            // One line per report, written out at once: this is the boundary between the raw source's thread and
            // the dock's, and a report that crosses it and one that does not need opposite fixes.
            DropProfile.Mark(
                "motion.screen",
                0,
                "\"x\":" + screenX + ",\"y\":" + screenY
                + ",\"tracking\":" + (_isTracking ? "true" : "false")
                + ",\"disposed\":" + (_disposed ? "true" : "false")
                + ",\"queued\":" + QueueCount()
                + ",\"dispatcher\":" + (_dispatcher is not null ? "true" : "false")
                + ",\"originX\":" + Number(_windowOriginX)
                + ",\"originY\":" + Number(_windowOriginY));
        }

        if (!_isTracking || _disposed)
        {
            return;
        }

        var schedule = false;
        lock (_pointerQueueGate)
        {
            if (_pointerQueueCount == PointerQueueCapacity)
            {
                _pointerEventsDropped++;
                return;
            }

            var tail = (_pointerQueueHead + _pointerQueueCount) % PointerQueueCapacity;
            _pointerQueue[tail] = new PointerSample(screenX, screenY, Stopwatch.GetTimestamp());
            _pointerQueueCount++;
            _pointerEventsQueued++;

            if (!_pointerDrainQueued)
            {
                _pointerDrainQueued = true;
                schedule = true;
            }
        }

        if (schedule && (_dispatcher is null || !_dispatcher.TryEnqueue(DispatcherQueuePriority.High, _drainPointers)))
        {
            // XAML may only be touched by its owner thread. If that owner is gone, static Dock is the safe
            // fallback; applying from the raw-input thread would trade a missed enhancement for a crash.
            ClearPointerQueue(countAsDropped: true);
        }
    }

    private void DrainPointers()
    {
        if (DropProfile.IsEnabled)
        {
            DropProfile.Mark("motion.drain", 0, "\"thread\":" + Environment.CurrentManagedThreadId);
        }

        while (TryDequeuePointer(out var sample))
        {
            RecordLatency(sample.CapturedAt);
            ApplyPointer(sample.ScreenX, sample.ScreenY);
        }
    }

    /// <summary>
    /// Reports the raw source's counters once, shortly after it opens.
    /// </summary>
    /// <remarks>
    /// One reading, taken on work the framework was doing anyway, and taken whether or not anything arrived.
    /// Without it a source that never receives a report and a source whose reports land outside the dock's
    /// region both leave the same trace 闂?no motion 闂?and they need opposite fixes.
    /// </remarks>
    private void ProbeRawOnce()
    {
        if (_pointerBroker is null || _rawProbed)
        {
            return;
        }

        var elapsed = Environment.TickCount64 - _startedAt;
        if (elapsed < RawProbeDelayMs)
        {
            return;
        }

        _rawProbed = true;
        Trace(
            "raw",
            ",\"registered\":" + (_pointerBroker.IsRegistered ? "true" : "false")
            + ",\"consumers\":" + Number(_pointerBroker.ConsumerCount)
            + ",\"elapsed\":" + Number(elapsed));
    }

    private long _startedAt;
    private bool _rawProbed;

    /// <summary>How long to wait before the one reading, so it lands after the pointer has had a chance to move.</summary>
    private const long RawProbeDelayMs = 2000;

    /// <summary>
    /// Turns one screen position into one motion update, and decides whether the pointer is on the dock at all.
    /// </summary>
    private void ApplyPointer(int screenX, int screenY)
    {
        if (!_isTracking || _disposed)
        {
            return;
        }

        // Screen pixels to the dock's own DIP space. The window's client origin is where the dock's space
        // starts on screen, and the scale is how many screen pixels one of its units covers, so the two
        // together are the whole translation 闂?no display scale is assumed anywhere.
        var (x, y) = DockPointerMath.ToDockSpace(
            screenX,
            screenY,
            _windowOriginX,
            _windowOriginY,
            _windowScale);

        _lastScreenX = screenX;
        _lastScreenY = screenY;

        // Enter/leave uses stable physical screen geometry. Resizing the HWND changes its client origin, but
        // it cannot change whether this same screen point is inside the dock's interaction region.
        var inside = _isPointerInside
            ? DockPointerMath.Contains(_expandedScreenRegion, screenX, screenY)
            : DockPointerMath.Contains(_restingScreenRegion, screenX, screenY);
        if (!inside)
        {
            if (DropProfile.IsEnabled)
            {
                // The pointer arrived and the dock said no. Recorded because a pointer that never arrives and a
                // pointer whose every position falls outside the region look the same from the dock's side.
                DropProfile.Mark(
                    "motion.outside",
                    0,
                    "\"x\":" + Number(x) + ",\"y\":" + Number(y)
                    + ",\"wasInside\":" + (_isPointerInside ? "true" : "false")
                    + ",\"originX\":" + Number(_windowOriginX) + ",\"originY\":" + Number(_windowOriginY)
                    + ",\"scale\":" + Number(_windowScale)
                    + ",\"left\":" + Number(_dockLeft) + ",\"right\":" + Number(_dockRight)
                    + ",\"top\":" + Number(_dockTop) + ",\"bottom\":" + Number(_dockBottom));
            }

            if (_isPointerInside)
            {
                // Leaving: the wave goes back to rest in one step, and the window gives its room back. Easing
                // the return is the settle spring's job and it belongs to a later stage.
                SetPointerInside(false, "left-expanded-screen-region");
                Settle();
            }

            return;
        }

        if (!_isPointerInside)
        {
            // Entering: the window has to grow before the wave can be drawn, or the peak is clipped by the
            // window it is drawn in. The expansion is the host's, and it happens once, here 闂?never on the
            // moves that follow.
            SetPointerInside(true, "entered-resting-screen-region");
        }

        _pointerX = x;

        // A drag owns the pointer and the icon it is carrying, so the wave is not drawn while one is in
        // flight. The full integration is a later stage's problem.
        double? pointer = _drag.IsActive ? null : x;
        _engine.Apply(_layout, pointer, _profile);
        _pointerUpdates++;
        Publish();
        Trace("pointer");
    }

    private void SetPointerInside(bool inside, string reason)
    {
        if (_isPointerInside == inside)
        {
            return;
        }

        TraceInteractionTransition(inside, reason);

        if (!inside)
        {
            TracePerformance("leave");
        }

        _isPointerInside = inside;
        for (var i = 0; i < _icons.Count; i++)
        {
            _icons[i].SetNexusMotionActive(inside && _profile.IsMoving);
        }

        PointerInsideChanged?.Invoke(this, inside);
    }

    private void TraceInteractionTransition(bool entering, string reason)
    {
        if (!DropProfile.IsEnabled)
        {
            return;
        }

        var window = WindowScreenBounds.Capture(_windowHandle());
        DropProfile.Mark(
            "motion.bounds.transition",
            0,
            "\"state\":\"" + (entering ? "expanded" : "resting") + "\""
            + ",\"reason\":\"" + reason + "\""
            + ",\"screenX\":" + _lastScreenX
            + ",\"screenY\":" + _lastScreenY
            + ",\"restingLeft\":" + Number(_restingScreenRegion.Left)
            + ",\"restingTop\":" + Number(_restingScreenRegion.Top)
            + ",\"restingRight\":" + Number(_restingScreenRegion.Right)
            + ",\"restingBottom\":" + Number(_restingScreenRegion.Bottom)
            + ",\"expandedLeft\":" + Number(_expandedScreenRegion.Left)
            + ",\"expandedTop\":" + Number(_expandedScreenRegion.Top)
            + ",\"expandedRight\":" + Number(_expandedScreenRegion.Right)
            + ",\"expandedBottom\":" + Number(_expandedScreenRegion.Bottom)
            + ",\"windowLeft\":" + window.Left
            + ",\"windowTop\":" + window.Top
            + ",\"windowRight\":" + window.Right
            + ",\"windowBottom\":" + window.Bottom);
    }

    private bool TryDequeuePointer(out PointerSample sample)
    {
        lock (_pointerQueueGate)
        {
            if (_pointerQueueCount == 0)
            {
                _pointerDrainQueued = false;
                sample = default;
                return false;
            }

            sample = _pointerQueue[_pointerQueueHead];
            _pointerQueueHead = (_pointerQueueHead + 1) % PointerQueueCapacity;
            _pointerQueueCount--;
            return true;
        }
    }

    private void ClearPointerQueue(bool countAsDropped = false)
    {
        lock (_pointerQueueGate)
        {
            if (countAsDropped)
            {
                _pointerEventsDropped += _pointerQueueCount;
            }

            _pointerQueueHead = 0;
            _pointerQueueCount = 0;
            _pointerDrainQueued = false;
        }
    }

    private int QueueCount()
    {
        lock (_pointerQueueGate)
        {
            return _pointerQueueCount;
        }
    }

    private void RecordLatency(long capturedAt)
    {
        var elapsed = Stopwatch.GetTimestamp() - capturedAt;
        var microseconds = Math.Max(0, (long)Math.Round(elapsed * 1_000_000.0 / Stopwatch.Frequency));
        _latencyMicroseconds[_latencyWrite] = microseconds;
        _latencyWrite = (_latencyWrite + 1) % _latencyMicroseconds.Length;
        _latencyCount = Math.Min(_latencyCount + 1, _latencyMicroseconds.Length);
        _latencyTotalMicroseconds += microseconds;
        _latencyMaximumMicroseconds = Math.Max(_latencyMaximumMicroseconds, microseconds);
        _latencyObserved++;
    }

    /// <summary>Writes percentiles only on a transition, never on the pointer hot path.</summary>
    private void TracePerformance(string reason)
    {
        if (!DropProfile.IsEnabled || _latencyCount == 0)
        {
            return;
        }

        var ordered = new long[_latencyCount];
        Array.Copy(_latencyMicroseconds, ordered, _latencyCount);
        Array.Sort(ordered);
        var average = _latencyObserved == 0 ? 0 : _latencyTotalMicroseconds / _latencyObserved;

        DropProfile.Mark(
            "motion.performance",
            0,
            "\"reason\":\"" + reason + "\""
            + ",\"samples\":" + _latencyCount
            + ",\"queued\":" + _pointerEventsQueued
            + ",\"applied\":" + _pointerUpdates
            + ",\"dropped\":" + _pointerEventsDropped
            + ",\"latencyP50Us\":" + Percentile(ordered, 0.50)
            + ",\"latencyP95Us\":" + Percentile(ordered, 0.95)
            + ",\"latencyP99Us\":" + Percentile(ordered, 0.99)
            + ",\"latencyAverageUs\":" + average
            + ",\"latencyMaxUs\":" + _latencyMaximumMicroseconds);
    }

    private static long Percentile(long[] ordered, double percentile)
    {
        var index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private readonly record struct PointerSample(int ScreenX, int ScreenY, long CapturedAt);

    /// <summary>
    /// Rebuilds the cache if the dock has actually moved, and does nothing if it has not. Called from the
    /// layout pass, so it runs at most once per pass and never while the pointer is moving.
    /// </summary>
    private void OnLayoutUpdated()
    {
        if (_isTracking)
        {
            Rebuild(force: false);
            ProbeRawOnce();
        }
    }

    private void Settle()
    {
        _engine.Apply(_layout, null, _profile);

        for (var i = 0; i < _layers.Count; i++)
        {
            DockIconMotion.Release(_layers[i]);
        }
    }

    /// <summary>Writes what the engine worked out to the icons it belongs to.</summary>
    private void Publish()
    {
        var samples = _engine.Samples;
        for (var i = 0; i < _layers.Count && i < _engine.Count; i++)
        {
            var sample = samples[i];
            DockIconMotion.ApplyMagnification(_layers[i], sample.Scale, sample.TranslateX, sample.Lift);
        }
    }

    /// <summary>
    /// Measures the dock and rebuilds the cache when anything has actually moved.
    /// </summary>
    /// <remarks>
    /// Kept off the pointer path entirely: this runs from the layout pass, never from <c>ApplyPointer</c>. What the
    /// comparison below saves is the work after the measurement 闂?the window capture, the layout rebuild, the
    /// regions and the engine's capacity check. It does not save the measurement itself: <see cref="Collect"/>
    /// walks the tree and transforms every icon before the comparison is reached, on every layout pass.
    /// </remarks>
    private void Rebuild(bool force)
    {
        Collect();

        var origin = Tracker
            .TransformToVisual(_host)
            .TransformPoint(new Windows.Foundation.Point(0, 0));

        // Where the window's client area sits on screen and how many screen pixels a unit of the dock's own
        // space occupies, which together turn a screen cursor position into a point in that space. Taken from
        // the window itself: the framework's own answer for this is in client space, not screen space.
        var windowOrigin = WindowClientOrigin.Capture(_windowHandle(), _host.ActualWidth, _host.ActualHeight);

        var centres = new double[_layers.Count];
        var widths = new double[_layers.Count];
        var reach = 0.0;
        var depth = 0.0;
        var left = double.MaxValue;
        var right = double.MinValue;
        var top = double.MaxValue;
        var bottom = double.MinValue;

        for (var i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            var corner = layer
                .TransformToVisual(Tracker)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            widths[i] = layer.ActualWidth;
            centres[i] = corner.X + (layer.ActualWidth / 2.0);
            reach = Math.Max(reach, layer.ActualWidth);

            if (layer.ActualHeight > depth)
            {
                depth = layer.ActualHeight;
            }

            // The dock's drawn bounds: every icon, in the space the pointer is measured in.
            if (corner.X < left) { left = corner.X; }
            if (corner.X + layer.ActualWidth > right) { right = corner.X + layer.ActualWidth; }
            if (corner.Y < top) { top = corner.Y; }
            if (corner.Y + layer.ActualHeight > bottom) { bottom = corner.Y + layer.ActualHeight; }
        }

        // The Shelf is a scroller, so most of its icons are laid out far outside the window and are clipped
        // away by it. They are not part of the dock as far as the pointer is concerned: a region built from
        // them would claim the whole desktop. Only what the window can actually draw is on the dock.
        //
        // These bounds are already in the same units as the window's own size, so no conversion belongs here.
        // The dock's space is DIP (DockPointerMath), Tracker is a full-size child of the host, and the measured
        // icon widths are the elements' DIP widths 闂?which is why the raw right edge reads 3668 to 3759 against
        // a 954 to 1055 wide window without any mismatch: that is the Shelf's real overhang in DIP, exactly what
        // this clamp exists to trim. An earlier revision of this comment claimed a unit mismatch and divided by
        // the window scale. That was wrong and actively harmful: at a display scale above 1 it would have shrunk
        // the bound below the dock's own left edge, making the region empty, and an empty region contains
        // nothing, so the whole dock would have gone inert.
        if (right > left)
        {
            left = Math.Max(left, 0);
            right = Math.Min(right, _host.ActualWidth);
            top = Math.Max(top, 0);
            bottom = Math.Min(bottom, _host.ActualHeight);
        }

        var sane = _layout.Count == centres.Length
            && Math.Abs(origin.X - _cachedOriginX) <= CacheTolerance
            && Math.Abs(origin.Y - _cachedOriginY) <= CacheTolerance
            && Math.Abs(reach - _cachedWidth) <= CacheTolerance
            && Math.Abs(depth - _cachedHeight) <= CacheTolerance
            && Math.Abs(windowOrigin.X - _windowOriginX) <= CacheTolerance
            && Math.Abs(windowOrigin.Y - _windowOriginY) <= CacheTolerance
            && Math.Abs(windowOrigin.Scale - _windowScale) <= CacheTolerance
            && SameBounds(left, right, top, bottom);

        if (!force && sane)
        {
            return;
        }

        _cachedOriginX = origin.X;
        _cachedOriginY = origin.Y;
        _cachedWidth = reach;
        _cachedHeight = depth;
        _windowOriginX = windowOrigin.X;
        _windowOriginY = windowOrigin.Y;
        _windowScale = windowOrigin.Scale;

        if (centres.Length > 0)
        {
            _dockLeft = left;
            _dockRight = right;
            _dockTop = top;
            _dockBottom = bottom;

            var screenLeft = windowOrigin.X + (left * windowOrigin.Scale);
            var screenTop = windowOrigin.Y + (top * windowOrigin.Scale);
            var screenRight = windowOrigin.X + (right * windowOrigin.Scale);
            var screenBottom = windowOrigin.Y + (bottom * windowOrigin.Scale);
            _restingScreenRegion = DockPointerMath.RestingRegion(screenLeft, screenTop, screenRight, screenBottom);
            _expandedScreenRegion = DockPointerMath.ExpandedRegion(
                screenLeft,
                screenTop,
                screenRight,
                screenBottom,
                DockMotionEngine.ReserveFor(
                    centres.Length,
                    _profile.BaseIconSize,
                    _profile.SpacingDip,
                    _profile) * windowOrigin.Scale,
                VerticalReserveDip * windowOrigin.Scale,
                _profile.ExitMarginDip * windowOrigin.Scale);
        }
        else
        {
            // Nothing to be on: every region is empty, so the pointer cannot be inside one.
            _dockLeft = 0;
            _dockRight = 0;
            _dockTop = 0;
            _dockBottom = 0;
            _restingScreenRegion = default;
            _expandedScreenRegion = default;
        }

        _layout = centres.Length == 0 ? DockMotionLayout.Empty : new DockMotionLayout(centres, widths);
        _cacheRebuilds++;

        EnsureCapacity(centres.Length);
        for (var i = 0; i < _icons.Count; i++)
        {
            _icons[i].SetNexusMotionActive(_isPointerInside && _profile.IsMoving);
        }

        Trace("rebuild");

        // A cache that changed while the pointer was away still has to leave the icons where they rest.
        if (!_isPointerInside)
        {
            Settle();
        }
    }

    private bool SameBounds(double left, double right, double top, double bottom) =>
        _dockRight > _dockLeft
        && Math.Abs(left - _dockLeft) <= CacheTolerance
        && Math.Abs(right - _dockRight) <= CacheTolerance
        && Math.Abs(top - _dockTop) <= CacheTolerance
        && Math.Abs(bottom - _dockBottom) <= CacheTolerance;

    private double _dockLeft;
    private double _dockRight;
    private double _dockTop;
    private double _dockBottom;
    private double _windowOriginX;
    private double _windowOriginY;

    /// <summary>
    /// Gives the engine a slot per icon the dock has actually realized.
    /// </summary>
    /// <remarks>
    /// The engine's buffers are allocated once, so this is the only place its size changes and it happens from a
    /// layout pass rather than from the pointer path. A dock that somehow outgrows what the engine was built
    /// for leaves its icons at rest rather than throwing: the motion is an enhancement, and an enhancement that
    /// can take the dock down is worse than no enhancement.
    /// </remarks>
    private void EnsureCapacity(int icons)
    {
        if (icons <= _engine.Samples.Count)
        {
            return;
        }

        try
        {
            _engine = new DockMotionEngine(icons);
        }
        catch (Exception)
        {
            // Out of memory for the engine's buffers: leave the icons at rest rather than take the dock down.
        }
    }

    /// <summary>
    /// Gathers the icons that share the dock's rail, in the order the pointer meets them. The zones are
    /// separate panels, so this is what puts the pinned zone, the Shelf and the utilities on one rail for the
    /// engine to measure along.
    /// </summary>
    /// <remarks>
    /// The rail is one horizontal run, and not every icon in the tree is on it.
    ///
    /// A previous revision of this comment claimed the Shelf is a virtualizing scroller whose unrealized items
    /// report a width of zero, and that this is why thirty-three of forty-five icons reached the engine. That is
    /// false, and it cost a detour. Measured on the running dock with the profiler on: the size filter never
    /// rejected anything at all 闂?input, size-pass and participant counts were forty-five, forty-five and
    /// forty-five on every pass while the pointer was outside the dock 闂?and out-of-band icons never entered a
    /// layout, all five hundred and twenty-nine rebuild records carrying widths of fifty-two and a narrow index
    /// of minus one. The dock also does not virtualize: the Shelf is an ItemsControl with an explicit StackPanel
    /// inside a ScrollViewer, so every item stays realized. The loss is real but it happens in the rail filter
    /// below, and it is caused by the motion's own output, not by the Shelf.
    /// </remarks>
    private void Collect()
    {
        var found = new List<DockIcon>();
        _icons.Clear();
        _layers.Clear();
        Gather(_host, found);

        foreach (var icon in found)
        {
            var layer = icon.MotionTarget;
            if (layer.ActualWidth > 0 && layer.ActualHeight > 0)
            {
                _icons.Add(icon);
            }
        }

        if (_icons.Count == 0)
        {
            return;
        }

        // Membership is decided by a rule that never reads a pose the motion itself wrote; the reasoning and the
        // measurements behind it live on DockRailMembership. What happens here is only the measurement: each
        // icon's own top, paired with its identity, and whether anything is lifted right now.
        var byIdentity = new Dictionary<int, double>(_icons.Count);
        for (var i = 0; i < _icons.Count; i++)
        {
            byIdentity[i] = _icons[i]
                .MotionTarget
                .TransformToVisual(Tracker)
                .TransformPoint(new Windows.Foundation.Point(0, 0))
                .Y;
        }

        foreach (var identity in _rail.Observe(byIdentity, AtRestSampleCount() == _icons.Count))
        {
            _layers.Add(_icons[identity].MotionTarget);
        }

        // Sorted by where they are, never by where the tree happens to list them: the engine measures along a
        // single axis and a run it cannot order is a run it will not draw.
        _layers.Sort(static (a, b) =>
        {
            var ax = a.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            var bx = b.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            return ax.CompareTo(bx);
        });
    }

    /// <summary>
    /// Walks the dock's visual tree and collects every icon on it, in tree order.
    /// </summary>
    private static void Gather(DependencyObject node, List<DockIcon> found)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is DockIcon icon)
            {
                found.Add(icon);
                continue;
            }

            Gather(child, found);
        }
    }

    /// <summary>
    /// How many engine samples are at rest right now.
    /// </summary>
    /// <remarks>
    /// <see cref="Collect"/> compares this against the sized-icon count to decide whether it may hand out a
    /// reference top, on the reasoning that a pass where nothing is lifted is a pass whose measured tops the
    /// motion did not write. Two things make that a good proxy rather than a guarantee, and both are recorded
    /// rather than papered over: the engine's sample slots and the dock's icon list are different populations, so
    /// the comparison is only exact while the engine's capacity equals the icon count; and a freshly built engine
    /// reports every slot at rest by construction, so a capacity rebuild without a release could report rest over
    /// posed icons. The second is not reachable on this dock 闂?capacity reaches forty-five on the first pass and
    /// only ever grows 闂?and neither would lose a member, because membership is held rather than re-derived.
    /// </remarks>
    private int AtRestSampleCount()
    {
        var rest = 0;
        for (var i = 0; i < _engine.Samples.Count; i++)
        {
            if (_engine.Samples[i].IsAtRest)
            {
                rest++;
            }
        }

        return rest;
    }

    /// <summary>
    /// Writes what the pointer path did, for the profiler.
    /// </summary>
    /// <remarks>
    /// Gated on the profiler being switched on, so a shipping build pays one bool read per pointer update and
    /// nothing else: no allocation, no formatting, no file handle.
    /// </remarks>
    private void Trace(string stage, string extra = "")
    {
        if (!DropProfile.IsEnabled)
        {
            return;
        }

        var payload =
            "\"icons\":" + Number(_layout.Count)
            + ",\"capacity\":" + Number(_engine.Samples.Count)
            + ",\"rebuilds\":" + Number(_cacheRebuilds)
            + ",\"updates\":" + Number(_pointerUpdates)
            + ",\"raw\":" + Number(_pointerBroker?.Reports ?? 0)
            + ",\"msgs\":" + Number(_pointerBroker?.Messages ?? 0)
            + ",\"inside\":" + (_isPointerInside ? "true" : "false")
            + ",\"pointer\":" + Number(_pointerX)
            + ",\"originX\":" + Number(_windowOriginX)
            + ",\"originY\":" + Number(_windowOriginY)
            + ",\"dockLeft\":" + Number(_dockLeft)
            + ",\"dockRight\":" + Number(_dockRight)
            + ",\"dockTop\":" + Number(_dockTop)
            + ",\"dockBottom\":" + Number(_dockBottom)
            + ",\"peak\":" + Number(_engine.PeakScale)
            + ",\"reach\":" + Number(_engine.HorizontalReach)
            + ","
            + DescribeLayout()
            + extra;

        // A mark rather than an event: an event is only buffered until some later work happens to flush the
        // buffer, and a reading that lands after the movement it describes is not a reading. This is the
        // profiler's own path and exists only while the profile is on; the motion itself neither allocates nor
        // touches a file.
        DropProfile.Mark("motion." + stage, 0, payload);
    }

    private static string Number(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? "null"
            : value.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>Reports the cached centres, so a layout the engine refuses can be read rather than guessed at.</summary>
    private string DescribeLayout()
    {
        var centres = _layout.Centres;
        var widths = _layout.Widths;

        // The whole run, not a sample: centres that are increasing at the front and out of order at the back
        // are exactly the case a truncated reading hides.
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < centres.Length; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(centres[i].ToString("F0", CultureInfo.InvariantCulture));
        }

        var widthsText = new System.Text.StringBuilder();
        for (var i = 0; i < widths.Length; i++)
        {
            if (i > 0)
            {
                widthsText.Append(',');
            }

            widthsText.Append(widths[i].ToString("F0", CultureInfo.InvariantCulture));
        }

        // Where the run first goes wrong, which is the one thing a list of numbers does not say.
        var breakAt = -1;
        for (var i = 1; i < centres.Length && breakAt < 0; i++)
        {
            if (centres[i] <= centres[i - 1])
            {
                breakAt = i;
            }
        }

        var narrowAt = -1;
        for (var i = 0; i < widths.Length && narrowAt < 0; i++)
        {
            if (widths[i] <= 0 || double.IsNaN(widths[i]) || double.IsInfinity(widths[i]))
            {
                narrowAt = i;
            }
        }

        return "\"sane\":" + (_layout.IsSane ? "true" : "false")
            + ",\"breakAt\":" + Number(breakAt)
            + ",\"narrowAt\":" + Number(narrowAt)
            + ",\"centres\":\"" + text + "\""
            + ",\"widths\":\"" + widthsText + "\"";
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The dock's layout and pointer subscriptions, in one object so a coordinator can be disposed by dropping
    /// it rather than by remembering which handlers were attached where.
    /// </summary>
    private sealed class MotionHooks
    {
        private readonly DockMotionCoordinator _owner;

        public MotionHooks(DockMotionCoordinator owner, FrameworkElement tracker)
        {
            _owner = owner;
            Tracker = tracker;
        }

        /// <summary>The frame the cached centres and the pointer are both expressed in.</summary>
        public FrameworkElement Tracker { get; }

        public void OnLayoutUpdated(object? sender, object args) => _owner.OnLayoutUpdated();
    }
}
