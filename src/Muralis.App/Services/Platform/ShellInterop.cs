using System.Runtime.InteropServices;

namespace Muralis.App.Services.Platform;

/// <summary>
/// Shell-level broadcasts and foreground activation helpers shared by the single-instance guard
/// and the shell lifecycle watcher. Registered messages are resolved lazily and cached; Windows
/// assigns the same id to the same name across all processes of a session.
/// </summary>
internal static class ShellInterop
{
    /// <summary>Broadcast by Explorer once the taskbar (and the notification area) exists again.</summary>
    internal const string TaskbarCreatedMessageName = "TaskbarCreated";

    /// <summary>Private message a second launch broadcasts to wake the running instance.</summary>
    internal const string ActivationMessageName = "Muralis.ActivateMainWindow.v1";

    internal const uint WsPopup = 0x80000000;

    internal static readonly nint HwndBroadcast = new(0xFFFF);

    /// <summary>Passed to <see cref="AllowSetForegroundWindow"/> to grant every process the right.</summary>
    internal const uint AsfwAny = 0xFFFFFFFF;

    private static readonly Lazy<uint> TaskbarCreatedId =
        new(() => RegisterWindowMessageW(TaskbarCreatedMessageName));

    private static readonly Lazy<uint> ActivationId =
        new(() => RegisterWindowMessageW(ActivationMessageName));

    /// <summary>Message id of <see cref="TaskbarCreatedMessageName"/>, or 0 when registration failed.</summary>
    internal static uint TaskbarCreated => TaskbarCreatedId.Value;

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
