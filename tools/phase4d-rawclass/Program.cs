using System.Runtime.InteropServices;
using Muralis.Desktop.Input;

// Exercises the dock's raw pointer source on its own, outside the application.
//
// This separates two failures that look identical from inside the app: a class that cannot construct at all,
// and a class that constructs but never sees a report. It uses the real Muralis.Desktop type, not a copy.
//
// Usage: phase4d-rawclass [seconds]

var seconds = args.Length > 0 ? int.Parse(args[0]) : 4;

RawPointerWindow? source;
try
{
    source = new RawPointerWindow();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"constructing RawPointerWindow threw {ex.GetType().FullName}: {ex.Message}");
    if (ex.InnerException is { } inner)
    {
        Console.Error.WriteLine($"  inner: {inner.GetType().FullName}: {inner.Message}");
    }

    return 1;
}

Console.WriteLine($"constructed: ready={source.IsReady} hwnd=0x{source.WindowHandle:X}");

var moved = 0;
source.Moved += (_, _) => Interlocked.Increment(ref moved);

var opened = source.Open();
Console.WriteLine($"Open() -> {opened}  registered={source.IsRegistered}  failure={source.LastFailure}");

var screen = GetSystemMetrics(0);
var height = GetSystemMetrics(1);
var deadline = DateTime.UtcNow.AddSeconds(seconds);
var step = 0;
while (DateTime.UtcNow < deadline)
{
    var x = 300 + ((step * 37) % 900);
    var y = 300 + ((step * 17) % 400);
    mouse_event(0x0001 | 0x8000, (int)((long)x * 65535 / (screen - 1)), (int)((long)y * 65535 / (height - 1)), 0, 0);
    step++;
    Thread.Sleep(20);
}

Console.WriteLine($"injected {step}; dispatched={source.Dispatched} messages={source.Messages} reports={source.Reports} moved={moved}");
Console.WriteLine($"pumping={source.IsPumping} threadFailure='{source.ThreadFailure}'");

source.Dispose();
Console.WriteLine("disposed");

Console.WriteLine(source.Reports > 0
    ? "VERDICT: the class receives raw reports."
    : "VERDICT: the class receives nothing.");

return source.Reports > 0 ? 0 : 2;

[DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, nint extra);
[DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
