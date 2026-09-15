using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Muralis.App.Services.Platform;

/// <summary>
/// Hidden top-level window that receives the private broadcast a second launch sends. It does not
/// watch the shell lifecycle - Explorer restarts come from the desktop layer's shell event source -
/// its only job is turning the broadcast into <see cref="ActivationRequested"/> so the running
/// window can come forward.
/// </summary>
public sealed class ActivationWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;

    private readonly ILogger<ActivationWindow> _logger;
    private readonly TrayInterop.WindowProc _windowProc;
    private readonly uint _activationMessage;
    private nint _window;
    private string? _className;
    private bool _disposed;

    /// <summary>A second launch asked this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    public ActivationWindow(ILogger<ActivationWindow> logger)
    {
        _logger = logger;
        _windowProc = OnWindowMessage;

        _activationMessage = ShellInterop.ActivationRequested;
        if (_activationMessage == 0)
        {
            _logger.LogWarning("The activation message could not be registered");
        }

        CreateWindow();
    }

    private void CreateWindow()
    {
        try
        {
            var instance = ShellInterop.GetModuleHandleW(null);
            _className = "MuralisActivationWindow_" + Guid.NewGuid().ToString("N");

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
                0, _className, "Muralis activation", WsPopup, 0, 0, 0, 0,
                nint.Zero, nint.Zero, instance, nint.Zero);

            if (_window == 0)
            {
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex)
        {
            // Without this window the app still runs; a second launch then only shows a message.
            _logger.LogError(ex, "Could not create the activation window");
            Cleanup();
        }
    }

    private nint OnWindowMessage(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (!_disposed && message != 0 && message == _activationMessage)
        {
            _logger.LogInformation("A second launch asked this instance to show itself");
            RaiseActivation();
            return 0;
        }

        if (message == TrayInterop.WmDestroy)
        {
            TrayInterop.PostMessageW(hWnd, TrayInterop.WmNull, 0, 0);
            return 0;
        }

        return TrayInterop.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    /// <summary>One failing subscriber must not take the message loop down with it.</summary>
    private void RaiseActivation()
    {
        var handler = ActivationRequested;
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
                _logger.LogError(ex, "A subscriber of {Event} failed", nameof(ActivationRequested));
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
