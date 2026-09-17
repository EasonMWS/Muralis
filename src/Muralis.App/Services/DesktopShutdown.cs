using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Desktop.Shell;

namespace Muralis.App.Services;

/// <summary>
/// Hands the desktop back before the process goes away. The order is the point: the native icons come
/// back and are verified first, then the canvas is taken off the desktop, and only then is the desktop
/// layer itself shut down. Doing it the other way round would tear the canvas out from under a desktop
/// whose icons are still hidden.
/// </summary>
/// <remarks>
/// <para>
/// This is the graceful path, not the safety net. A process that is killed never reaches it, which is
/// exactly what the takeover marker is for; running this first is what keeps the marker from having to
/// be used on an ordinary exit.
/// </para>
/// <para>
/// The dock's window is closed here too, and last. It is a window of its own rather than part of the
/// main one, so leaving it up would keep the process alive with a dock floating over a desktop that has
/// no window behind it — and it is closed last so that the icons are already back by the time the dock
/// that stood in for them goes.
/// </para>
/// <para>
/// Idempotent, and never allowed to throw: a desktop that could not be handed back is reported, and the
/// shutdown carries on, because the alternative is an app that will not close.
/// </para>
/// </remarks>
public sealed class DesktopShutdown
{
    private readonly ILogger<DesktopShutdown> _logger;
    private readonly IDesktopModeService _mode;
    private readonly ICleanDesktopPresentation _cleanDesktop;
    private readonly IDesktopShell _shell;
    private readonly IDockExperienceService _dock;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private bool _done;

    public DesktopShutdown(
        ILogger<DesktopShutdown> logger,
        IDesktopModeService mode,
        ICleanDesktopPresentation cleanDesktop,
        IDesktopShell shell,
        IDockExperienceService dock)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(cleanDesktop);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(dock);

        _logger = logger;
        _mode = mode;
        _cleanDesktop = cleanDesktop;
        _shell = shell;
        _dock = dock;
    }

    /// <summary>Whether the desktop has already been handed back by this shutdown.</summary>
    public bool HasRun => _done;

    /// <summary>
    /// Restores the user's desktop and ends the desktop layer. Safe to call from the UI thread, safe to
    /// call more than once, and safe to call when nothing was ever put on the desktop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_done)
            {
                return;
            }

            _done = true;

            // The icons first, and through the emergency path: at this point the canvas may already be
            // gone and the layout unreadable, and the user's desktop still has to come back.
            try
            {
                var clean = await _cleanDesktop.DeactivateAsync(cancellationToken).ConfigureAwait(false);
                if (clean.IsActive)
                {
                    _logger.LogWarning("Clean Desktop could not restore native icons during shutdown: {Error}", clean.Error);
                }

                var status = await _mode.RestoreNativeDesktopAsync(cancellationToken).ConfigureAwait(false);
                if (status.NeedsRecovery)
                {
                    _logger.LogWarning(
                        "The native desktop could not be verified as given back; the next launch will try again ({Error})",
                        status.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The desktop could not be handed back during shutdown");
            }

            // Every surface is released and the shell thread ends; awaiting this is what makes the
            // desktop layer really handed back rather than merely asked to stop.
            try
            {
                await _shell.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The desktop layer could not be shut down");
            }

            // Last, because until this returns the icons are already back and the dock that stood in
            // for them is still there to cover the moment in between.
            try
            {
                if (!await _dock.ShutdownAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("The dock's window could not be closed as the app quit");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The dock could not be shut down");
            }
        }
        finally
        {
            _mutex.Release();
        }
    }
}
