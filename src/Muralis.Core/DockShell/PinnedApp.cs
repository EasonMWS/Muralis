namespace Muralis.Core.DockShell;

/// <summary>What a pinned app's target is. Both kinds are started by the shell; only the file the
/// shell is handed, and what it does with it, differ.</summary>
public enum PinnedAppKind
{
    /// <summary>A program, started with the arguments and working directory it was pinned with.</summary>
    Application,

    /// <summary>
    /// A Windows shortcut. The shell opens it, so the shortcut's own target, arguments and start-in
    /// folder stay in charge and a shortcut edited in Explorer keeps working here.
    /// </summary>
    Shortcut,
}

/// <summary>
/// One application the user has pinned to the dock: what to start, what to call it and which picture
/// to show. Only the launch target is a fact about the machine; everything else is a preference the
/// user can change without touching the file itself.
/// </summary>
/// <remarks>
/// <see cref="Identity"/> is what stops the same application being pinned twice, and it is stored
/// rather than recomputed so a target that moves on disk cannot silently become a second pin — see
/// <see cref="PinnedAppIdentity"/>.
/// </remarks>
public sealed record PinnedApp(
    string Id,
    string DisplayName,
    string LaunchTarget,
    string IconIdentity,
    PinnedAppKind Kind,
    string Identity,
    string? Arguments = null,
    string? WorkingDirectory = null);

/// <summary>
/// Which applications count as the same one. A shortcut is the same application as the program it
/// points at, so the resolved target is the identity where there is one; where a shortcut cannot be
/// resolved the shortcut's own path is all there is to go on. Paths are compared case-insensitively
/// and without a trailing separator, because Windows does.
/// </summary>
public static class PinnedAppIdentity
{
    /// <summary>The identity for a launch target, preferring what a shortcut resolves to.</summary>
    public static string Of(string launchTarget, string? resolvedTarget) =>
        Normalize(string.IsNullOrWhiteSpace(resolvedTarget) ? launchTarget : resolvedTarget);

    /// <summary>
    /// A comparison key for a path: absolute where the path allows it, upper case, no trailing
    /// separator. A path the file system will not even accept is still given a stable key, because a
    /// pin the user made must not become unloadable when the file behind it is deleted.
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or System.Security.SecurityException)
        {
            // Not a path this machine can resolve; the text as given is the most stable key available.
        }

        return trimmed.ToUpperInvariant();
    }
}

/// <summary>The launch targets the dock knows how to pin.</summary>
public static class PinnedAppTargets
{
    public const string ApplicationExtension = ".exe";

    public const string ShortcutExtension = ".lnk";

    /// <summary>Which kind of pin a path makes, or null when the dock does not pin that.</summary>
    public static PinnedAppKind? KindOf(string path) => Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
    {
        ApplicationExtension => PinnedAppKind.Application,
        ShortcutExtension => PinnedAppKind.Shortcut,
        _ => null,
    };

    public static bool IsSupported(string path) => KindOf(path) is not null;
}
