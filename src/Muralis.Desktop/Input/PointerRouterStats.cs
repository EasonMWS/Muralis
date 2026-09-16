namespace Muralis.Desktop.Input;

/// <summary>What the router has seen so far, for the development overlay.</summary>
internal readonly record struct PointerRouterStats(
    long Reports,
    long Dispatches,
    double DispatchesPerSecond,
    DesktopPointerContext Context);
