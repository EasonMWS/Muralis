namespace Muralis.Core.Desktop.Takeover;

/// <summary>
/// The desktop's takeover state and the moves allowed out of it. Pure — no shell, no clock, no
/// files — so every way the takeover can go is provable without a desktop.
/// </summary>
/// <remarks>
/// <para>
/// The shape of the rules is that a transaction always ends somewhere definite: a takeover that fails
/// before the icons were hidden rolls straight back to <see cref="DesktopTakeoverState.Native"/>, and
/// one that fails after them lands in <see cref="DesktopTakeoverState.RecoveryRequired"/> rather than
/// pretending nothing happened. Nothing may take the desktop over again while a give-back is owed, so
/// a half-restored desktop can never be built on.
/// </para>
/// <para>
/// The last problem is kept as text: it is what the user is told and what the recovery file records,
/// and it is cleared only when the desktop is genuinely back.
/// </para>
/// </remarks>
public sealed class DesktopTakeoverMachine
{
    /// <summary>What the desktop is doing right now.</summary>
    public DesktopTakeoverState State { get; private set; } = DesktopTakeoverState.Native;

    /// <summary>The last thing that went wrong, or null when nothing has.</summary>
    public string? Problem { get; private set; }

    /// <summary>A transaction is running; nothing else may start.</summary>
    public bool IsBusy => State is DesktopTakeoverState.Enabling or DesktopTakeoverState.Disabling;

    /// <summary>Muralis owns the interactive desktop right now.</summary>
    public bool OwnsTheDesktop => State == DesktopTakeoverState.Muralis;

    /// <summary>The native desktop has to be given back before anything else may happen.</summary>
    public bool NeedsRecovery => State == DesktopTakeoverState.RecoveryRequired;

    /// <summary>Whether the native icons may be hidden: only from a fresh start of a takeover.</summary>
    public bool MayHideIcons => State == DesktopTakeoverState.Enabling;

    /// <summary>Starts a takeover. Refused while one is running, while the desktop is already owned, and while a give-back is owed.</summary>
    public bool BeginEnable()
    {
        if (State != DesktopTakeoverState.Native)
        {
            return false;
        }

        State = DesktopTakeoverState.Enabling;
        Problem = null;
        return true;
    }

    /// <summary>The icons are hidden and verified: the desktop is Muralis's.</summary>
    public bool MarkEnabled()
    {
        if (State != DesktopTakeoverState.Enabling)
        {
            return false;
        }

        State = DesktopTakeoverState.Muralis;
        Problem = null;
        return true;
    }

    /// <summary>
    /// A takeover failed. <paramref name="desktopGivenBack"/> says whether the icons were already
    /// hidden and have been put back: without that, the desktop is not the user's any more and the
    /// state says so.
    /// </summary>
    public bool FailEnable(string problem, bool desktopGivenBack)
    {
        if (State != DesktopTakeoverState.Enabling)
        {
            return false;
        }

        State = desktopGivenBack ? DesktopTakeoverState.Native : DesktopTakeoverState.RecoveryRequired;
        Problem = problem;
        return true;
    }

    /// <summary>
    /// Starts giving the desktop back. Allowed from the owned state, from a desktop that was never
    /// taken over (so "restore" is always safe to ask for) and from a state that owes a recovery.
    /// </summary>
    public bool BeginDisable()
    {
        if (State is not (DesktopTakeoverState.Muralis
            or DesktopTakeoverState.Native
            or DesktopTakeoverState.RecoveryRequired))
        {
            return false;
        }

        State = DesktopTakeoverState.Disabling;
        return true;
    }

    /// <summary>The icons are back and verified: the desktop is the user's again.</summary>
    public bool MarkDisabled()
    {
        if (State != DesktopTakeoverState.Disabling)
        {
            return false;
        }

        State = DesktopTakeoverState.Native;
        Problem = null;
        return true;
    }

    /// <summary>A give-back failed: the icons may still be hidden, so the desktop is owed a recovery.</summary>
    public bool FailDisable(string problem)
    {
        if (State != DesktopTakeoverState.Disabling)
        {
            return false;
        }

        State = DesktopTakeoverState.RecoveryRequired;
        Problem = problem;
        return true;
    }

    /// <summary>
    /// Records that the desktop has to be given back, from wherever the machine happens to be. Used
    /// when a crash marker is found at startup and when a shell restart leaves the state unclear.
    /// </summary>
    public void RequireRecovery(string problem)
    {
        State = DesktopTakeoverState.RecoveryRequired;
        Problem = problem;
    }

    /// <summary>Puts the machine back to the native desktop without claiming anything was restored.</summary>
    public void ResetToNative()
    {
        State = DesktopTakeoverState.Native;
        Problem = null;
    }
}
