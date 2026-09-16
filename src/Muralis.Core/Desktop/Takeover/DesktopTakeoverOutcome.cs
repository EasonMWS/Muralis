namespace Muralis.Core.Desktop.Takeover;

/// <summary>
/// What a takeover or give-back ended as. The state is always filled in — including after a failure,
/// where it says whether the desktop was handed back or is owed a recovery — and the strategy names
/// which way the icons were moved, for the log line and the diagnostics panel.
/// </summary>
public sealed record DesktopTakeoverOutcome(
    DesktopTakeoverState State,
    DesktopIconStrategy Strategy,
    NativeDesktopVisualState? Original,
    string? Error)
{
    /// <summary>Muralis owns the interactive desktop.</summary>
    public bool OwnsTheDesktop => State == DesktopTakeoverState.Muralis;

    /// <summary>The desktop could not be handed back and the user has to be told.</summary>
    public bool NeedsRecovery => State == DesktopTakeoverState.RecoveryRequired;

    /// <summary>A takeover that did not happen, for a caller that only asked.</summary>
    public static DesktopTakeoverOutcome AlreadyNative(string? error = null) =>
        new(DesktopTakeoverState.Native, DesktopIconStrategy.None, null, error);
}
