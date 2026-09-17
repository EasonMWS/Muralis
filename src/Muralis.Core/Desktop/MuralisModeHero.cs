using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.Core.Desktop;

/// <summary>
/// What the Muralis Mode hero is showing. This is presentation state and nothing else: the desktop
/// itself has two modes, and every state here is read from one of them.
/// </summary>
public enum MuralisModeHeroState
{
    /// <summary>The native desktop is in place and Muralis Mode can be entered.</summary>
    Ready,

    /// <summary>Muralis Mode was asked for and the desktop is being rearranged.</summary>
    Entering,

    /// <summary>Muralis Mode is running.</summary>
    Active,

    /// <summary>The native desktop was asked for and the icons are on their way back.</summary>
    Exiting,

    /// <summary>The last request did not take; the desktop really is native again.</summary>
    Error,
}

/// <summary>
/// The home page's Muralis Mode hero, as a state machine. It holds no mode of its own: the desktop's
/// own service is the one place the mode lives, and this reads it, asks it for a change, and reports
/// what it really answered — so a mode switched from the settings page shows up on the home page
/// without a restart, and a change that failed fails open to the native desktop in the hero too.
/// </summary>
/// <remarks>
/// It sits in the headless layer because its behaviour is the part worth testing, and the app layer
/// has no test project to test it in. It holds no UI type and touches no window: everything it needs
/// it gets from <see cref="IDesktopExperienceService"/>, which is also the only service it may use.
/// </remarks>
public sealed class MuralisModeHero : IDisposable
{
    private readonly IDesktopExperienceService _desktop;
    private string? _error;
    private bool _busy;
    private bool _disposed;

    public MuralisModeHero(IDesktopExperienceService desktop)
    {
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _desktop.Changed += OnDesktopChanged;
        State = Resolve(_desktop.Status);
        Error = _desktop.Status.Error;
    }

    /// <summary>What the hero should show right now.</summary>
    public MuralisModeHeroState State { get; private set; }

    /// <summary>What the desktop said went wrong, when it said anything.</summary>
    public string? Error { get; private set; }

    /// <summary>The desktop's own state, which is the one the mode is read from.</summary>
    public DesktopExperienceStatus Status => _desktop.Status;

    /// <summary>True while a request is in flight; the hero's actions are held until it lands.</summary>
    public bool IsBusy => _busy;

    /// <summary>Raised after the hero's state changes, with the state it is now in.</summary>
    public event EventHandler<MuralisModeHeroState>? StateChanged;

    /// <summary>Asks the desktop for Muralis Mode.</summary>
    public Task EnterAsync(CancellationToken cancellationToken = default) =>
        MoveAsync(DesktopExperienceMode.Muralis, MuralisModeHeroState.Entering, cancellationToken);

    /// <summary>Asks the desktop to give Windows its icons back.</summary>
    public Task ExitAsync(CancellationToken cancellationToken = default) =>
        MoveAsync(DesktopExperienceMode.Native, MuralisModeHeroState.Exiting, cancellationToken);

    private async Task MoveAsync(
        DesktopExperienceMode mode,
        MuralisModeHeroState transitional,
        CancellationToken cancellationToken)
    {
        // One request at a time. A second press while the first is still landing would ask the desktop
        // for two modes in a row, and the user would only ever see the last of them.
        if (_busy)
        {
            return;
        }

        _busy = true;
        _error = null;
        Publish(transitional, null);

        try
        {
            await _desktop.ApplyAsync(mode, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The page was left while the desktop was moving. What it really reached is read below,
            // which is the same answer whether or not the move finished.
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;

            // The service reports what is really in place, so a request that failed ends on the native
            // desktop and the hero says so, instead of claiming a mode that is not running.
            Publish(Resolve(_desktop.Status), _error ?? _desktop.Status.Error);
        }
    }

    private MuralisModeHeroState Resolve(DesktopExperienceStatus status) => status.IsMuralis
        ? MuralisModeHeroState.Active
        : status.HasError || _error is not null
            ? MuralisModeHeroState.Error
            : MuralisModeHeroState.Ready;

    private void OnDesktopChanged(object? sender, DesktopExperienceStatus status)
    {
        // A change publishes on every step of its way through, and those intermediate reports describe the
        // desktop the request is moving away from: reading one of them four milliseconds into an enter puts
        // the hero back on the native desktop for the whole time the desktop is really being rearranged.
        // While a request of the hero's own is in flight, the only answer that counts is the one the request
        // itself publishes when it lands, and that one is read from the desktop rather than remembered.
        if (_busy)
        {
            return;
        }

        Publish(Resolve(status), _error ?? status.Error);
    }

    private void Publish(MuralisModeHeroState state, string? error)
    {
        // The same state can arrive with a different reason — a second request failing the same way it
        // did last time reads as the same state to the hero but is a different sentence to the user.
        var arrived = state != State || !string.Equals(error, Error, StringComparison.Ordinal);
        Error = error;
        if (!arrived)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _desktop.Changed -= OnDesktopChanged;
    }
}
