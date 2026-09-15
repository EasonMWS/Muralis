using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Shell;

/// <summary>
/// The single place that watches the Windows shell lifecycle. A hidden top-level window receives
/// the <c>TaskbarCreated</c> broadcast and <c>WM_DISPLAYCHANGE</c> and republishes them as .NET
/// events, so consumers - the tray, the desktop host, future shell modules - never deal with
/// window handles, registered message ids or window procedures.
/// </summary>
public sealed class ShellEventSource : IDisposable
{
    private const string TaskbarCreatedMessageName = "TaskbarCreated";

    private readonly ILogger<ShellEventSource> _logger;
    private readonly ShellMessageRouter _router;
    private readonly NativeMethods.WindowProc _windowProc;
    private readonly nint _instance;
    private nint _window;
    private string? _className;
    private bool _disposed;

    public ShellEventSource(ILogger<ShellEventSource> logger)
    {
        _logger = logger;
        _windowProc = OnWindowMessage;
        _instance = NativeMethods.GetModuleHandleW(null);

        // Windows assigns the same id to this name in every process of the session; Explorer
        // broadcasts it to all top-level windows after recreating the taskbar.
        var taskbarCreated = NativeMethods.RegisterWindowMessageW(TaskbarCreatedMessageName);
        if (taskbarCreated == 0)
        {
            _logger.LogWarning(
                "TaskbarCreated could not be registered ({Error}); Explorer restarts will not be detected",
                Marshal.GetLastWin32Error());
        }

        _router = new ShellMessageRouter(taskbarCreated, logger);
        CreateWindow();
    }

    /// <summary>Explorer was restarted: the taskbar, notification area and desktop worker are new.</summary>
    public event EventHandler? ShellRestarted
    {
        add => _router.ShellRestarted += value;
        remove => _router.ShellRestarted -= value;
    }

    /// <summary>The display configuration changed: resolution, arrangement or monitor set.</summary>
    public event EventHandler? DisplaysChanged
    {
        add => _router.DisplaysChanged += value;
        remove => _router.DisplaysChanged -= value;
    }

    private void CreateWindow()
    {
        try
        {
            _className = "MuralisShellEvents_" + Guid.NewGuid().ToString("N");

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

            // A real top-level window, never shown. Message-only windows do not receive
            // broadcast messages, which is the whole point of this window.
            _window = NativeMethods.CreateWindowExW(
                0, _className, "Muralis shell events", NativeMethods.WsPopup, 0, 0, 0, 0,
                nint.Zero, nint.Zero, _instance, nint.Zero);

            if (_window == 0)
            {
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex)
        {
            // Without this window the app still runs; it only loses shell lifecycle awareness.
            _logger.LogError(ex, "Could not create the shell event window");
            Cleanup();
        }
    }

    private nint OnWindowMessage(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (_router.Handle(message))
        {
            return 0;
        }

        if (message == NativeMethods.WmDestroy)
        {
            // The window is already gone when WM_DESTROY arrives; the post wakes the message
            // loop once more so teardown of dependent windows can finish.
            NativeMethods.PostMessageW(hWnd, NativeMethods.WmNull, 0, 0);
            return 0;
        }

        return NativeMethods.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private void Cleanup()
    {
        if (_window != 0)
        {
            NativeMethods.DestroyWindow(_window);
            _window = 0;
        }

        if (_className is not null)
        {
            NativeMethods.UnregisterClassW(_className, _instance);
            _className = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _router.Dispose();
        Cleanup();
    }
}
