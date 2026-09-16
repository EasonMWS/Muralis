using Muralis.Core.Desktop.Takeover;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Takes the native desktop over and gives it back: while Muralis owns it, Explorer's own icons are
/// not drawn and the canvas is the way in. Implemented per platform in the app layer
/// (<c>Muralis.Desktop</c>), because it is the shell's own view that has to be asked — and it is only
/// ever asked, never replaced, injected or restarted.
/// </summary>
/// <remarks>
/// <para>
/// What the takeover does is <em>stop drawing</em> the icons, with the shell's own documented view
/// call. Nothing about the user's files, folders or shortcuts is read for it, and nothing on the
/// native desktop is modified: switching the takeover off returns the desktop as it was, and every
/// native icon keeps working while it is on.
/// </para>
/// <para>
/// The state is a machine rather than a flag, because "Muralis is showing its desktop" and "Muralis
/// failed to give the desktop back" have to be told apart: see <see cref="DesktopTakeoverState"/>.
/// The icon visibility the user had <em>before</em> the takeover is recorded, and giving the desktop
/// back restores that — never "shows the icons", which would override a user who keeps them hidden.
/// </para>
/// <para>
/// Every operation is atomic from the caller's side: it either ends with the desktop taken over and
/// verified, or with the desktop exactly as it was, or with a state that says the desktop is owed a
/// give-back. A failure is reported in the returned <see cref="DesktopTakeoverOutcome"/> rather than
/// thrown, because a failed give-back is a thing the user has to be told about, not a crash.
/// </para>
/// </remarks>
public interface IDesktopTakeoverService
{
    /// <summary>What the desktop is doing right now.</summary>
    DesktopTakeoverState State { get; }

    /// <summary>The last thing that went wrong, or null when nothing has.</summary>
    string? Problem { get; }

    /// <summary>Raised on every change. May be raised on a background thread.</summary>
    event EventHandler<DesktopTakeoverOutcome>? Changed;

    /// <summary>
    /// Hides the native icons and puts Muralis in charge of the desktop. Already taken over is a
    /// success, not an error; a desktop that is owed a give-back is refused, and the refusal says so.
    /// </summary>
    Task<DesktopTakeoverOutcome> TakeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives the desktop back the way it was found. Always safe to ask for — a desktop that was never
    /// taken over is simply left alone — and a give-back that cannot be verified leaves the state at
    /// <see cref="DesktopTakeoverState.RecoveryRequired"/> rather than claiming success.
    /// </summary>
    Task<DesktopTakeoverOutcome> ReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The emergency give-back: it does not care what state the app is in, needs no canvas and no
    /// layout, and restores what the crash marker recorded. With nothing recorded it assumes the icons
    /// were shown, because that is the assumption that gives the user their desktop back.
    /// </summary>
    Task<DesktopTakeoverOutcome> RestoreNativeDesktopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The one call a fresh process makes: if the marker says a previous run left the native icons
    /// hidden, they are given back now. A marker that cannot be read is reported rather than acted on,
    /// so a corrupt file never changes the user's desktop behind their back.
    /// </summary>
    Task<DesktopTakeoverOutcome> RecoverIfNeededAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What the user's desktop looks like right now, read without changing anything. Used to record
    /// what a takeover would be undoing, and to report it.
    /// </summary>
    Task<NativeDesktopVisualState> ReadNativeStateAsync(CancellationToken cancellationToken = default);
}
