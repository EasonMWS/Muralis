using System.Text.Json.Serialization;

namespace Muralis.Core.Desktop.Takeover;

/// <summary>
/// What the user's own desktop looked like before Muralis touched it. Restoring means putting the
/// icons back the way they were found — never making them appear. A user who keeps their desktop
/// icons hidden has that recorded here, and a give-back leaves them hidden.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OriginalFolderFlags"/> is the whole flag word the shell view reported, kept so the
/// give-back can put the exact same word back rather than a guess at it. Every other bit in it —
/// snap-to-grid, auto-arrange, the view mode in the upper word — belongs to the user, and a give-back
/// that wrote its own idea of the word would quietly change settings it was never asked to touch.
/// </para>
/// <para>
/// <see cref="Observed"/> says whether the state was actually read from the shell or assumed. A
/// recovery that could not reach the shell view must not pretend it knows what to restore.
/// </para>
/// </remarks>
public sealed record NativeDesktopVisualState
{
    /// <summary>Whether the desktop icons were on screen when the state was recorded.</summary>
    public bool OriginalIconsVisible { get; init; } = true;

    /// <summary>The shell view's folder flags at that moment, as reported by <c>IFolderView2</c>.</summary>
    public uint OriginalFolderFlags { get; init; }

    /// <summary>The icon list window, when the shell had one at that moment.</summary>
    public bool HadIconWindow { get; init; }

    /// <summary>False when the state was assumed because the shell could not be asked.</summary>
    public bool Observed { get; init; }

    /// <summary>
    /// The state a desktop is assumed to be in when nothing could be recorded: icons shown, which is
    /// the shell's default and the only assumption that gives the user their desktop back.
    /// </summary>
    public static NativeDesktopVisualState AssumedVisible { get; } = new() { Observed = false };

    /// <summary>The one state that must not be mistaken for real knowledge.</summary>
    [JsonIgnore]
    public bool IsKnown => Observed;
}
