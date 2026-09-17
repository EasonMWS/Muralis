using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop.Takeover;
using Muralis.Desktop.Interop;
using Muralis.Desktop.Shell;

namespace Muralis.Desktop.Takeover;

/// <summary>
/// Takes the native desktop over and gives it back, using the shell's own documented view call and a
/// marker on disk that survives a crash. Every operation is a transaction: it either ends with the
/// desktop verified to be in the state it claims, or with a state that says the desktop is owed a
/// give-back — never with a half-done desktop described as fine.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here replaces Explorer or reaches inside it. The only thing done to the user's desktop is
/// asking the shell to stop drawing the icons, through the same view object the shell itself uses, and
/// the user's own icon visibility is recorded first so that giving the desktop back restores what they
/// had rather than what Muralis thinks they should have.
/// </para>
/// <para>
/// The shell's view is opened per operation on a dedicated apartment thread and closed again at the
/// end of it. Explorer rebuilds the desktop from scratch when it restarts, and a pointer kept from
/// before that is a pointer into a dead object; re-opening costs a handful of calls and removes that
/// whole class of failure. It is also what lets a shell restart be handled by simply doing the last
/// step again.
/// </para>
/// <para>
/// Retired as a product path. Muralis Mode hides Explorer's icons through the clean-desktop
/// presentation instead of a takeover, and the mode that used to reach this is no longer a choice. It
/// stays for the give-back a desktop may still owe at startup, and for internal diagnostics; nothing
/// new should reference it.
/// </para>
/// </remarks>
public sealed class DesktopTakeoverService : IDesktopTakeoverService, IDisposable
{
    /// <summary>How long after a shell restart the re-apply is first tried: the new desktop needs a moment.</summary>
    private static readonly TimeSpan ReapplyFirstDelay = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan ReapplyRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How many times the re-apply is tried before giving up until the next restart.</summary>
    private const int ReapplyAttempts = 8;

    private readonly object _gate = new();
    private readonly ILogger<DesktopTakeoverService> _logger;
    private readonly DesktopTakeoverRecordStore _marker;
    private readonly DesktopTakeoverMachine _machine = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private readonly Lazy<DesktopComThread> _comThread;

    private IDesktopShell? _shell;
    private DesktopIconStrategy _strategy = DesktopIconStrategy.None;
    private NativeDesktopVisualState? _original;
    private bool _disposed;

    public DesktopTakeoverService(
        ILoggerFactory loggerFactory,
        ILogger<DesktopTakeoverService> logger,
        DesktopTakeoverRecordStore marker)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(marker);

        _logger = logger;
        _marker = marker;

        // Started on first use and owned for the life of the app: the apartment has to outlive every
        // operation, but a process that never takes the desktop over never needs the thread at all.
        _comThread = new Lazy<DesktopComThread>(
            () => new DesktopComThread(loggerFactory.CreateLogger<DesktopComThread>()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public DesktopTakeoverState State
    {
        get
        {
            lock (_gate)
            {
                return _machine.State;
            }
        }
    }

    /// <inheritdoc />
    public string? Problem
    {
        get
        {
            lock (_gate)
            {
                return _machine.Problem;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<DesktopTakeoverOutcome>? Changed;

    /// <summary>
    /// Lets the shell announce its restarts here. Optional: without it the takeover still works, but a
    /// restart leaves the native icons visible again until something else asks for the desktop.
    /// </summary>
    public void AttachTo(IDesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.CompareExchange(ref _shell, shell, null) is not null)
        {
            return;
        }

        shell.ShellRestarted += OnShellRestarted;
    }

    /// <inheritdoc />
    public async Task<DesktopTakeoverOutcome> TakeAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_machine.OwnsTheDesktop)
            {
                _logger.LogInformation("The desktop is already Muralis's; nothing to take over");
                return Publish(new DesktopTakeoverOutcome(
                    DesktopTakeoverState.Muralis, _strategy, _original, null));
            }

            if (!_machine.BeginEnable())
            {
                var refusal = _machine.NeedsRecovery
                    ? $"The native desktop still has to be given back first ({_machine.Problem})"
                    : "The desktop is already being changed";
                _logger.LogWarning("The desktop takeover was refused: {Reason}", refusal);
                return Publish(new DesktopTakeoverOutcome(_machine.State, _strategy, _original, refusal));
            }

            Publish(new DesktopTakeoverOutcome(DesktopTakeoverState.Enabling, _strategy, null, null));

            if (!_comThread.Value.IsAvailable)
            {
                return FailEnable("the shell's desktop view could not be reached on this system");
            }

            // The transaction: read the desktop as it is, leave the marker a crash would be recovered
            // from, hide, verify. Each step is what makes the next one safe to undo.
            var step = await _comThread.Value
                .RunAsync(TryTakeOver, cancellationToken)
                .ConfigureAwait(false);

            if (step.Failure is not null)
            {
                return FailEnable(step.Failure);
            }

            _strategy = step.Strategy;
            _original = step.Original;

            // The marker is rewritten now that the rung is known, so a recovery can use the same one to
            // put the icons back. Failing to rewrite it is not fatal: the marker already there says a
            // takeover was active, which is what recovery needs.
            if (!_marker.TrySave(Record(DesktopTakeoverState.Muralis, step.Strategy, step.Original)))
            {
                _logger.LogWarning("The takeover marker could not be updated with the way the icons were hidden");
            }

            _machine.MarkEnabled();
            _logger.LogInformation("The native desktop was taken over with {Strategy}", step.Strategy);
            return Publish(new DesktopTakeoverOutcome(
                DesktopTakeoverState.Muralis, step.Strategy, step.Original, null));
        }
        catch (OperationCanceledException)
        {
            _machine.ResetToNative();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop takeover did not finish");
            return FailEnable(ex.Message);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DesktopTakeoverOutcome> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_machine.BeginDisable())
            {
                return Publish(new DesktopTakeoverOutcome(
                    _machine.State, _strategy, _original, "The desktop is being changed right now"));
            }

            Publish(new DesktopTakeoverOutcome(DesktopTakeoverState.Disabling, _strategy, _original, null));
            return await GiveBackAsync(assumeVisibleWhenUnknown: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DesktopTakeoverOutcome> RestoreNativeDesktopAsync(CancellationToken cancellationToken = default)
    {
        // The emergency path is the one place a give-back is not allowed to be refused. Whatever the
        // machine happened to be doing is set aside first, because being asked for it at all means the
        // desktop may be stuck; then the ordinary give-back runs with the assumption that gives the user
        // their desktop back when nothing was recorded.
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _machine.RequireRecovery(_machine.Problem ?? "an explicit restore of the native desktop was asked for");
            _machine.BeginDisable();

            Publish(new DesktopTakeoverOutcome(DesktopTakeoverState.Disabling, _strategy, _original, null));
            return await GiveBackAsync(assumeVisibleWhenUnknown: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DesktopTakeoverOutcome> RecoverIfNeededAsync(CancellationToken cancellationToken = default)
    {
        DesktopTakeoverMarker marker;
        try
        {
            marker = _marker.Load();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop takeover marker could not be checked");
            return Publish(new DesktopTakeoverOutcome(
                DesktopTakeoverState.Native, DesktopIconStrategy.None, null, ex.Message));
        }

        if (marker.IsUnreadable)
        {
            // A file that cannot be read is the one case where the user's desktop must not be touched:
            // there is no way to know whether the icons are hidden or what they looked like before. It
            // has already been set aside, so the next check starts clean.
            _logger.LogWarning("A desktop takeover marker was found but not used: {Error}", marker.Error);
            return Publish(new DesktopTakeoverOutcome(
                DesktopTakeoverState.Native, DesktopIconStrategy.None, null, marker.Error));
        }

        if (marker.Record is not { TakeoverWasActive: true } record)
        {
            if (marker.Record is not null)
            {
                // A marker from a run that ended cleanly, left behind by a crash after the desktop was
                // given back. There is nothing to undo, so it is simply removed.
                _marker.TryClear();
            }

            return Publish(DesktopTakeoverOutcome.AlreadyNative());
        }

        _logger.LogWarning(
            "A previous run (session {Session}, process {ProcessId}, {When}) left the desktop taken over; giving it back",
            record.SessionId,
            record.ProcessId,
            record.Timestamp);

        // Recovery acts on what the marker says and never on what this process remembers, because this
        // process remembers nothing — the crash is the whole reason the marker exists.
        _strategy = record.Strategy;
        _original = record.OriginalNativeState;

        return await RestoreNativeDesktopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NativeDesktopVisualState> ReadNativeStateAsync(CancellationToken cancellationToken = default)
    {
        if (!_comThread.Value.IsAvailable)
        {
            return NativeDesktopVisualState.AssumedVisible;
        }

        return await _comThread.Value
            .RunAsync(ReadState, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_shell is { } shell)
        {
            shell.ShellRestarted -= OnShellRestarted;
        }

        if (_comThread.IsValueCreated)
        {
            _comThread.Value.Dispose();
        }

        _mutex.Dispose();
    }

    /// <summary>
    /// The takeover transaction, run on the apartment thread. Nothing is touched until the user's own
    /// desktop state has been read and the marker that a crash would be recovered from is on disk, and
    /// a failure after that point puts back whatever was written before it reports itself.
    /// </summary>
    private TakeOverStep TryTakeOver()
    {
        using var view = DesktopShellView.Open(_logger);
        if (view is null)
        {
            return TakeOverStep.Failed("the shell's desktop view could not be opened");
        }

        var original = view.VisualState();
        if (!original.Observed)
        {
            return TakeOverStep.Failed("the desktop's own icon state could not be read");
        }

        if (!original.OriginalIconsVisible)
        {
            // The user keeps their own icons hidden. There is nothing to hide, and hiding them would
            // make the marker claim a change Muralis never made.
            _logger.LogInformation("The native desktop icons are already hidden by the user's own setting");
            return TakeOverStep.Done(DesktopIconStrategy.None, original);
        }

        if (!_marker.TrySave(Record(DesktopTakeoverState.Enabling, DesktopIconStrategy.None, original)))
        {
            // Fail closed. Icons hidden with no marker is the one state a crash cannot undo, so the
            // takeover is refused rather than risking a desktop nobody can give back.
            return TakeOverStep.Failed("the recovery marker could not be written");
        }

        var strategy = HideIcons(view);
        if (strategy is DesktopIconStrategy.None)
        {
            // Roll back: a rung may have written the flag without the icons actually going away, so the
            // recorded word is put back before the takeover reports that it did not happen.
            var rollback = Restore(view, original);
            if (rollback is not null)
            {
                _logger.LogWarning("Rolling the failed takeover back did not verify: {Problem}", rollback);
                return TakeOverStep.Failed(
                    $"the shell would not stop drawing the desktop icons, and putting them back did not verify ({rollback})");
            }

            _marker.TryClear();
            return TakeOverStep.Failed("the shell would not stop drawing the desktop icons");
        }

        return TakeOverStep.Done(strategy, original);
    }

    /// <summary>
    /// The ladder, in the order the rungs are meant to be reached for. Each rung is verified before the
    /// next is tried, so a call that reports success but changed nothing is not taken for one that
    /// worked.
    /// </summary>
    private DesktopIconStrategy HideIcons(DesktopShellView view)
    {
        if (view.CanReadFlags)
        {
            var result = view.SetFolderFlags(ShellViewInterfaces.FolderFlagNoIcons, ShellViewInterfaces.FolderFlagNoIcons);
            if (result == 0 && !view.IconsAreDrawn())
            {
                return DesktopIconStrategy.ShellViewFlags;
            }

            _logger.LogWarning(
                "The shell would not hide the desktop icons through its folder flags ({Result}); falling back to the icon window",
                result);
        }

        if (view.HideIconList() && !view.IconsAreDrawn())
        {
            return DesktopIconStrategy.IconWindow;
        }

        return DesktopIconStrategy.None;
    }

    /// <summary>
    /// Puts the desktop back the way the marker says it was, and clears the marker only once the icons
    /// are verified to be back. A give-back that cannot be verified leaves the marker in place and the
    /// state at <see cref="DesktopTakeoverState.RecoveryRequired"/>, so the next attempt still knows
    /// what to restore.
    /// </summary>
    private async Task<DesktopTakeoverOutcome> GiveBackAsync(
        bool assumeVisibleWhenUnknown,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_comThread.Value.IsAvailable)
            {
                return FailDisable("the shell's desktop view could not be reached on this system");
            }

            // What to restore comes from the marker first, because that is the only record that
            // survives the process which hid the icons. What this process remembers is the second
            // choice, and an assumption is the last.
            var marker = _marker.Load();
            var wanted = marker.Record?.OriginalNativeState
                ?? _original
                ?? (assumeVisibleWhenUnknown ? NativeDesktopVisualState.AssumedVisible : null);

            if (wanted is null)
            {
                if (marker.Error is null)
                {
                    // No marker and nothing remembered: there is no takeover of Muralis's outstanding,
                    // so the desktop is left exactly as it is and that is reported as the success it
                    // is, not as a failure to give something back.
                    _machine.MarkDisabled();
                    _strategy = DesktopIconStrategy.None;
                    _logger.LogInformation("There was no takeover to give back; the native desktop was left alone");
                    return Publish(new DesktopTakeoverOutcome(
                        DesktopTakeoverState.Native, DesktopIconStrategy.None, null, null));
                }

                // A marker that cannot be read is the one case where the desktop must not be guessed
                // at: it may be holding hidden icons and there is no way to know what they looked like.
                // The emergency restore is the way out, and it says that it assumed.
                return FailDisable(marker.Error);
            }

            if (marker.Record?.Strategy is { } recorded && recorded != DesktopIconStrategy.None)
            {
                _strategy = recorded;
            }

            var problem = await _comThread.Value
                .RunAsync(() => Restore(wanted), cancellationToken)
                .ConfigureAwait(false);

            if (problem is not null)
            {
                return FailDisable(problem);
            }

            _marker.TryClear();
            _strategy = DesktopIconStrategy.None;
            _machine.MarkDisabled();
            _logger.LogInformation("The native desktop was given back as it was found");
            return Publish(new DesktopTakeoverOutcome(
                DesktopTakeoverState.Native, DesktopIconStrategy.None, wanted, null));
        }
        catch (OperationCanceledException)
        {
            _machine.RequireRecovery("the give-back was cancelled before it could be verified");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The native desktop could not be given back");
            return FailDisable(ex.Message);
        }
    }

    /// <summary>
    /// Puts the recorded state back on an open view: the whole flag word the shell reported rather than
    /// a guess at it, and the icon window shown back only when hiding it is what could have been done.
    /// Returns the reason it did not verify, or null when the desktop is as it was.
    /// </summary>
    private string? Restore(NativeDesktopVisualState wanted)
    {
        using var view = DesktopShellView.Open(_logger);
        return view is null ? "the shell's desktop view could not be opened" : Restore(view, wanted);
    }

    private static string? Restore(DesktopShellView view, NativeDesktopVisualState wanted)
    {
        if (view.CanReadFlags)
        {
            // An observed state is put back whole, so settings Muralis never touched — auto arrange,
            // snap to grid, the view mode — cannot be changed by a restore. An assumed one has no word
            // to put back, and writing the zero it carries would wipe exactly those settings; the only
            // thing that can then be undone is the one bit that hides the icons.
            var result = wanted.Observed
                ? view.SetFolderFlags(uint.MaxValue, wanted.OriginalFolderFlags)
                : view.SetFolderFlags(ShellViewInterfaces.FolderFlagNoIcons, 0);

            if (result != 0)
            {
                return $"the shell would not take the desktop's settings back ({result})";
            }
        }

        if (wanted.OriginalIconsVisible && view.IconList != nint.Zero
            && !NativeMethods.IsWindowVisible(view.IconList))
        {
            // A window hidden at the window level is not covered by the flag word, so it is shown back
            // regardless of whether that is how it was hidden.
            view.ShowIconList();
        }

        return view.IconsAreDrawn() == wanted.OriginalIconsVisible
            ? null
            : $"the desktop icons are not back the way they were (they should be {(wanted.OriginalIconsVisible ? "shown" : "hidden")})";
    }

    private NativeDesktopVisualState ReadState()
    {
        using var view = DesktopShellView.Open(_logger);
        return view is null ? NativeDesktopVisualState.AssumedVisible : view.VisualState();
    }

    /// <summary>
    /// Explorer restarted, so the desktop it draws has been rebuilt and everything done to the old one
    /// is gone, including a hidden icon window. The re-apply runs for as long as the new desktop takes
    /// to appear, then gives up quietly: the next restart tries again, and nothing about the user's
    /// desktop is at risk when it fails.
    /// </summary>
    private async void OnShellRestarted(object? sender, EventArgs e)
    {
        try
        {
            if (!_machine.OwnsTheDesktop)
            {
                return;
            }

            await Task.Delay(ReapplyFirstDelay).ConfigureAwait(false);

            for (var attempt = 1; attempt <= ReapplyAttempts; attempt++)
            {
                if (_disposed || !_machine.OwnsTheDesktop)
                {
                    return;
                }

                var redone = await _comThread.Value
                    .RunAsync(ReapplyAfterRestart, CancellationToken.None)
                    .ConfigureAwait(false);

                if (redone)
                {
                    _logger.LogInformation("The desktop takeover was re-applied after the shell restarted");
                    return;
                }

                await Task.Delay(ReapplyRetryDelay).ConfigureAwait(false);
            }

            _logger.LogWarning("The desktop takeover could not be re-applied after the shell restarted");
        }
        catch (Exception ex)
        {
            // Nothing awaits this handler, so a failure must not escape into the shell's event.
            _logger.LogWarning(ex, "Re-applying the desktop takeover after a shell restart did not finish");
        }
    }

    private bool ReapplyAfterRestart()
    {
        using var view = DesktopShellView.Open(_logger);
        if (view is null)
        {
            return false;
        }

        if (!view.IconsAreDrawn())
        {
            // Either the user's own desktop keeps the icons hidden or the new desktop came back with
            // them already hidden; either way there is nothing of Muralis's left to redo.
            return true;
        }

        return HideIcons(view) is not DesktopIconStrategy.None;
    }

    private DesktopTakeoverRecord Record(
        DesktopTakeoverState state,
        DesktopIconStrategy strategy,
        NativeDesktopVisualState? original) => new()
        {
            TakeoverWasActive = true,
            OriginalNativeState = original,
            SessionId = _sessionId,
            ProcessId = Environment.ProcessId,
            Timestamp = DateTimeOffset.Now,
            State = state,
            Strategy = strategy,
            Problem = _machine.Problem,
        };

    private DesktopTakeoverOutcome FailEnable(string problem)
    {
        _machine.FailEnable(problem, desktopGivenBack: true);
        _logger.LogError("The desktop takeover failed: {Problem}", problem);
        return Publish(new DesktopTakeoverOutcome(_machine.State, _strategy, _original, problem));
    }

    private DesktopTakeoverOutcome FailDisable(string problem)
    {
        _machine.FailDisable(problem);
        _logger.LogError("The native desktop could not be given back: {Problem}", problem);

        // The marker stays exactly where it is: a give-back that could not be verified is the case the
        // file exists for, and removing it would throw away the only record of what to restore.
        return Publish(new DesktopTakeoverOutcome(_machine.State, _strategy, _original, problem));
    }

    private DesktopTakeoverOutcome Publish(DesktopTakeoverOutcome outcome)
    {
        Changed?.Invoke(this, outcome);
        return outcome;
    }

    /// <summary>What one attempt at the takeover did: the rung it used, or the reason it failed.</summary>
    private sealed record TakeOverStep(
        DesktopIconStrategy Strategy,
        NativeDesktopVisualState? Original,
        string? Failure)
    {
        internal static TakeOverStep Failed(string reason) => new(DesktopIconStrategy.None, null, reason);

        internal static TakeOverStep Done(DesktopIconStrategy strategy, NativeDesktopVisualState original) =>
            new(strategy, original, null);
    }
}
