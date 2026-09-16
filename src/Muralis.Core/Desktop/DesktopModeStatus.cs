using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Models;

namespace Muralis.Core.Desktop;

/// <summary>
/// Where the desktop stands right now: the mode the user asked for, what the takeover is really
/// doing, and whether the canvas is really on the desktop. Three answers rather than one, because
/// the mode is a wish and the other two are the facts — a takeover can be owed a give-back while the
/// user's chosen mode is still takeover, and saying so is the whole point of reporting all three.
/// </summary>
public sealed record DesktopModeStatus(
    DesktopMode Mode,
    DesktopTakeoverState Takeover,
    CanvasPrototypeState Canvas,
    string? Error)
{
    /// <summary>The native desktop as it was left: Muralis shows a window and a tray, nothing else.</summary>
    public static DesktopModeStatus Native { get; } = new(
        DesktopMode.Native,
        DesktopTakeoverState.Native,
        CanvasPrototypeState.Disabled,
        null);

    /// <summary>Muralis owns the interactive desktop: the native icons are not drawn.</summary>
    public bool OwnsTheDesktop => Takeover == DesktopTakeoverState.Muralis;

    /// <summary>The canvas is on the desktop, whether or not the native icons were hidden.</summary>
    public bool IsShowingCanvas => Canvas == CanvasPrototypeState.Active;

    /// <summary>The desktop has to be given back before anything else may be asked of it.</summary>
    public bool NeedsRecovery => Takeover == DesktopTakeoverState.RecoveryRequired;

    /// <summary>What the mode really is right now, which is not always what was asked for.</summary>
    public DesktopMode EffectiveMode => (Mode, IsShowingCanvas) switch
    {
        (DesktopMode.Takeover, _) when OwnsTheDesktop => DesktopMode.Takeover,
        (DesktopMode.Native, false) => DesktopMode.Native,
        (_, true) => DesktopMode.Preview,
        _ => DesktopMode.Native,
    };

    /// <summary>Whether a failure is being reported.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);
}
