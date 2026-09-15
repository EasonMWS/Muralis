namespace Muralis.Core.Models;

/// <summary>What one display's backdrop is doing right now.</summary>
/// <param name="Kind">Content kind currently applied.</param>
/// <param name="IsActive">Whether the content is actually applied and showing.</param>
/// <param name="AssetRef">Reference being shown, when there is one.</param>
/// <param name="Error">Human-readable reason when applying failed.</param>
public sealed record BackdropStatus(BackdropKind Kind, bool IsActive, string? AssetRef = null, string? Error = null)
{
    /// <summary>Nothing is applied; the static background is visible.</summary>
    public static BackdropStatus Idle { get; } = new(BackdropKind.None, false);
}

/// <summary>Raised when a display's backdrop changed, so subscribers know which display it was.</summary>
public sealed record BackdropStatusChanged(MonitorRef Monitor, BackdropStatus Status);
