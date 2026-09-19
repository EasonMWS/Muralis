using System.Runtime.InteropServices;
using System.Text;

// Standalone check: can a plain Win32 window receive WM_INPUT from mouse_event injection?
//
// This exists to separate "the mechanism does not work here" from "the dock's use of it does not work".
// It registers exactly as the dock does 鈥?generic desktop / mouse, RIDEV_INPUTSINK, on a window of its own 鈥?// then injects a pointer move and waits for a report.
//
// Usage: phase4d-rawtest [seconds]

const uint WM_INPUT = 0x00FF;
const uint RIDEV_INPUTSINK = 0x00000100;
const uint RIDEV_REMOVE = 0x00000001;
const ushort HID_USAGE_PAGE_GENERIC_DESKTOP = 0x0001;
const ushort HID_USAGE_MOUSE = 0x0002;
const uint MOUSEEVENTF_MOVE = 0x0001;
const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

var seconds = args.Length > 0 ? int.Parse(args[0]) : 4;
var UseMessageOnly = args.Length > 1 && args[1] == "message-only";
var reports = 0;
var messages = 0;
var lastError = 0;

// `Muralis.Desktop.Input.RawPointerWindow` is exercised directly with `class` as the second argument. That
// separates "the class is broken" from "the class is broken inside the app", which are very different problems.
if (args.Length > 1 && args[1] == "class")
{
    return ClassProbe(seconds);
}

WndProc proc = (hwnd, msg, wParam, lParam) =>
{
    messages++;
    if (msg == WM_INPUT)
    {
        Interlocked.Increment(ref reports);
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
};

var instance = GetModuleHandleW(null);
var className = "Phase4DRawTest";
var windowClass = new WNDCLASS
{
    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
    hInstance = instance,
    lpszClassName = className,
};

if (RegisterClassW(ref windowClass) == 0)
{
    Console.Error.WriteLine($"RegisterClassW failed: {Marshal.GetLastWin32Error()}");
    return 1;
}

var hwnd = CreateWindowExW(0, className, "raw test", 0, 0, 0, 0, 0, UseMessageOnly ? new nint(-3) : 0, 0, instance, 0);
if (hwnd == 0)
{
    Console.Error.WriteLine($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
    return 1;
}

Console.WriteLine($"window created: hwnd=0x{hwnd:X} message-only={UseMessageOnly}");

var devices = new[]
{
    new RAWINPUTDEVICE
    {
        usUsagePage = HID_USAGE_PAGE_GENERIC_DESKTOP,
        usUsage = HID_USAGE_MOUSE,
        dwFlags = RIDEV_INPUTSINK,
        hwndTarget = hwnd,
    },
};

if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
{
    lastError = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"RegisterRawInputDevices failed: {lastError}");
    return 1;
}

Console.WriteLine("raw mouse registered with RIDEV_INPUTSINK");

// Inject a slow sweep so there is plenty of input to see.
var screen = GetSystemMetrics(0);
var height = GetSystemMetrics(1);
var deadline = DateTime.UtcNow.AddSeconds(seconds);
var step = 0;
while (DateTime.UtcNow < deadline)
{
    var x = 300 + ((step * 37) % 900);
    var y = 300 + ((step * 17) % 400);
    mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, (int)((long)x * 65535 / (screen - 1)), (int)((long)y * 65535 / (height - 1)), 0, 0);
    step++;

    // Pump, exactly as a real message loop would.
    while (PeekMessageW(out var message, 0, 0, 0, 1))
    {
        TranslateMessage(ref message);
        DispatchMessageW(ref message);
    }

    Thread.Sleep(20);
}

Console.WriteLine($"injected {step} moves; window procedure saw {messages} messages, {reports} of them WM_INPUT");
Console.WriteLine(reports > 0
    ? "VERDICT: raw input does arrive for injected mouse movement."
    : "VERDICT: no WM_INPUT arrived. The mechanism itself is not delivering in this environment.");

// Give the registration back.
devices[0].dwFlags = RIDEV_REMOVE;
devices[0].hwndTarget = 0;
RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
DestroyWindow(hwnd);
UnregisterClassW(className, instance);
return reports > 0 ? 0 : 2;

// Exercises the real class from Muralis.Desktop, exactly as the dock uses it: construct, Open, inject, read.
int ClassProbe(int seconds)
{
    Muralis.Desktop.Input.RawPointerWindow? source = null;

    try
    {
        source = new Muralis.Desktop.Input.RawPointerWindow();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"constructing RawPointerWindow threw {ex.GetType().Name}: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"constructed: ready={source.IsReady} hwnd=0x{source.WindowHandle:X}");
    var moved = 0;
    source.Moved += (_, _) => Interlocked.Increment(ref moved);

    var opened = source.Open();
    Console.WriteLine($"Open() -> {opened} registered={source.IsRegistered} failure={source.LastFailure}");

    var screen = GetSystemMetrics(0);
    var height = GetSystemMetrics(1);
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    var step = 0;
    while (DateTime.UtcNow < deadline)
    {
        var x = 300 + ((step * 37) % 900);
        var y = 300 + ((step * 17) % 400);
        mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, (int)((long)x * 65535 / (screen - 1)), (int)((long)y * 65535 / (height - 1)), 0, 0);
        step++;
        Thread.Sleep(20);
    }

    Console.WriteLine($"injected {step}; dispatched={source.Dispatched} messages={source.Messages} reports={source.Reports} moved={moved}");
    Console.WriteLine($"pumping={source.IsPumping} threadFailure='{source.ThreadFailure}'");
    source.Dispose();
    return source.Reports > 0 ? 0 : 2;

[DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassW(ref WNDCLASS c);[DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterClassW(string n, nint i);
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
[DllImport("user32.dll")] static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
[DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
[DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
[DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint data, nint extra);
[DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, nint h, uint min, uint max, uint remove);
[DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
[DllImport("user32.dll")] static extern nint DispatchMessageW(ref MSG m);

delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

[StructLayout(LayoutKind.Sequential)]
struct WNDCLASS
{
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
    [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
}

[StructLayout(LayoutKind.Sequential)]
struct RAWINPUTDEVICE
{
    public ushort usUsagePage;
    public ushort usUsage;
    public uint dwFlags;
    public nint hwndTarget;
}

[StructLayout(LayoutKind.Sequential)]
struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public int ptX;
    public int ptY;
}
