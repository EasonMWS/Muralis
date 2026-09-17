using Muralis.Core.Desktop;
using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Product-level desktop mode coordinator. The public model is deliberately limited to Native and
/// Muralis; the frozen Phase 3 service is retained only for startup recovery and internal diagnostics.
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
    bool IsMuralisAvailable,
    DesktopModeStatus Phase3,
    string? Error = null)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool IsMuralis => Mode == DesktopExperienceMode.Muralis;
}
