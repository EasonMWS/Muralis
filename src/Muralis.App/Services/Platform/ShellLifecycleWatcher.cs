using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Muralis.App.Services.Platform;

/// <summary>
/// Hidden top-level window that watches the shell lifecycle. Explorer announces every restart by
/// broadcasting <c>TaskbarCreated</c>; the same window also receives the private activation
/// broadcast a second launch of Muralis sends. Services subscribe to the events instead of digging
/// into shell internals, which keeps the logic in one place when the desktop host moves to its own
/// process (Architecture V2).
/// </summary>
public sealed class ShellLifecycleWatcher : IDisposable
{
    private readonly ILogger<ShellLifecycleWatcher> _logger;
    private readonly TrayInterop.WindowProc _windowProc;
    private readonly uint _taskbarCreatedMessage;
    private readonly uint _activationMessage;
    private nint _window;
    private string? _className;
    private bool _disposed;

    /// <summary>Explorer restarted: the taskbar, notification area and desktop worker were recreated.</summary>
    public event EventHandler? ShellRestarted;

    /// <summary>A second launch asked this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    public ShellLifecycleWatcher(ILogger<ShellLifecycleWatcher> logger)
    {
        _logger = logger;
        _windowProc = OnWindowMessage;

        _taskbarCreatedMessage = ShellInterop.TaskbarCreated;
        _activationMessage = ShellInterop.ActivationRequested;
        if (_taskbarCreatedMessage == 0 || _activationMessage == 0)
        {
            _logger.LogWarning(
                "The shell messages could not be registered (TaskbarCreated: {Taskbar}, activation: {Activation})",
                _taskbarCreatedMessage,
                _activationMessage);
        }

        CreateWatcherWindow();
    }

    private void CreateWatcherWindow()
    {
        try
        {
            var instance = ShellInterop.GetModuleHandleW(null);
            _className = "MuralisShellWatcher_" + Guid.NewGuid().ToString("N");

            var windowClass = new TrayInterop.WindowClass
            {
                WindowProc = _windowProc,
                Instance = instance,
                ClassName = _className,
            };

            if (TrayInterop.RegisterClassW(ref windowClass) == 0)
            {
                throw new InvalidOperationException($"RegisterClass failed ({Marshal.GetLastWin32Error()}).");
            }

            // A real top-level window, never shown. Message-only windows do not receive
            // broadcast messages, which is the whole point of this window.
            _window = TrayInterop.CreateWindowExW(
                0, _className, "Muralis shell watcher", ShellInterop.WsPopup, 0, 0, 0, 0,
                nint.Zero, nint.Zero, instance, nint.Zero);

            if (_window == 0)
            {
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex)
        {
            // Without the watcher the app still runs; it only loses shell recovery.
            _logger.LogError(ex, "Could not create the shell lifecycle watcher");
            Cleanup();
        }
    }

    private nint OnWindowMessage(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (!_disposed && message != 0)
        {
            if (message == _taskbarCreatedMessage)
            {
                _logger.LogInformation("Explorer restarted; notifying shell-dependent services");
                Raise(ShellRestarted, nameof(ShellRestarted));
                return 0;
            }

            if (message == _activationMessage)
            {
                _logger.LogInformation("A second launch asked this instance to show itself");
                Raise(ActivationRequested, nameof(ActivationRequested));
                return 0;
            }
        }

        if (message == TrayInterop.WmDestroy)
        {
            TrayInterop.PostMessageW(hWnd, TrayInterop.WmNull, 0, 0);
            return 0;
        }

        return TrayInterop.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    /// <summary>One failing subscriber must not keep the others (tray, video wallpaper) from reacting.</summary>
    private void Raise(EventHandler? handler, string eventName)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A subscriber of {Event} failed", eventName);
            }
        }
    }

    private void Cleanup()
    {
        if (_window != 0)
        {
            TrayInterop.DestroyWindow(_window);
            _window = 0;
        }

        if (_className is not null)
        {
            TrayInterop.UnregisterClassW(_className, ShellInterop.GetModuleHandleW(null));
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
        Cleanup();
    }
}
