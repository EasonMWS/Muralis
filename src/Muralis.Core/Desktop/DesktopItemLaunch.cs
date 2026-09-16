namespace Muralis.Core.Desktop;

/// <summary>
/// What opening an item hands to the shell: the item's location and the verb to open it with. It is
/// deliberately just a location and a verb — no command line is ever built from user data, and the
/// shell decides which program an association points at.
/// </summary>
public sealed record DesktopItemLaunchRequest(string File, string Verb);

/// <summary>
/// Turns an item into the request that opens it. Pure: it reads the target and nothing else, so the
/// routing for every kind can be tested without launching anything.
/// </summary>
public static class DesktopItemLaunch
{
    /// <summary>The verb that means "open it the way a double-click would".</summary>
    public const string OpenVerb = "open";

    /// <summary>
    /// The request for <paramref name="item"/>, or <c>null</c> when there is nothing to open. Every
    /// kind routes to its own location — a shortcut stays a shortcut and the shell resolves it, an
    /// address is not a file, and a folder is opened rather than searched.
    /// </summary>
    public static DesktopItemLaunchRequest? Plan(DesktopItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var location = item.Target?.Location;
        return string.IsNullOrWhiteSpace(location)
            ? null
            : new DesktopItemLaunchRequest(location, OpenVerb);
    }
}
