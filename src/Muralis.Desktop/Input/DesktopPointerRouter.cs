using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Desktop.Interop;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop.Input;

/// <summary>
/// The one place that watches the pointer for the whole desktop. It listens to raw mouse reports on
/// a hidden window of the shell thread — a passive registration: no hook, nothing captured, nothing
/// consumed, every report still reaches every other window unchanged — reads the cursor and the
/// button state behind each report, works out what the pointer is over and publishes the result.
/// </summary>
/// <remarks>
/// <para>
/// It publishes only what concerns the desktop: <see cref="PointerMoved"/> fires while the pointer
/// is over the desktop layer or over a surface of ours, and stops firing — with one
/// <see cref="LeftDesktopRegion"/> — the moment it moves over an ordinary application window.
/// Moving around inside a browser therefore costs the desktop nothing.
/// </para>
/// <para>
/// No polling: a report only wakes the router up. The pointer is read once the flush timer fires —
/// never while handling the report itself — because a report can be delivered before the system has
/// applied the move it describes, which would leave every reading one move behind. The one-shot
/// timer also coalesces a burst into a single reading, to at most one per six milliseconds, and a
/// burst that was still arriving when the flush fired gets one verification look afterwards, so it
/// ends on the position it really ended on. With no subscribers at all — no canvas on the desktop —
/// a report is not even counted.
/// </para>
/// <para>
/// Threading: attached to the shell thread when it starts and released when it ends; every event is
/// raised on that thread, in order, so consumers need no locking of their own. The instance stays
/// valid across shell thread restarts: only the window and the registration come and go.
/// </para>
/// </remarks>
public sealed class DesktopPointerRouter : IDisposable
{
    /// <summary>The one-shot timer that processes the last report of a coalesced burst.</summary>
    private const int FlushTimerId = 1;

    /// <summary>
    /// The delay the flush timer is armed with. The system rounds it up to its own timer
    /// granularity (≈15.6 ms), which is still well inside what feels immediate for a pointer.
    /// </summary>
    private const int FlushDelayMilliseconds = 6;

    /// <summary>At most one dispatch per interval; far above what a display can show.</summary>
    private const int MinimumDispatchIntervalMilliseconds = 6;

    private readonly ILogger<DesktopPointerRouter> _logger;
    private readonly IDesktopPointerSampler _sampler;
    private readonly Func<long> _clock;
    private readonly PointerDispatchGate _gate = new(MinimumDispatchIntervalMilliseconds);
    private readonly CanvasUpdateRate _rate = new();
    private readonly NativeMethods.WindowProc _windowProc;

    private string? _className;
    private nint _instance;
    private nint _window;
    private bool _flushArmed;
    private bool _tailPending;
    private bool _hasReading;
    private bool _disposed;
    private long _reports;
    private long _dispatches;
    private DesktopPointerContext _context = DesktopPointerContext.Foreign;

    public DesktopPointerRouter(ILogger<DesktopPointerRouter> logger)
        : this(logger, new Win32PointerSampler(), () => Environment.TickCount64)
    {
    }

    /// <summary>The seam the tests use: any pointer source, any clock, no Windows.</summary>
    internal DesktopPointerRouter(ILogger<DesktopPointerRouter> logger, IDesktopPointerSampler sampler, Func<long> clock)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(clock);

        _logger = logger;
        _sampler = sampler;
        _clock = clock;
        _windowProc = OnWindowMessage;
    }

    /// <summary>The pointer moved over the desktop layer or over one of our surfaces.</summary>
    public event EventHandler<DesktopPointerEventArgs>? PointerMoved;

    /// <summary>The pointer came onto the desktop: the desktop layer or one of our surfaces.</summary>
    public event EventHandler<DesktopPointerEventArgs>? EnteredDesktopRegion;

    /// <summary>The pointer left the desktop for an ordinary window; no moves follow until it returns.</summary>
    public event EventHandler<DesktopPointerEventArgs>? LeftDesktopRegion;

    /// <summary>The last reading; <see cref="DesktopPointerState.Unknown"/> until something was read.</summary>
    public DesktopPointerState Current { get; private set; } = DesktopPointerState.Unknown;

    /// <summary>Whether the hidden window is up and raw input is registered.</summary>
    internal bool IsAttached => _window != nint.Zero;

    /// <summary>Reports seen, dispatches made and the current dispatch rate, for the overlay.</summary>
    internal PointerRouterStats Stats => new(_reports, _dispatches, _rate.PerSecond(_clock()), Current.Context);

    private bool HasSubscribers => PointerMoved is not null || EnteredDesktopRegion is not null || LeftDesktopRegion is not null;

    /// <summary>
    /// Creates the hidden window and registers the mouse for raw input. Runs on the shell thread,
    /// which is also the thread every event is raised on. Never throws: without a window the canvas
    /// simply keeps using its own window messages.
    /// </summary>
    internal void Attach()
    {
        if (_window != nint.Zero)
        {
            return;
        }

        try
        {
            _instance = NativeMethods.GetModuleHandleW(null);
            _className = "MuralisPointerRouter_" + Guid.NewGuid().ToString("N");
            var windowClass = new NativeMethods.WindowClass
            {
                WndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
                Instance = _instance,
                ClassName = _className,
            };

            if (NativeMethods.RegisterClassW(ref windowClass) == 0)
            {
                throw new InvalidOperationException($"RegisterClass failed ({Marshal.GetLastWin32Error()}).");
            }

            // A real top-level window, never shown: raw input is not delivered to message-only windows.
            _window = NativeMethods.CreateWindowExW(
                0, _className, "Muralis pointer router", NativeMethods.WsPopup, 0, 0, 0, 0,
                nint.Zero, nint.Zero, _instance, nint.Zero);

            if (_window == nint.Zero)
            {
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
            }

            Register(flags: NativeMethods.RidevInputSink, target: _window);

            Current = DesktopPointerState.Unknown;
            _hasReading = false;
            _context = DesktopPointerContext.Foreign;
            _logger.LogInformation("The desktop pointer router is listening to raw mouse input on the shell thread");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The desktop pointer router could not be attached; the desktop keeps using window messages only");
            Detach();
        }
    }

    /// <summary>
    /// Releases the registration and destroys the window. Runs on the shell thread; safe to call
    /// when nothing is attached.
    /// </summary>
    internal void Detach()
    {
        if (_window != nint.Zero)
        {
            Register(flags: NativeMethods.RidevRemove, target: nint.Zero);
            NativeMethods.DestroyWindow(_window);
            _window = nint.Zero;
            _logger.LogInformation("The desktop pointer router released its raw mouse input registration");
        }

        if (_className is not null)
        {
            NativeMethods.UnregisterClassW(_className, _instance);
            _className = null;
        }

        _flushArmed = false;
        _tailPending = false;
        _hasReading = false;
        _context = DesktopPointerContext.Foreign;
        Current = DesktopPointerState.Unknown;
    }

    /// <summary>
    /// Reads the pointer once, outside the report stream: for a consumer that has just mounted while
    /// the pointer may already be resting in its region. Runs on the shell thread.
    /// </summary>
    /// <remarks>
    /// Whether the hidden window exists only decides whether reports keep coming afterwards, not
    /// whether this one read is useful, so the read happens for any consumer that is listening.
    /// </remarks>
    internal void SampleOnce()
    {
        if (!HasSubscribers)
        {
            return;
        }

        var now = _clock();
        _gate.MarkDispatched(now);
        Dispatch(now);
    }

    /// <summary>
    /// One raw mouse report: marks that the pointer moved and asks for the flush timer. Returns true
    /// when the caller should arm it, which tests use to drive the coalescing without a window.
    /// </summary>
    internal bool HandlePointerReport()
    {
        if (!HasSubscribers)
        {
            return false;
        }

        _reports++;

        if (_flushArmed)
        {
            // The burst is already being coalesced. This report's move may still be in flight when
            // the flush reads, so the burst asks for one verification look after that reading.
            _tailPending = true;
            return false;
        }

        // The report only says the pointer moved; the reading happens in HandleFlushTimer once the
        // system has applied the move. Reading here would be too early — a report can be delivered
        // before its own move has taken effect, leaving the reading one move behind.
        _flushArmed = true;
        ArmFlushTimer(FlushDelayMilliseconds);
        return true;
    }

    /// <summary>The coalesced report: reads wherever the pointer is now and publishes it.</summary>
    internal void HandleFlushTimer(long nowMilliseconds)
    {
        _flushArmed = false;
        var tail = _tailPending;
        _tailPending = false;

        if (!HasSubscribers)
        {
            return;
        }

        if (!_gate.TryDispatch(nowMilliseconds))
        {
            // Too soon after the previous dispatch: keep the report pending and look again; nothing
            // has been read yet, so nothing is lost.
            _flushArmed = true;
            _tailPending |= tail;
            ArmFlushTimer(_gate.DelayToNextDispatch(nowMilliseconds));
            return;
        }

        Dispatch(nowMilliseconds);

        if (tail)
        {
            // The burst was still arriving when this reading was taken, so that reading may have
            // raced its last move: one more look ends the burst on the position it ended on
            // instead of leaving it one move behind until something moves again.
            _flushArmed = true;
            ArmFlushTimer(FlushDelayMilliseconds);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
    }

    private void Register(uint flags, nint target)
    {
        var devices = new[]
        {
            new NativeMethods.RawInputDevice
            {
                UsagePage = NativeMethods.HidUsagePageGenericDesktop,
                Usage = NativeMethods.HidUsageMouse,
                Flags = flags,
                Target = target,
            },
        };

        if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()))
        {
            var error = Marshal.GetLastWin32Error();
            if ((flags & NativeMethods.RidevRemove) != 0)
            {
                // A removal that fails has nothing left to release.
                _logger.LogDebug("The desktop pointer raw input registration was already gone ({Error})", error);
                return;
            }

            throw new InvalidOperationException($"Raw input registration failed ({error}).");
        }
    }

    private void ArmFlushTimer(int milliseconds)
    {
        if (_window == nint.Zero)
        {
            // Unattached: tests drive the flush themselves, and the shell has no window to arm.
            return;
        }

        NativeMethods.SetTimer(_window, FlushTimerId, (uint)milliseconds, nint.Zero);
    }

    /// <summary>Reads the pointer, publishes the reading and the transitions it implies.</summary>
    private void Dispatch(long nowMilliseconds)
    {
        _dispatches++;
        _rate.Bump(nowMilliseconds);

        var sampled = _sampler.TryRead(out var x, out var y, out var buttons, out var context);
        if (!sampled)
        {
            // A locked desktop, for one, cannot be read: nothing is known about the pointer any more.
            x = 0;
            y = 0;
            buttons = DesktopPointerButtons.None;
            context = DesktopPointerContext.Foreign;
        }

        var state = new DesktopPointerState(x, y, context, buttons);
        var moved = !_hasReading || Current.X != state.X || Current.Y != state.Y;
        var wasOnDesktop = _context != DesktopPointerContext.Foreign;
        var isOnDesktop = context != DesktopPointerContext.Foreign;

        Current = state;
        _hasReading = true;
        _context = context;

        if (!isOnDesktop)
        {
            if (wasOnDesktop)
            {
                Raise(LeftDesktopRegion, state);
            }

            return;
        }

        if (!wasOnDesktop)
        {
            Raise(EnteredDesktopRegion, state);
        }

        if (moved || !wasOnDesktop)
        {
            Raise(PointerMoved, state);
        }
    }

    /// <summary>Raises to every subscriber; one failing consumer never stops the others or the shell thread.</summary>
    private void Raise(EventHandler<DesktopPointerEventArgs>? handlers, DesktopPointerState state)
    {
        if (handlers is null)
        {
            return;
        }

        var args = new DesktopPointerEventArgs(state);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<DesktopPointerEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A desktop pointer subscriber failed to handle a pointer event");
            }
        }
    }

    private nint OnWindowMessage(nint hWnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.WmInput:
                try
                {
                    HandlePointerReport();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A desktop pointer report could not be processed");
                }

                // The report itself is only a signal — the position is read when the flush timer
                // fires — and every one of them goes on to DefWindowProc, which is the documented
                // way to let the system release the raw input buffer.
                break;

            case NativeMethods.WmTimer when (int)wParam == FlushTimerId:
                NativeMethods.KillTimer(hWnd, FlushTimerId);
                try
                {
                    HandleFlushTimer(_clock());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A coalesced desktop pointer report could not be processed");
                }

                break;
        }

        return NativeMethods.DefWindowProcW(hWnd, message, wParam, lParam);
    }
}
