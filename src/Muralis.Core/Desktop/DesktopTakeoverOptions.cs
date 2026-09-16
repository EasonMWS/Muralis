using Muralis.Core.Desktop.Takeover;

namespace Muralis.Core.Desktop;

/// <summary>
/// The desktop takeover as the documents keep it: which of the three desktop modes the user chose,
/// whether the user's own desktop content is adopted on enable, and which sources the user has turned
/// down so a later sync does not bring them back.
/// </summary>
/// <remarks>
/// This is the choice, not the situation. What is happening right now — including whether a crash left
/// the desktop owed a recovery — is runtime state and lives in the takeover marker instead, so a stale
/// choice can never make the app believe the native desktop is still hidden.
/// </remarks>
public sealed class DesktopTakeoverOptions
{
    /// <summary>What the user asked for.</summary>
    public DesktopMode Mode { get; set; } = DesktopMode.Native;

    /// <summary>
    /// Whether enabling the takeover adopts the items already on the user's desktop. Nothing is ever
    /// moved or rewritten by it: each entry becomes an item that points at where the file already is.
    /// </summary>
    public bool AdoptDesktopItems { get; set; } = true;

    /// <summary>
    /// Desktop entries the user has taken off the canvas. A sync adopts what it finds, so without this
    /// list an item the user deliberately removed would be back on the next scan.
    /// </summary>
    public List<string> IgnoredSourcePaths { get; set; } = [];

    /// <summary>Whether a desktop entry is one the user has turned down.</summary>
    public bool IsIgnored(string sourcePath) =>
        !string.IsNullOrEmpty(sourcePath)
        && IgnoredSourcePaths.Any(ignored => PathsMatch(ignored, sourcePath));

    /// <summary>Remembers that a source is not wanted, once.</summary>
    public void Ignore(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || IsIgnored(sourcePath))
        {
            return;
        }

        IgnoredSourcePaths.Add(sourcePath);
    }

    /// <summary>Whether two paths name the same place, in the way the file system on Windows does.</summary>
    internal static bool PathsMatch(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks the facts consumers rely on, one message per problem found.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!Enum.IsDefined(Mode))
        {
            problems.Add("The desktop mode is not one of native, preview or takeover.");
        }

        if (IgnoredSourcePaths is null)
        {
            problems.Add("The list of turned-down desktop sources is missing.");
            return problems;
        }

        foreach (var path in IgnoredSourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                problems.Add($"The turned-down desktop source '{path}' is not a fully qualified path.");
            }
        }

        return problems;
    }

    /// <summary>Deep copy, safe to hand to another thread while the app keeps editing this one.</summary>
    public DesktopTakeoverOptions Clone() => new()
    {
        Mode = Mode,
        AdoptDesktopItems = AdoptDesktopItems,
        IgnoredSourcePaths = [.. IgnoredSourcePaths],
    };

    /// <summary>Copies another set of options in place, so one instance can serve a whole session.</summary>
    public void CopyFrom(DesktopTakeoverOptions other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Mode = other.Mode;
        AdoptDesktopItems = other.AdoptDesktopItems;
        IgnoredSourcePaths = [.. other.IgnoredSourcePaths];
    }
}
