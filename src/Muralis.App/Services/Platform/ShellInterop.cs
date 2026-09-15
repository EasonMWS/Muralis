using System.Runtime.InteropServices;

namespace Muralis.App.Services.Platform;

/// <summary>
/// Foreground activation helpers shared by the single-instance guard and the activation window.
/// Registered messages are resolved lazily and cached; Windows assigns the same id to the same
/// name across all processes of a session. Explorer lifecycle messages are handled by the
/// desktop layer's shell event source.
/// </summary>
internal static class ShellInterop
{
    /// <summary>Private message a second launch broadcasts to wake the running instance.</summary>
    internal const string ActivationMessageName = "Muralis.ActivateMainWindow.v1";

    internal static readonly nint HwndBroadcast = new(0xFFFF);

    /// <summary>Passed to <see cref="AllowSetForegroundWindow"/> to grant every process the right.</summary>
    internal const uint AsfwAny = 0xFFFFFFFF;

    private static readonly Lazy<uint> ActivationId =
        new(() => RegisterWindowMessageW(ActivationMessageName));

    /// <summary>Message id of <see cref="ActivationMessageName"/>, or 0 when registration failed.</summary>
    internal static uint ActivationRequested => ActivationId.Value;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? moduleName);
}
