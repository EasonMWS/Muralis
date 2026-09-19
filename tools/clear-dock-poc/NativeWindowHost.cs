using System.Runtime.InteropServices;

namespace ClearDockPoc;

/// <summary>
/// The dock's window, with a class of its own so the window procedure can be ours.
/// </summary>
/// <remarks>
/// <para>
/// The proof originally used the stock <c>STATIC</c> class, which was enough while the pointer was polled on a
/// timer: nothing ever needed to be delivered to the window. A renderer driven by raw input needs the opposite —
/// a report arrives on the raw source's thread and the frame must be composed on the window's thread, so the
/// window has to be able to receive a message that says "there are samples waiting".
/// </para>
/// <para>
/// The class is registered once per process and the window procedure is static, with the single instance's
/// context held in a field. That is a deliberate simplification for a one-window prototype; a second dock window
/// would need the context moved into the window's own user data rather than shared.
/// </para>
/// <para>
/// The class shape is the one the product already uses for its own raw input window, which is known to register
/// on this platform. Getting there took two corrections worth recording, because neither failure names itself:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>WNDCLASSW</c> has <b>ten</b> members and is <b>72</b> bytes on 64-bit. Padding it to 80, as
/// <c>WNDCLASSEXW</c> requires, makes <c>RegisterClassW</c> fail with <c>ERROR_INVALID_PARAMETER</c> — the size
/// is only correct for the <c>Ex</c> call, and the two are not interchangeable.
/// </item>
/// <item>
/// The procedure must be an <c>IntPtr</c> obtained from <c>Marshal.GetFunctionPointerForDelegate</c>, not a
/// delegate field, and the string members need <c>LPWStr</c> marshalling.
/// </item>
/// </list>
/// </remarks>
internal static class NativeWindowHost
{
    /// <summary>Posted by the input adapter when the first sample of a batch is queued.</summary>
    internal const uint WmPointerBatch = WmApp + 1;

    private const uint WmApp = 0x8000;
    private const string ClassName = "MuralisClearDockWindow";
    private const int ErrorClassAlreadyExists = 1410;

    private static nint _instance;
    private static WndProc? _procedure;
    private static nint _procedurePointer;
    private static Action? _onPointerBatch;
    private static bool _registered;

    /// <summary>Creates the dock window. Returns zero when the class or the window could not be created.</summary>
    internal static nint Create(
        long exStyle, string title, int x, int y, int width, int height, Action onPointerBatch)
    {
        ArgumentNullException.ThrowIfNull(onPointerBatch);

        if (!_registered)
        {
            _procedure = Dispatch;

            // The pointer is kept for the life of the class: it points into this delegate's thunk, so a delegate
            // that stops being referenced would leave the class pointing at freed code.
            _procedurePointer = Marshal.GetFunctionPointerForDelegate(_procedure);
            _instance = GetModuleHandleW(null);

            var windowClass = new WindowClass
            {
                Style = 0,
                WndProc = _procedurePointer,
                ClsExtra = 0,
                WndExtra = 0,
                Instance = _instance,
                Icon = nint.Zero,
                Cursor = LoadCursorW(nint.Zero, IdiApplication),
                Background = nint.Zero,   // never painted: every pixel comes from the layered surface
                MenuName = null,
                ClassName = ClassName,
            };

            if (RegisterClassW(ref windowClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorClassAlreadyExists)
                {
                    Console.Error.WriteLine($"RegisterClassW failed: {error}");
                    return nint.Zero;
                }
            }

            _registered = true;
        }

        _onPointerBatch = onPointerBatch;

        var hwnd = CreateWindowExW(
            exStyle, ClassName, title, WsPopup,
            x, y, width, height, nint.Zero, nint.Zero, _instance, nint.Zero);

        if (hwnd == nint.Zero)
        {
            Console.Error.WriteLine($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        return hwnd;
    }

    private static nint Dispatch(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == WmPointerBatch)
        {
            _onPointerBatch?.Invoke();
            return nint.Zero;
        }

        // WM_PAINT is never handled: the window has no non-client area and every pixel it shows comes from
        // UpdateLayeredWindow, so validating is all that is required to stop Windows asking again.
        if (message == WmPaint)
        {
            var paint = default(PaintStruct);
            _ = BeginPaint(hwnd, out paint);
            _ = EndPaint(hwnd, ref paint);
            return nint.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);

    /// <summary>The native <c>WNDCLASSW</c>: ten members, 72 bytes on 64-bit.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint DeviceContext;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;
        public int Reserved1;
        public int Reserved2;
        public int Reserved3;
        public int Reserved4;
        public int Reserved5;
        public int Reserved6;
        public int Reserved7;
        public int Reserved8;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint WsPopup = 0x80000000;
    private const uint WmPaint = 0x000F;
    private const int IdiApplication = 32512;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WindowClass windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        long exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint hwnd, ref PaintStruct paint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint instance, int name);
}
