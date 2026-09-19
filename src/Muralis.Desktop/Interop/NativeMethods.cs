using System.Runtime.InteropServices;

namespace Muralis.Desktop.Interop;

/// <summary>
/// The Win32 surface the desktop host needs: window creation, the desktop worker lookup and
/// the message pump. Kept in one place so the host code reads as plain C#.
/// </summary>
internal static class NativeMethods
{
    internal const uint WsPopup = 0x80000000;

    internal const uint WsExTopmost = 0x00000008;
    internal const uint WsExToolWindow = 0x00000080;
    internal const uint WsExNoActivate = 0x08000000;
    internal const uint WsExNoRedirectionBitmap = 0x00200000;

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;

    internal const uint GwHwndNext = 2;
    internal const uint GwChild = 5;

    internal const uint WmNull = 0x0000;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmClose = 0x0010;

    /// <summary>Ends a message loop that has no window of its own; posted with <c>PostThreadMessageW</c>.</summary>
    internal const uint WmQuit = 0x0012;
    internal const uint WmEraseBackground = 0x0014;
    internal const uint WmSetCursor = 0x0020;
    internal const uint WmDisplayChange = 0x007E;
    internal const uint WmTimer = 0x0113;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmCaptureChanged = 0x0215;
    internal const uint WmMouseLeave = 0x02A3;

    /// <summary>A raw input report is queued for a window registered with <c>RegisterRawInputDevices</c>.</summary>
    internal const uint WmInput = 0x00FF;
    internal const uint WmStopHost = 0x8000 + 1;
    internal const uint WmShellRestarted = 0x8000 + 2;
    internal const uint WmRunWork = 0x8000 + 3;

    /// <summary>A surface content has work waiting for the shell thread that only it can run.</summary>
    internal const uint WmCanvasWork = 0x8000 + 4;

    internal const uint PmNoRemove = 0x0000;

    /// <summary>TrackMouseEvent flags: report when the pointer leaves the window.</summary>
    internal const uint TmeLeave = 0x00000002;

    /// <summary>WM_SETCURSOR hit test: the pointer is over the client area.</summary>
    internal const int HtClient = 1;

    /// <summary>CombineRgn mode: union.</summary>
    internal const int RgnOr = 2;

    internal const int IdcArrow = 32512;
    internal const int IdcHand = 32649;

    /// <summary>The desktop icon list, the window that draws the icons themselves.</summary>
    internal const string IconListClass = "SysListView32";

    /// <summary><c>SW_HIDE</c> / <c>SW_SHOW</c>: the last resort for hiding the icons.</summary>
    internal const int SwHide = 0;
    internal const int SwShow = 5;

    /// <summary>Single-threaded apartment, for the thread that talks to the shell's own view.</summary>
    internal const uint CoInitApartmentThreaded = 0x2;

    /// <summary><c>CLSCTX_LOCAL_SERVER</c>: the shell's windows live in a server of their own.</summary>
    internal const uint ClsCtxLocalServer = 0x4;

    /// <summary>Any context the object can be created in; what a shell object is asked for with.</summary>
    internal const uint ClsCtxAll = 0x17;

    internal const uint SmtoNormal = 0x0000;
    internal const uint SpawnWallpaperWorker = 0x052C;

    /// <summary>Raw input: receive reports even when the app is not in the foreground.</summary>
    internal const uint RidevInputSink = 0x00000100;

    /// <summary>Raw input: removes a registration; the target must be zero.</summary>
    internal const uint RidevRemove = 0x00000001;

    /// <summary>Raw input device selector for a mouse (generic desktop page, mouse usage).</summary>
    internal const ushort HidUsagePageGenericDesktop = 0x0001;
    internal const ushort HidUsageMouse = 0x0002;

    /// <summary>Ancestor walk step that never leaves the window's own parent chain.</summary>
    internal const uint GaParent = 1;

    /// <summary>Asks the shell for the icon's index in its image lists instead of a ready-made HICON.</summary>
    internal const uint ShgfiSysIconIndex = 0x000004000;

    /// <summary>System metrics the canvas reads for its gestures: the drag and double-click rectangles.</summary>
    internal const int SmCxDoubleClick = 36;
    internal const int SmCyDoubleClick = 37;
    internal const int SmCxDrag = 68;
    internal const int SmCyDrag = 69;

    /// <summary>Which of the shell's image lists to open: large is 32, extra large 48, jumbo 256 pixels.</summary>
    internal const int ShilSmall = 0x1;
    internal const int ShilLarge = 0x0;
    internal const int ShilExtraLarge = 0x2;
    internal const int ShilJumbo = 0x4;

    /// <summary>Draw flags for <c>IImageList.GetIcon</c>: keep the icon's own transparency.</summary>
    internal const uint IldTransparent = 0x1;

    /// <summary>The image list the shell keeps icons in, per size.</summary>
    internal static readonly Guid ImageListId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    internal const int VkLButton = 0x01;
    internal const int VkRButton = 0x02;

    internal const uint MonitorDefaultToPrimary = 1;
    internal const int MonitorDpiTypeEffective = 0;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out Rect rect);

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
    internal struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
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

    /// <summary>Input for <see cref="TrackMouseEvent"/>: <c>Size</c> must be filled in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackMouseEventOptions
    {
        public int Size;
        public uint Flags;
        public nint Track;
        public uint HoverTime;
    }

    /// <summary>What the shell reports about one file: its place in the image lists, and its names.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    /// <summary>A GDI bitmap's shape, as reported by <see cref="GetObjectW"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Bitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    /// <summary>The top of a <c>BITMAPINFO</c>; only the 32bpp BI_RGB case is ever asked for.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    /// <summary>The two bitmaps behind an icon: the colour one and the legacy 1bpp mask.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    /// <summary>One raw input source: the device class, how it is taken, and the window that receives it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
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

    /// <summary>
    /// Shapes a window's input and rendering area: outside the region the window is invisible to
    /// the mouse walk — other processes included — and composition content is clipped to it.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowRgn(nint hWnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    /// <summary>Requests the leave notification once per call; re-arm after every WM_MOUSELEAVE.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TrackMouseEvent(ref TrackMouseEventOptions options);

    [DllImport("user32.dll")]
    internal static extern nint LoadCursorW(nint instance, nint cursorName);

    /// <summary>The user's own drag and double-click thresholds, so gestures behave like the rest of Windows.</summary>
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    /// <summary>How long the user's double-click may take, in milliseconds.</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    internal static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetWindow(nint hWnd, uint command);

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
    internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    /// <summary>Effective DPI of a display; <c>shcore</c> reports 96 for DPI-unaware processes.</summary>
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeoutW(nint hWnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetTimer(nint hWnd, nint timerId, uint intervalMs, nint timerProc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint hWnd, nint timerId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsWindow(nint hWnd);

    /// <summary>Whether a window and everything it sits in is set to be drawn at all.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    /// <summary>
    /// Shows or hides a window. Used only as the last resort for the desktop's icon list: it is a
    /// public, reversible window call that leaves nothing behind anywhere, but Explorer builds a new
    /// list on every restart, so it has to be applied again after one.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int command);

    /// <summary>
    /// Enters a COM apartment. <c>S_OK</c> means this call created it and must be balanced with
    /// <see cref="CoUninitialize"/>; <c>S_FALSE</c> means the thread was already in that apartment.
    /// </summary>
    [DllImport("ole32.dll", PreserveSig = true)]
    internal static extern int CoInitializeEx(nint reserved, uint apartment);

    [DllImport("ole32.dll", PreserveSig = true)]
    internal static extern void CoUninitialize();

    /// <summary>
    /// Creates an object by its class id. The result arrives as an RCW, which can be cast to any
    /// interface the object implements — the cast is what asks the object whether it does.
    /// </summary>
    [DllImport("ole32.dll", PreserveSig = true)]
    internal static extern int CoCreateInstance(
        in Guid classId,
        [MarshalAs(UnmanagedType.IUnknown)] object? outer,
        uint context,
        in Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    /// <summary>Posts to the thread queue: reaches the host even when it has no window.</summary>
    /// <remarks>
    /// Distinct from <see cref="PostMessageW"/> for a reason that cost a long detour: a message posted to the
    /// thread queue carries a null window, and <c>DispatchMessageW</c> does nothing at all with such a message.
    /// Work that has to run inside a window procedure — a raw input registration, which belongs to the thread
    /// that makes it — must be posted to the window, not to the thread. Only <c>WM_QUIT</c>, which has no
    /// window by design, belongs here.
    /// </remarks>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetMessageW(out Message message, nint hWnd, uint filterMin, uint filterMax);

    /// <summary>
    /// Also the cheapest way to force a thread's message queue into existence, which must happen
    /// before its thread id can be used with <see cref="PostThreadMessageW"/>.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool PeekMessageW(out Message message, nint hWnd, uint filterMin, uint filterMax, uint removeMessage);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint DispatchMessageW(ref Message message);

    /// <summary>
    /// Registers (or removes, with <see cref="RidevRemove"/>) a passive raw input source. The
    /// reports keep flowing to every other window exactly as before: nothing is captured, consumed
    /// or suppressed, which is why this is preferred over a low level mouse hook.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint deviceCount, uint deviceSize);

    /// <summary>The window under a screen point, as the mouse walk sees it (region and z-order included).</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint WindowFromPoint(Point point);

    /// <summary>Walks the parent chain; never returns the owner, unlike <c>GetParent</c>.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetAncestor(nint hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(nint hWnd, [Out] char[] className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetDesktopWindow();

    /// <summary>Physical button state, readable from a background thread; bit 15 is the down state.</summary>
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>
    /// The shell's opinion about a file: <see cref="ShFileInfo.IconIndex"/> is its place in the
    /// system image lists, which is what icon extraction keys off.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SHGetFileInfoW(string path, uint fileAttributes, ref ShFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern int SHGetImageList(int imageList, in Guid interfaceId, out IImageList imageListPointer);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetIconInfo(nint icon, out IconInfo info);

    /// <summary>Reads a GDI object's shape; for a bitmap it fills <see cref="Bitmap"/>.</summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetObjectW(nint handle, int size, ref Bitmap bitmap);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ReleaseDC(nint hWnd, nint dc);

    /// <summary>
    /// Copies a bitmap's pixels out. A negative <c>Height</c> in the header asks for top-down rows,
    /// which is the order a texture upload wants; the call needs a DC even for 32bpp DIBs.
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetDIBits(nint dc, nint bitmap, uint startScan, uint scanLines, [Out] byte[] bits, ref BitmapInfoHeader header, uint usage);
}
