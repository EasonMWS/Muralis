using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;

namespace Muralis.Core.Services;

/// <summary>
/// Owns the dock's presence on the desktop. It reads the dock's own settings section, keeps the Shelf
/// behind it alive, and is the only thing that shows or hides the window.
/// </summary>
/// <remarks>
/// Clean Desktop is the reason this is not simply a settings toggle: while it runs it has hidden
/// Explorer's icons, so a dock that the user had switched off must still be up. That requirement is
/// held as a flag here rather than by Clean Desktop showing the window itself, because two services
/// showing and hiding one window is how a mode change ends up hiding the only thing on the screen.
/// </remarks>
public sealed class DockExperienceService : IDockExperienceService, IDisposable
{
    private readonly IDesktopDockHost _host;
    private readonly IDesktopShelfService _shelf;
    private readonly ISettingsService _settings;
    private readonly ILogger<DockExperienceService> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private bool _wanted;
    private bool _required;
    private bool _shown;
    private bool _applied;
    private bool _closed;

    public DockExperienceService(
        IDesktopDockHost host,
        IDesktopShelfService shelf,
        ISettingsService settings,
        ILogger<DockExperienceService> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsAvailable => _host.IsAvailable;

    /// <inheritdoc />
    public bool IsVisible => _shown;

    /// <inheritdoc />
    public event EventHandler<bool>? VisibilityChanged;

    /// <inheritdoc />
    public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) =>
        ReconcileAsync(_settings.Current.Dock.IsVisible, required: null, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        if (_settings.Current.Dock.IsVisible != visible)
        {
            try
            {
                _settings.Update(settings => settings.Dock.IsVisible = visible);
                await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("The dock was switched {State} by the user", visible ? "on" : "off");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The dock's own setting could not be saved");
                return false;
            }
        }

        return await ReconcileAsync(visible, required: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> EnsureVisibleAsync(CancellationToken cancellationToken = default) =>
        ReconcileAsync(wanted: null, required: true, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(CancellationToken cancellationToken = default) =>
        ReconcileAsync(wanted: null, required: false, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed)
            {
                return true;
            }

            await _host.CloseAsync(cancellationToken).ConfigureAwait(false);
            _closed = true;
            _wanted = false;
            _required = false;
            return Publish(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The dock's window could not be closed as the app quit");
            return false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Makes the window match the preferences it now has: whatever the user last asked for, or up
    /// because something is standing on it. Only the change is applied, so a call that asks for what
    /// is already true costs nothing and cannot flicker the window.
    /// </summary>
    private async Task<bool> ReconcileAsync(bool? wanted, bool? required, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The dock has already ended with the app; a request that arrives afterwards must not put
            // the window back up behind a process that is on its way out.
            if (_closed)
            {
                return false;
            }

            if (wanted is bool userChoice)
            {
                _wanted = userChoice;
            }

            if (required is bool needed)
            {
                _required = needed;
            }

            var visible = _wanted || _required;
            if (_applied && visible == _shown)
            {
                return true;
            }

            if (!visible)
            {
                await _host.HideAsync(cancellationToken).ConfigureAwait(false);
                return Publish(false);
            }

            // The Shelf is what makes the dock worth a window, so the folders are read before it
            // appears rather than into a strip that is already on screen showing nothing.
            await _shelf.StartAsync(cancellationToken).ConfigureAwait(false);
            await _host.PrepareAsync(cancellationToken).ConfigureAwait(false);
            await _host.ShowAsync(cancellationToken).ConfigureAwait(false);
            return Publish(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The dock could not be shown or hidden");
            return false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private bool Publish(bool visible)
    {
        _applied = true;
        if (_shown == visible)
        {
            return true;
        }

        _shown = visible;
        _logger.LogInformation(
            "The dock is now {State} (asked for by the user: {Wanted}, required by the desktop: {Required})",
            visible ? "on the desktop" : "hidden",
            _wanted,
            _required);
        VisibilityChanged?.Invoke(this, visible);
        return true;
    }

    public void Dispose() => _mutex.Dispose();
}
