namespace Muralis.Core.DockShell;

/// <summary>
/// Persistence boundary for a future real Dock. Phase 4A mock items are never written through it.
/// Keeping this outside DesktopLayout is what prevents the new Dock from depending on the takeover canvas.
/// </summary>
public interface IDockShellStateStore
{
    Task<DockShellState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DockShellState state, CancellationToken cancellationToken = default);
}

public sealed record DockShellState
{
    public IReadOnlyList<string> PinnedItemIds { get; init; } = [];

    public double ShelfPreferredWidth { get; init; } = 420;
}
