using System.Runtime.InteropServices;

// Reports the raw input devices registered for this process, without registering anything.
//
// Windows allows one registration window per raw input device class per process. Before the dock registers
// mouse raw input, this answers whether something in the process already owns it — and if so, which window.
//
// Note what the Win32 call actually answers: GetRegisteredRawInputDevices returns device *lists*
// (hDevice + dwType), not usage pages. What it is good for is the count and, for a registration made with an
// hwndTarget, the fact that a registration exists. The usage page is the caller's business, so this tool is
// used together with a grep of the source for who registers what.
//
// Usage: phase4d-rawinput

const uint RIM_TYPEMOUSE = 0;
const uint RIM_TYPEKEYBOARD = 1;
const uint RIM_TYPEHID = 2;

var size = 0u;
GetRegisteredRawInputDevices(null, ref size, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>());

if (size == 0)
{
    Console.WriteLine("this process has no raw input devices registered");
    return 0;
}

var list = new RAWINPUTDEVICELIST[size];
var got = GetRegisteredRawInputDevices(list, ref size, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>());
if (got == uint.MaxValue)
{
    Console.Error.WriteLine($"GetRegisteredRawInputDevices failed: {Marshal.GetLastWin32Error()}");
    return 1;
}

Console.WriteLine($"this process has {got} raw input registration(s):");
for (var i = 0u; i < got; i++)
{
    var kind = list[i].dwType switch
    {
        RIM_TYPEMOUSE => "mouse",
        RIM_TYPEKEYBOARD => "keyboard",
        RIM_TYPEHID => "HID",
        _ => $"type {list[i].dwType}",
    };

    Console.WriteLine($"  [{i}] {kind}  hDevice=0x{list[i].hDevice:X}");
}

Console.WriteLine();
Console.WriteLine(got > 0
    ? "VERDICT: this process already registers raw input — find out which device class before adding another."
    : "VERDICT: nothing registered in this process.");

return 0;

[DllImport("user32.dll", SetLastError = true)]
static extern uint GetRegisteredRawInputDevices(RAWINPUTDEVICELIST[]? devices, ref uint count, uint size);

[StructLayout(LayoutKind.Sequential)]
struct RAWINPUTDEVICELIST
{
    public nint hDevice;
    public uint dwType;
}
