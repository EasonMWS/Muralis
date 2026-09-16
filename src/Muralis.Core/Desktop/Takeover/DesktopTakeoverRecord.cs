using System.Text.Json.Serialization;

namespace Muralis.Core.Desktop.Takeover;

/// <summary>
/// The mark Muralis leaves on disk while the native desktop is taken over. It exists for one reason:
/// a process that dies — or is killed, or loses power — cannot run its own cleanup, and the icons it
/// hid outlive it in the shell's own view state. The next launch finds this file, learns what the
/// user's desktop looked like before, and gives it back before anything else happens.
/// </summary>
/// <remarks>
/// The file is written <em>before</em> the icons are hidden and removed only after they are verified
/// to be back, so its existence is a conservative claim: it may be there when nothing was hidden, but
/// it is never missing when something was. Everything in it is for a human reading the file as much
/// as for the code: which run wrote it, which process, when, and what the desktop looked like.
/// </remarks>
public sealed class DesktopTakeoverRecord
{
    public const string DocumentKind = "muralis.desktopTakeover";

    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Kind { get; set; } = DocumentKind;

    /// <summary>Whether the run that wrote this file had hidden the native icons.</summary>
    public bool TakeoverWasActive { get; set; }

    /// <summary>What the desktop looked like before that run touched it.</summary>
    public NativeDesktopVisualState? OriginalNativeState { get; set; }

    /// <summary>Identifies the run that wrote the file, so a marker from this run can be told from an older one.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The process that wrote the file. Only ever read by a human or a log line.</summary>
    public int ProcessId { get; set; }

    /// <summary>When the file was last written.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The state the writer was in, as text: the marker is read when the enum may have moved on.</summary>
    public DesktopTakeoverState State { get; set; }

    /// <summary>Which rung had hidden the icons, so the same one can be used to put them back.</summary>
    public DesktopIconStrategy Strategy { get; set; }

    /// <summary>Why the marker is still there, when the writer knew it was leaving a mess behind.</summary>
    public string? Problem { get; set; }

    /// <summary>The state to restore, or null when the file does not say one.</summary>
    [JsonIgnore]
    public NativeDesktopVisualState? Original => OriginalNativeState;

    /// <summary>Checks the facts a recovery depends on.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (SchemaVersion <= 0 || SchemaVersion > CurrentSchemaVersion)
        {
            problems.Add($"The takeover marker schema version {SchemaVersion} is not one this build writes.");
        }

        if (!string.Equals(Kind, DocumentKind, StringComparison.Ordinal))
        {
            problems.Add($"The takeover marker kind must be '{DocumentKind}'.");
        }

        if (TakeoverWasActive && OriginalNativeState is null)
        {
            // Without this the recovery cannot know whether to leave the icons hidden or show them,
            // and guessing "shown" would override a user who keeps their own icons hidden.
            problems.Add("A marker that claims a takeover has to say what the desktop looked like before it.");
        }

        return problems;
    }
}
