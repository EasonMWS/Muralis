using System.Globalization;
using System.Runtime.InteropServices;
using Muralis.Core.Diagnostics;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Input;

/// <summary>
/// Where a window's client area is on screen, and how many screen pixels one of its device-independent units
/// occupies.
/// </summary>
/// <remarks>
/// <para>
/// The window is asked, rather than the framework. In this app <c>UIElement.TransformToVisual(null)</c> answers
/// in the window's client space and starts at the client's own origin, so a dock window sitting at screen y
/// 1316 reports 0 — and a cursor converted with that answer arrives a thousand pixels below the dock and is
/// never inside it. It was measured behaving exactly that way.
/// </para>
/// <para>
/// The scale is taken from the two sizes the window already reports — its client rectangle in physical pixels
/// and the root element's width in device-independent units — so no display scale has to be guessed or
/// recomputed, and the answer stays right on a mixed-DPI desktop.
/// </para>
/// </remarks>
public readonly record struct WindowClientOrigin(double X, double Y, double Scale)
{
    /// <summary>Reads a window's client origin and scale. Returns a zero origin when the window is not there.</summary>
    public static WindowClientOrigin Capture(nint window, double rootWidthInDip, double rootHeightInDip)
    {
        if (window == nint.Zero || !NativeMethods.GetClientRect(window, out var client))
        {
            return new WindowClientOrigin(0, 0, 1);
        }

        var corner = new NativeMethods.Point { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(window, ref corner))
        {
            return new WindowClientOrigin(0, 0, 1);
        }

        // The client rectangle is in physical pixels and the root element is in device-independent units, so
        // their ratio is the scale the desktop is currently running this window at.
        var scaleX = rootWidthInDip > 0 ? client.Width / rootWidthInDip : 1;
        var scaleY = rootHeightInDip > 0 ? client.Height / rootHeightInDip : 1;
        var scale = scaleX > 0 && scaleY > 0 ? (scaleX + scaleY) / 2 : 1;

        return new WindowClientOrigin(corner.X, corner.Y, scale);
    }
}

/// <summary>A native window rectangle in physical screen pixels, used only by transition diagnostics.</summary>
public readonly record struct WindowScreenBounds(int Left, int Top, int Right, int Bottom)
{
    public static WindowScreenBounds Capture(nint window)
    {
        if (window == nint.Zero || !NativeMethods.GetWindowRect(window, out var rect))
        {
            return default;
        }

        return new WindowScreenBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}

/// <summary>
/// What this process has registered for raw input, asked of Windows rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// Windows allows one registration window per raw input device class per process. Anything that wants to
/// register mouse raw input therefore has to know whether something already has, and this is the only honest
/// way to find out: the answer depends on which services the running mode actually started, not on which
/// classes exist in the source.
/// </para>
/// <para>
/// The Win32 call returns device lists — a device handle and a type — not usage pages, so "mouse" here means
/// the mouse device list the process has registered against. Which usage page was registered is the caller's
/// business and is answered by reading the caller.
/// </para>
/// </remarks>
public static class RawInputRegistry
{
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RimTypeHid = 2;

    /// <summary>The device classes this process currently has raw input registered for.</summary>
    public static RawInputRegistration Current()
    {
        var size = 0u;
        _ = GetRegisteredRawInputDevices(null, ref size, (uint)Marshal.SizeOf<RawInputDeviceList>());
        if (size == 0)
        {
            return new RawInputRegistration(0, 0, 0);
        }

        var list = new RawInputDeviceList[size];
        var got = GetRegisteredRawInputDevices(list, ref size, (uint)Marshal.SizeOf<RawInputDeviceList>());
        if (got == uint.MaxValue)
        {
            return new RawInputRegistration(0, 0, 0);
        }

        var mouse = 0;
        var keyboard = 0;
        var other = 0;
        for (var i = 0u; i < got; i++)
        {
            switch (list[i].DeviceType)
            {
                case RimTypeMouse:
                    mouse++;
                    break;
                case RimTypeKeyboard:
                    keyboard++;
                    break;
                case RimTypeHid:
                default:
                    other++;
                    break;
            }
        }

        return new RawInputRegistration((int)got, mouse, keyboard + other);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices(
        RawInputDeviceList[]? devices,
        ref uint count,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public nint Device;
        public uint DeviceType;
    }
}

/// <summary>How many raw input registrations this process holds, by device class.</summary>
/// <param name="Total">Every registration, whatever the class.</param>
/// <param name="Mouse">Mouse registrations. At most one window can hold this per process.</param>
/// <param name="Other">Keyboard and HID registrations together.</param>
public readonly record struct RawInputRegistration(int Total, int Mouse, int Other)
{
    /// <summary>Whether this process already owns the one mouse registration Windows allows it.</summary>
    public bool HasMouseOwner => Mouse > 0;
}

/// <summary>
/// A window that exists only to receive raw mouse reports, and the registration that feeds it.
/// </summary>
/// <remarks>
/// <para>
/// This is for consumers that cannot use XAML pointer events: a passive tool window that never takes
/// activation is never the active window, and the XAML input system does not deliver pointer moves to it.
/// <c>RIDEV_INPUTSINK</c> asks for reports even while the window is in the background, which is exactly that
/// window's situation.
/// </para>
/// <para>
/// <b>Nothing is captured, consumed or intercepted.</b> The registration is passive, every report is passed
/// on to <c>DefWindowProc</c> as any window does, and no <c>WM_INPUT</c> is swallowed — the rest of Windows
/// sees exactly what it saw before. <c>RIDEV_NOLEGACY</c> and <c>RIDEV_CAPTUREMOUSE</c> are deliberately not
/// used: either would change what other windows receive.
/// </para>
/// <para>
/// <b>The report is only a hint that the mouse moved.</b> <see cref="Moved"/> carries the position from
/// <c>GetCursorPos</c> rather than anything accumulated from <c>RAWMOUSE</c> deltas, so pointer acceleration,
/// absolute versus relative devices, injected input and DPI scaling cannot make the answer drift: the cursor
/// position is the same quantity the rest of the desktop uses, in physical screen pixels.
/// </para>
/// <para>
/// <b>The window owns a thread with an ordinary <c>GetMessage</c> loop.</b> It cannot share one with a UI
/// framework's dispatcher, however idle that dispatcher looks: a dispatcher pumps the windows it created
/// itself, and a window it was never told about has its messages left sitting in the queue. That was measured
/// rather than assumed — a window created on a WinUI thread had its procedure called exactly zero times, while
/// the same window on its own thread is called for every report. One background thread, woken only by the
/// mouse, is a small price for a pointer source that actually delivers.
/// </para>
/// <para>
/// <b>Ownership.</b> Windows allows one registration window per raw input device class per process. The
/// registration is made when <see cref="Open"/> is called, given back by <see cref="Close"/>, and never
/// removed if it was never made.
/// </para>
/// </remarks>
internal sealed class RawPointerWindow : IRawPointerRegistration
{
    /// <summary>The mouse device class already belongs to another window in this process.</summary>
    public const int RawMouseAlreadyOwned = -2;

    /// <summary>How long the constructor waits for the receiving thread to report for duty.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    // Held for the life of the thread: the class's WndProc is a function pointer into this delegate's thunk,
    // and a delegate that is collected takes the thunk with it.
    private readonly NativeMethods.WindowProc _windowProc;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private uint _threadId;
    private string? _className;
    private nint _instance;
    private nint _window;
    private volatile bool _registered;
    private volatile bool _stopping;
    private bool _disposed;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;

    public RawPointerWindow()
    {
        _windowProc = OnWindowMessage;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Muralis raw pointer",
        };
        _thread.Start();

        // The consumer is told when the thread is up rather than being allowed to guess: a pointer source that
        // is not ready yet must not look like one that never reports.
        IsReady = _ready.Wait(StartupTimeout);
    }

    /// <summary>Why the last <see cref="Open"/> failed, as a Win32 error code, or 0 when it succeeded.</summary>
    /// <remarks>
    /// Kept because a registration that quietly does nothing looks exactly like a pointer that never moved.
    /// </remarks>
    public int LastFailure { get; private set; }

    /// <summary>Whether the receiving thread and its window came up.</summary>
    public bool IsReady { get; private set; }

    /// <summary>The receiving window's handle, or zero when there is not one.</summary>
    public nint WindowHandle => _window;

    /// <summary>How many messages the window procedure has been given, of any kind.</summary>
    /// <remarks>
    /// Kept beside <see cref="Reports"/> because two very different failures look identical from outside: a
    /// procedure that is never called means messages are not reaching the window at all, and one that is called
    /// but never with <c>WM_INPUT</c> means the registration is not taking effect.
    /// </remarks>
    public long Messages { get; private set; }

    /// <summary>How many mouse reports have arrived.</summary>
    public long Reports { get; private set; }

    /// <summary>Whether the mouse registration is currently held.</summary>
    public bool IsRegistered => _registered;

    /// <summary>Whether the receiving thread reached its message loop.</summary>
    public bool IsPumping { get; private set; }

    /// <summary>How many messages the pump has taken off the queue, of any kind.</summary>
    /// <remarks>
    /// Separate from <see cref="Messages"/> on purpose: a pump that never takes a message off the queue and a
    /// window procedure that is never called are two different failures, and from outside they look the same.
    /// </remarks>
    public long Dispatched { get; private set; }

    /// <summary>What went wrong inside the receiving thread, or an empty string.</summary>
    public string ThreadFailure { get; private set; } = string.Empty;

    /// <summary>
    /// Every counter at once, as a compact diagnostic.
    /// </summary>
    /// <remarks>
    /// Kept because "the dock did not move" has several unrelated causes behind it — a registration that was
    /// never made, a procedure that is never called, a procedure that is called without <c>WM_INPUT</c>, and a
    /// report that arrives but does not move the cursor — and they are indistinguishable from the outside.
    /// </remarks>
    public string Stats =>
        "\"registered\":" + (_registered ? "true" : "false")
        + ",\"ready\":" + (IsReady ? "true" : "false")
        + ",\"pumping\":" + (IsPumping ? "true" : "false")
        + ",\"dispatched\":" + Dispatched.ToString(CultureInfo.InvariantCulture)
        + ",\"messages\":" + Messages.ToString(CultureInfo.InvariantCulture)
        + ",\"wndProcCalls\":" + WndProcCalls.ToString(CultureInfo.InvariantCulture)
        + ",\"reports\":" + Reports.ToString(CultureInfo.InvariantCulture)
        + ",\"failure\":" + LastFailure.ToString(CultureInfo.InvariantCulture)
        + ",\"hwnd\":" + _window.ToString(CultureInfo.InvariantCulture)
        + ",\"threadFailure\":\"" + ThreadFailure.Replace("\"", "'", StringComparison.Ordinal) + "\""
        + ",\"log\":\"" + MessageLog.Trim() + "\"";

    /// <summary>How many times the window procedure has been entered. Diagnostic only.</summary>
    public long WndProcCalls { get; private set; }

    /// <summary>The message identifiers the window procedure has been given, in order. Diagnostic only.</summary>
    public string MessageLog { get; private set; } = string.Empty;

    /// <summary>
    /// A mouse report, with the cursor in physical screen pixels. Raised on the receiving thread, and only when
    /// the position actually changed.
    /// </summary>
    public event Action<int, int>? Moved;

    /// <summary>
    /// Asks for mouse reports. Never throws: a caller without raw input is still a caller, so a registration
    /// that cannot be made leaves this inert and reports so through <see cref="LastFailure"/>.
    /// </summary>
    /// <remarks>
    /// The work is handed to the receiving thread rather than done here. A raw input registration belongs to
    /// the thread that makes it, so registering from the caller's thread for a window on another one leaves the
    /// reports going to a queue nobody is reading — which looks exactly like a mouse that never moved, and was
    /// measured behaving exactly that way.
    /// </remarks>
    public bool Open()
    {
        if (_disposed || !IsReady || _window == nint.Zero)
        {
            LastFailure = IsReady ? 0 : -1;
            return false;
        }

        if (_registered)
        {
            return true;
        }

        // RegisterRawInputDevices has process-wide last-writer-wins ownership for a device class. Replacing
        // an owner here would silently stop the desktop canvas (or another active consumer), so an occupied
        // class is a normal static-dock fallback, not permission to steal it.
        if (RawInputRegistry.Current().HasMouseOwner)
        {
            LastFailure = RawMouseAlreadyOwned;
            return false;
        }

        LastFailure = 0;
        _ = NativeMethods.PostMessageW(_window, WmRegisterRawInput, nint.Zero, nint.Zero);

        // The registration happens on the other thread, so the answer is waited for rather than assumed.
        _registerDone.Wait(TimeSpan.FromSeconds(2));
        return _registered;
    }

    /// <summary>
    /// Gives the mouse registration back, closes the window and ends the thread. Safe to call more than once and
    /// safe when nothing was ever registered; it never removes a registration it did not make.
    /// </summary>
    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        if (_threadId != 0 && !_stopping)
        {
            _stopping = true;

            if (_registered)
            {
                _ = NativeMethods.PostMessageW(_window, WmUnregisterRawInput, nint.Zero, nint.Zero);
                _unregisterDone.Wait(TimeSpan.FromSeconds(2));
                _registered = false;
            }

            // The window ends with its thread, so the thread is asked to finish and the window is closed from
            // inside the loop that owns it — destroying a window from another thread leaves its messages
            // stranded in a queue nobody is reading. This one goes to the thread on purpose: WM_QUIT has no
            // window, and ending the loop is exactly what it is for.
            _ = NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmQuit, nint.Zero, nint.Zero);
        }

        if (_thread is { IsAlive: true })
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
        Moved = null;
        _ready.Dispose();
        _registerDone.Dispose();
        _unregisterDone.Dispose();
    }

    /// <summary>
    /// Asks the receiving window to report its counters into the drop profile.
    /// </summary>
    /// <remarks>
    /// The counters are public, but a reader on another thread cannot tell "no report has arrived yet" from "a
    /// report arrived and did not move the cursor", and cannot see the window procedure at all. This asks the
    /// procedure itself, from outside the process, which is the only way to separate those cases.
    /// </remarks>
    public void RequestDump()
    {
        if (!_disposed && _window != nint.Zero)
        {
            _ = NativeMethods.PostMessageW(_window, WmDumpCounters, nint.Zero, nint.Zero);
        }
    }

    private static bool Register(uint flags, nint target)
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

        return NativeMethods.RegisterRawInputDevices(
            devices,
            1,
            (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>());
    }

    /// <summary>The receiving thread: create the window, then pump until asked to stop.</summary>
    /// <remarks>
    /// The window is created <b>here</b>, not by <see cref="Open"/> on the caller's thread. A window belongs to
    /// the thread that created it and its messages are delivered to that thread's queue, so a window created
    /// from a UI thread would be left with its reports in a queue the UI framework never reads — which is
    /// exactly the failure this whole class exists to avoid, and one that looks identical to a mouse that never
    /// moved.
    /// </remarks>
    private void Run()
    {
        try
        {
            _ = NativeMethods.PeekMessageW(out _, nint.Zero, 0, 0, NativeMethods.PmNoRemove);
            _threadId = NativeMethods.GetCurrentThreadId();
            IsReady = EnsureWindow();
        }
        catch (Exception ex)
        {
            ThreadFailure = ex.GetType().Name + ": " + ex.Message;
            IsReady = false;
        }

        _ready.Set();

        if (!IsReady)
        {
            return;
        }

        try
        {
            IsPumping = true;

            // Written from the receiving thread itself: whether the loop was ever reached is the difference
            // between a window that is not being pumped and a window that is pumped but never given anything.
            DropProfile.Mark("raw.pump", 0, "\"entered\":true,\"hwnd\":" + _window.ToString(CultureInfo.InvariantCulture));

            // GetMessageW returns 0 for WM_QUIT and -1 on error; anything else is a message to dispatch.
            int result;
            while ((result = NativeMethods.GetMessageW(out var message, nint.Zero, 0, 0)) > 0)
            {
                Dispatched++;
                _ = NativeMethods.TranslateMessage(ref message);
                _ = NativeMethods.DispatchMessageW(ref message);
            }

            ThreadFailure = "loop ended with " + result;
            DropProfile.Mark("raw.pump", 0, "\"entered\":false,\"result\":" + result.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            ThreadFailure = "loop threw " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            IsPumping = false;
        }

        if (_window != nint.Zero)
        {
            NativeMethods.DestroyWindow(_window);
            _window = nint.Zero;
        }

        if (_className is not null)
        {
            NativeMethods.UnregisterClassW(_className, _instance);
            _className = null;
        }
    }

    private bool EnsureWindow()
    {
        if (_window != nint.Zero)
        {
            return true;
        }

        _instance = NativeMethods.GetModuleHandleW(null);
        _className = "MuralisRawPointerWindow";

        var windowClass = new NativeMethods.WindowClass
        {
            Style = 0,
            WndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            ClsExtra = 0,
            WndExtra = 0,
            Instance = _instance,
            Icon = nint.Zero,
            Cursor = nint.Zero,
            Background = nint.Zero,
            MenuName = null,
            ClassName = _className,
        };

        if (NativeMethods.RegisterClassW(ref windowClass) == 0)
        {
            // Already registered by an earlier instance in this process: reusing the class is correct.
            const int ErrorClassAlreadyExists = 1410;
            if (Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                _className = null;
                return false;
            }
        }

        // A message-only window: never drawn, never enumerated, never activated. It only has to exist on a
        // thread that pumps, which this one does.
        _window = NativeMethods.CreateWindowExW(
            0,
            _className,
            "Muralis Raw Pointer",
            0,
            0,
            0,
            0,
            0,
            MessageOnlyParent,
            nint.Zero,
            _instance,
            nint.Zero);

        if (_window == nint.Zero)
        {
            NativeMethods.UnregisterClassW(_className, _instance);
            _className = null;
            return false;
        }

        return true;
    }

    private nint OnWindowMessage(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (DropProfile.IsEnabled)
        {
            MessageLog = MessageLog.Length < 400
                ? MessageLog + message.ToString("X4", CultureInfo.InvariantCulture) + " "
                : MessageLog;
            WndProcCalls++;
        }

        if (message == WmRegisterRawInput)
        {
            // Repeat the ownership check on the registering thread to close the gap between Open's audit and
            // the actual native call. This source only unregisters registrations it successfully acquired.
            if (RawInputRegistry.Current().HasMouseOwner)
            {
                LastFailure = RawMouseAlreadyOwned;
                _registerDone.Set();
                return nint.Zero;
            }

            if (Register(NativeMethods.RidevInputSink, _window))
            {
                _registered = true;
            }
            else
            {
                LastFailure = Marshal.GetLastWin32Error();
            }

            _registerDone.Set();
            return nint.Zero;
        }

        if (message == WmUnregisterRawInput)
        {
            if (_registered)
            {
                _ = Register(NativeMethods.RidevRemove, nint.Zero);
                _registered = false;
            }

            _unregisterDone.Set();
            return nint.Zero;
        }

        if (message == WmDumpCounters)
        {
            // Written from the receiving thread, which is the only thread that sees these counters change, so
            // the reading is a snapshot rather than an average of two moments. A mark rather than an event
            // because an event is only buffered until the next drop writes the buffer out, and a reading that
            // arrives after the run it describes is not a reading.
            DropProfile.Mark("raw.dump", 0, Stats);
            return nint.Zero;
        }

        Messages++;

        if (message == NativeMethods.WmInput)
        {
            Reports++;

            // The report says the mouse moved; the cursor says where it is. Reading it here keeps every
            // downstream number in the same coordinate space as the rest of the desktop, and it is what makes
            // device type, acceleration and injection irrelevant.
            if (NativeMethods.GetCursorPos(out var point) && (point.X != _lastX || point.Y != _lastY))
            {
                _lastX = point.X;
                _lastY = point.Y;
                Moved?.Invoke(point.X, point.Y);
            }
        }

        return NativeMethods.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    /// <summary>Asks the receiving thread to make the registration; it belongs to that thread, not this one.</summary>
    private const uint WmRegisterRawInput = 0x8000 + 11;

    /// <summary>Asks the receiving thread to give the registration back.</summary>
    private const uint WmUnregisterRawInput = 0x8000 + 12;

    /// <summary>Asks the receiving window to write its counters into the drop profile.</summary>
    private const uint WmDumpCounters = 0x8000 + 13;

    private readonly ManualResetEventSlim _registerDone = new(false);
    private readonly ManualResetEventSlim _unregisterDone = new(false);

    /// <summary>
    /// The parent that makes a window message-only: documented as <c>HWND_MESSAGE</c>, <c>(HWND)-3</c>. Not a
    /// real window, so it is written as the cast it is rather than looked up.
    /// </summary>
    private static readonly nint MessageOnlyParent = new(-3);
}
