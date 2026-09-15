using System.Runtime.InteropServices;

namespace Muralis.Desktop.Interop;

/// <summary>
/// The Win32 surface the desktop host needs: window creation, the desktop worker lookup and
/// the message pump. Kept in one place so the host code reads as plain C#.
/// </summary>
internal static class NativeMethods
{
    internal const uint WsPopup = 0x80000000;

    internal const uint WsExNoActivate = 0x08000000;
    internal const uint WsExToolWindow = 0x00000080;

    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;

    internal const uint WmNull = 0x0000;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmClose = 0x0010;
    internal const uint WmEraseBackground = 0x0014;
    internal const uint WmDisplayChange = 0x007E;
    internal const uint WmTimer = 0x0113;
    internal const uint WmStopHost = 0x8000 + 1;
    internal const uint WmShellRestarted = 0x8000 + 2;

    internal const uint SmtoNormal = 0x0000;
    internal const uint SpawnWallpaperWorker = 0x052C;

    internal const uint MonitorDefaultToPrimary = 1;
    internal const nint DisplayChangeCheckTimerId = 1;
    internal const uint DisplayChangeCheckIntervalMs = 5000;

    internal delegate nint WindowProc(nint hWnd, uint message, nint wParam, nint lParam);

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
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
    internal struct Message
    {
        public nint Hwnd;
        public uint Value;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClassW(string className, nint instance);

    /// <summary>Returns a system-wide message id for a name, or 0 if the call fails.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessageW(string messageName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool PostMessageW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DefWindowProcW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ClientToScreen(nint hWnd, ref Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeoutW(nint hWnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetTimer(nint hWnd, nint timerId, uint intervalMs, nint timerProc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint hWnd, nint timerId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);

    /// <summary>Posts to the thread queue: reaches the host even when it has no window.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetMessageW(out Message message, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint DispatchMessageW(ref Message message);
}
