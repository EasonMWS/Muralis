using Muralis.Core.DockShell;

namespace Muralis.Core.Abstractions;

/// <summary>
/// What the shell says about a file the user has asked to pin: what kind of thing it is, what to call
/// it, and what it actually points at. <see cref="ResolvedTarget"/> is what a shortcut points at, and
/// it is the only reason a shortcut and its program can be told apart from an unrelated pair.
/// </summary>
public sealed record ApplicationDescription(
    PinnedAppKind Kind,
    string DisplayName,
    string LaunchTarget,
    string? ResolvedTarget)
{
    /// <summary>What makes this the same application as another pin.</summary>
    public string Identity => PinnedAppIdentity.Of(LaunchTarget, ResolvedTarget);
}

/// <summary>
/// Reads a program or shortcut the way Explorer would describe it: the shortcut's own resolution
/// rules, the program's own version information, and the file name as the last resort. Kept behind
/// an interface because only the desktop layer has the shell, and because the dock's rules have to be
/// testable on a machine with no shortcuts on it.
/// </summary>
public interface IApplicationInspector
{
    /// <summary>
    /// Describes the file at <paramref name="path"/>, or returns null when it is not an application
    /// the dock pins or cannot be read at all.
    /// </summary>
    Task<ApplicationDescription?> DescribeAsync(string path, CancellationToken cancellationToken = default);
}
