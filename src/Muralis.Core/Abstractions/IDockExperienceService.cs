namespace Muralis.Core.Abstractions;

/// <summary>
/// The dock's presence on the desktop. One window, and the two independent reasons for it to be up:
/// the user's own preference, and Clean Desktop, which has nothing left to show if the dock is not
/// there. The window is only ever shown or hidden from here, so the two can never fight over it.
/// </summary>
/// <remarks>
/// Every call answers whether the dock ended up in the state that was asked for and never throws: a
/// desktop that could not be covered is a fact to report, not a reason to fail a mode change that is
/// already half done.
/// </remarks>
public interface IDockExperienceService
{
    /// <summary>True when this build can put a dock on the desktop at all.</summary>
    bool IsAvailable { get; }

    /// <summary>True while the dock window is on the desktop.</summary>
    bool IsVisible { get; }

    /// <summary>Raised whenever the dock appears or disappears, with the state it is now in.</summary>
    event EventHandler<bool>? VisibilityChanged;

    /// <summary>Puts the dock back the way the user left it. Called once per launch, as settings are read.</summary>
    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows or hides the dock because the user asked, and remembers the choice.</summary>
    Task<bool> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requires the dock to be up without changing the user's preference: what Clean Desktop calls
    /// before it hides Explorer's icons, and what must have succeeded before it does.
    /// </summary>
    Task<bool> EnsureVisibleAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the requirement above and returns the dock to the user's preference.</summary>
    Task<bool> ReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the dock for this run, because the application is quitting. Called on the way out so the
    /// window really goes: hidden is still open, and an open window keeps the process alive.
    /// </summary>
    Task<bool> ShutdownAsync(CancellationToken cancellationToken = default);
}
