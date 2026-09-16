namespace Muralis.Core.Desktop.Takeover;

/// <summary>
/// What the desktop is doing right now. A single flag could not say the difference between "Muralis
/// is showing its desktop", "Muralis is in the middle of taking it over" and "something went wrong
/// and the native icons must be given back", so the three are separate states with their own legal
/// moves.
/// </summary>
public enum DesktopTakeoverState
{
    /// <summary>Explorer's own desktop, untouched. Muralis may still be showing a canvas over it.</summary>
    Native,

    /// <summary>A takeover transaction is running: the canvas is being prepared and the icons hidden.</summary>
    Enabling,

    /// <summary>Muralis owns the interactive desktop: the native icons are hidden and the canvas is the way in.</summary>
    Muralis,

    /// <summary>A give-back transaction is running: the icons are being put back and verified.</summary>
    Disabling,

    /// <summary>
    /// The desktop could not be handed back cleanly — a restore failed, or the app is starting up after
    /// a crash that left the icons hidden. The only way out is a give-back; taking over again is refused
    /// until the native desktop is back.
    /// </summary>
    RecoveryRequired,
}

/// <summary>
/// What the user asked for, as it is saved in the layout document. This is the choice; the
/// <see cref="DesktopTakeoverState"/> is what is happening.
/// </summary>
public enum DesktopMode
{
    /// <summary>Leave the native desktop alone. Muralis shows its own window and tray only.</summary>
    Native,

    /// <summary>Draw the items over the icons without hiding anything: a look at what a takeover would show.</summary>
    Preview,

    /// <summary>Hide Explorer's icons and make the Muralis canvas the desktop.</summary>
    Takeover,
}

/// <summary>
/// How the native icons were made to disappear, named so a log line and the recovery file can say
/// which one was used. The rungs are tried in the order they are declared.
/// </summary>
public enum DesktopIconStrategy
{
    /// <summary>Nothing was done to the native desktop.</summary>
    None,

    /// <summary>
    /// <c>IFolderView2::SetCurrentFolderFlags(FWF_NOICONS)</c> on the desktop's own shell view. The
    /// documented path, and the one a measured Windows 11 build accepts.
    /// </summary>
    ShellViewFlags,

    /// <summary>
    /// Hiding the desktop's icon list window. The last resort: it is a public, reversible window call
    /// and leaves no trace anywhere, but Explorer rebuilds that window from scratch, so it has to be
    /// re-applied after every shell restart and is not remembered across a reboot.
    /// </summary>
    IconWindow,
}
