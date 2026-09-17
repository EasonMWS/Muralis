using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Models;

namespace Muralis.Desktop.Modes;

/// <summary>
/// Decides what the desktop is: Explorer's own, a preview of Muralis's items drawn over it, or a
/// takeover with the native icons hidden. It owns no window and no COM: the canvas is the canvas
/// service's and the shell view is the takeover service's, and what happens here is the order they
/// are asked in and what is reported when one of them cannot do its part.
/// </summary>
/// <remarks>
/// <para>
/// The order is the safety: the canvas goes on first and is verified, and only then may the icons be
/// hidden, because hiding them on a desktop with nothing else to click would be taking something away
/// rather than offering a replacement. Giving the desktop back runs the other way round — the icons
/// come back first, and the canvas is removed only once the native desktop is the user's again.
/// </para>
/// <para>
/// What is remembered is what really happened, never what was asked for: a takeover that could not
/// hide the icons is saved as a preview, and a canvas that could not be mounted is not saved at all,
/// so a failure is reported once instead of failing again on every launch.
/// </para>
/// <para>
/// Retired as a product path. The product offers the native desktop and Muralis Mode, and what the
/// product uses from here is the give-back alone; preview and takeover are no longer reachable choices.
/// It is kept for startup recovery and internal diagnostics, and no new product code should reference
/// it.
/// </para>
/// </remarks>
public sealed class DesktopModeService : IDesktopModeService, IDisposable
{
    private readonly ILogger<DesktopModeService> _logger;
    private readonly IDesktopCanvasService _canvas;
    private readonly IDesktopTakeoverService _takeover;
    private readonly IDesktopItemSyncService _sync;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private DesktopMode _mode = DesktopMode.Native;
    private bool _disposed;

    public DesktopModeService(
        ILogger<DesktopModeService> logger,
        IDesktopCanvasService canvas,
        IDesktopTakeoverService takeover,
        IDesktopItemSyncService sync)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(takeover);
        ArgumentNullException.ThrowIfNull(sync);

        _logger = logger;
        _canvas = canvas;
        _takeover = takeover;
        _sync = sync;

        // The two halves both change on their own — a re-apply after a shell restart, a canvas whose
        // mount was lost — and the reported status is composed from them, so a change in either has
        // to reach the page that is showing it.
        _takeover.Changed += OnTakeoverChanged;
        _canvas.StatusChanged += OnCanvasStatusChanged;
    }

    /// <inheritdoc />
    public DesktopModeStatus Status => Compose(_mode, null);

    /// <inheritdoc />
    public event EventHandler<DesktopModeStatus>? Changed;

    /// <inheritdoc />
    public async Task<DesktopModeStatus> RestoreAsync(CancellationToken cancellationToken = default)
    {
        DesktopTakeoverOptions options;
        try
        {
            options = await _canvas.GetTakeoverOptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The saved desktop mode could not be read");
            return await FinishAsync(_mode, ex.Message, persist: false, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyAsync(options.Mode, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DesktopModeStatus> ApplyAsync(DesktopMode mode, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(mode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DesktopModeStatus> RestoreNativeDesktopAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The icons first, and straight through the takeover service: nothing on this path reads
            // the canvas, the layout or the mode, so the native desktop can be given back even when
            // none of them is usable.
            var outcome = await _takeover.RestoreNativeDesktopAsync(cancellationToken).ConfigureAwait(false);
            var error = outcome.Error;

            try
            {
                // Nothing may be clicked while the desktop changes under the pointer, and the canvas is
                // nothing without the desktop it was a front for.
                await _canvas.SuspendInteractionAsync(CancellationToken.None).ConfigureAwait(false);
                await _canvas.DisableAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The canvas could not be taken off the desktop");
                error ??= ex.Message;
            }

            StopWatching();
            return await FinishAsync(DesktopMode.Native, error, persist: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _takeover.Changed -= OnTakeoverChanged;
        _canvas.StatusChanged -= OnCanvasStatusChanged;
        _mutex.Dispose();
    }

    /// <summary>
    /// The whole move, in the order that keeps the user able to reach something at every step. Each
    /// step is verified before the next begins, and a step that did not do its part ends the move with
    /// the desktop where it still is.
    /// </summary>
    private async Task<DesktopModeStatus> ApplyCoreAsync(DesktopMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            return await FinishAsync(_mode, $"'{mode}' is not a desktop mode", persist: false, cancellationToken)
                .ConfigureAwait(false);
        }

        // Anything but a takeover means the native desktop has to be the user's again — including a
        // desktop that is owed a give-back, which is why this runs before the mode is read at all.
        if (mode != DesktopMode.Takeover && NeedsGiveBack())
        {
            var given = await _takeover.ReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (given.State != DesktopTakeoverState.Native)
            {
                return await FinishAsync(mode, given.Error, persist: false, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_takeover.State == DesktopTakeoverState.RecoveryRequired)
        {
            // Nothing is built on a desktop that is owed a give-back: taking it over again would hide
            // icons on a desktop nobody can vouch for. The emergency restore is the way out.
            return await FinishAsync(
                    mode,
                    _takeover.Problem ?? "the native desktop still has to be given back",
                    persist: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (mode == DesktopMode.Native)
        {
            await _canvas.DisableAsync(cancellationToken).ConfigureAwait(false);
            StopWatching();
            return await FinishAsync(DesktopMode.Native, null, persist: true, cancellationToken).ConfigureAwait(false);
        }

        // The canvas first, and only once it is really showing may the icons be hidden.
        var wasShowing = _canvas.Status.State == CanvasPrototypeState.Active;
        var canvas = await _canvas.EnableAsync(cancellationToken).ConfigureAwait(false);
        if (canvas.State != CanvasPrototypeState.Active)
        {
            // Nothing was achieved, so nothing is remembered: the mode stays where it was rather than
            // failing again on every launch.
            return await FinishAsync(
                    _mode,
                    canvas.Error ?? "the canvas could not be put on the desktop",
                    persist: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!wasShowing)
        {
            // The user's own desktop is brought across on the way in, by reference only. It is taken
            // once per showing, not once per mode change: preview and takeover share the canvas.
            await AdoptAsync(cancellationToken).ConfigureAwait(false);
            StartWatching();
        }

        if (mode == DesktopMode.Preview)
        {
            return await FinishAsync(DesktopMode.Preview, null, persist: true, cancellationToken).ConfigureAwait(false);
        }

        var taken = await _takeover.TakeAsync(cancellationToken).ConfigureAwait(false);
        if (taken.State != DesktopTakeoverState.Muralis)
        {
            // The canvas is showing and the icons are the user's, which is a preview and is what is
            // remembered; the takeover's own reason is reported so it is not a silent downgrade.
            return await FinishAsync(
                    DesktopMode.Preview,
                    taken.Error ?? "the native desktop icons could not be hidden",
                    persist: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await FinishAsync(DesktopMode.Takeover, null, persist: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the user's desktop across, and never lets a failure undo the mode: the canvas is on and
    /// the icons are the user's, which is exactly what a preview is.
    /// </summary>
    private async Task AdoptAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);
            if (result.Added.Count > 0)
            {
                _logger.LogInformation(
                    "{Added} items from the user's own desktop were brought onto the canvas",
                    result.Added.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The user's own desktop could not be brought across");
        }
    }

    private void StartWatching()
    {
        try
        {
            _sync.StartWatching();
        }
        catch (Exception ex)
        {
            // A desktop that cannot be watched is still a desktop that works; the manual refresh and
            // the next launch both scan it again.
            _logger.LogWarning(ex, "The user's desktop could not be watched for changes");
        }
    }

    private void StopWatching()
    {
        try
        {
            _sync.StopWatching();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The watch on the user's desktop could not be stopped");
        }
    }

    /// <summary>
    /// Publishes where the desktop ended and remembers the mode when the caller says something was
    /// really achieved. The status is composed from the two services, so it says what is true rather
    /// than what was asked for.
    /// </summary>
    private async Task<DesktopModeStatus> FinishAsync(
        DesktopMode mode,
        string? error,
        bool persist,
        CancellationToken cancellationToken)
    {
        if (persist)
        {
            await PersistAsync(mode, cancellationToken).ConfigureAwait(false);
            var status = Compose(mode, error);
            _logger.LogInformation(
                "The desktop is now {Mode} (takeover {Takeover}, canvas {Canvas}){Problem}",
                mode,
                status.Takeover,
                status.Canvas,
                status.HasError ? $": {error}" : string.Empty);
            Changed?.Invoke(this, status);
            return status;
        }

        var unchanged = Compose(_mode, error);
        _logger.LogWarning("The desktop stayed {Mode}: {Problem}", unchanged.Mode, error ?? "nothing was asked for");
        Changed?.Invoke(this, unchanged);
        return unchanged;
    }

    /// <summary>
    /// Writes the mode where the next launch reads it, keeping everything else the options carry —
    /// the adoption switch and the sources the user turned down are not this method's to change.
    /// </summary>
    private async Task PersistAsync(DesktopMode mode, CancellationToken cancellationToken)
    {
        _mode = mode;

        try
        {
            var options = await _canvas.GetTakeoverOptionsAsync(cancellationToken).ConfigureAwait(false);
            if (options.Mode == mode)
            {
                return;
            }

            options.Mode = mode;
            await _canvas.UpdateTakeoverOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The desktop is in the right state either way; only the memory of it is lost.
            _logger.LogWarning(ex, "The desktop mode could not be saved, so it will be {Mode} again next launch", _mode);
        }
    }

    private DesktopModeStatus Compose(DesktopMode mode, string? error) =>
        new(mode, _takeover.State, _canvas.Status.State, error);

    private bool NeedsGiveBack() =>
        _takeover.State is DesktopTakeoverState.Muralis
            or DesktopTakeoverState.RecoveryRequired
            or DesktopTakeoverState.Disabling;

    private void OnTakeoverChanged(object? sender, DesktopTakeoverOutcome outcome) => PublishCurrent();

    private void OnCanvasStatusChanged(object? sender, CanvasPrototypeStatus status) => PublishCurrent();

    private void PublishCurrent()
    {
        if (_disposed)
        {
            return;
        }

        Changed?.Invoke(this, Compose(_mode, null));
    }
}
