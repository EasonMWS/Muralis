using Muralis.Core.Desktop;
using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Product-level desktop mode coordinator. It delegates the experimental takeover to the frozen Phase 3
/// service and the Clean Desktop lifecycle to its own presentation boundary, so the modes never grow into
/// three copies of the same shell logic.
/// </summary>
public interface IDesktopExperienceService
{
    DesktopExperienceStatus Status { get; }

    event EventHandler<DesktopExperienceStatus>? Changed;

    Task<DesktopExperienceStatus> RestoreAsync(CancellationToken cancellationToken = default);

    Task<DesktopExperienceStatus> ApplyAsync(
        DesktopExperienceMode mode,
        CancellationToken cancellationToken = default);
}

public sealed record DesktopExperienceStatus(
    DesktopExperienceMode Mode,
    bool IsCleanDesktopAvailable,
    DesktopModeStatus Phase3,
    string? Error = null)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool IsExperimentalTakeover => Mode == DesktopExperienceMode.FullTakeoverExperimental;
}
