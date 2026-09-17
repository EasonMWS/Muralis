namespace Muralis.Core.Models;

/// <summary>The supported product-level relationships between Muralis and Explorer.</summary>
public enum DesktopExperienceMode
{
    /// <summary>Explorer stays fully responsible for the desktop.</summary>
    Native,

    /// <summary>
    /// Explorer remains alive, its icons are visually hidden, and a Muralis Shelf is the alternate
    /// presentation. Explorer remains the owner and source of every desktop item.
    /// </summary>
    CleanDesktop,

    /// <summary>The preserved Phase 3 canvas takeover. It is supported, but explicitly experimental.</summary>
    FullTakeoverExperimental,
}

public sealed class DesktopExperienceSettings
{
    /// <summary>Safe by default: a fresh or downgraded installation leaves Explorer untouched.</summary>
    public DesktopExperienceMode Mode { get; set; } = DesktopExperienceMode.Native;
}
