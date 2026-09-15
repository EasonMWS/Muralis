using Microsoft.Extensions.Logging;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Shell;

/// <summary>
/// Turns the raw window messages of the shell event window into .NET events. Contains no Win32
/// calls so the routing rules can be tested without a message loop.
/// </summary>
internal sealed class ShellMessageRouter : IDisposable
{
    private readonly uint _taskbarCreatedMessage;
    private readonly ILogger _logger;
    private bool _disposed;

    public ShellMessageRouter(uint taskbarCreatedMessage, ILogger logger)
    {
        _taskbarCreatedMessage = taskbarCreatedMessage;
        _logger = logger;
    }

    /// <summary>Explorer was restarted: the taskbar, notification area and desktop worker are new.</summary>
    public event EventHandler? ShellRestarted;

    /// <summary>The display configuration changed: resolution, arrangement or monitor set.</summary>
    public event EventHandler? DisplaysChanged;

    /// <summary>
    /// Returns true when the message was a shell lifecycle message and has been consumed.
    /// </summary>
    public bool Handle(uint message)
    {
        if (_disposed || message == NativeMethods.WmNull)
        {
            return false;
        }

        if (message == _taskbarCreatedMessage)
        {
            _logger.LogInformation("Explorer restarted; notifying shell-dependent services");
            Raise(ShellRestarted, nameof(ShellRestarted));
            return true;
        }

        if (message == NativeMethods.WmDisplayChange)
        {
            _logger.LogInformation("The display environment changed");
            Raise(DisplaysChanged, nameof(DisplaysChanged));
            return true;
        }

        return false;
    }

    public void Dispose() => _disposed = true;

    /// <summary>One failing subscriber must not keep the others from reacting.</summary>
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
}
