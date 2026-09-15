namespace Muralis.Core.Models;

/// <summary>
/// Whether a display is mirrored and, if so, which device leads the mirror group. A mirrored
/// display shows another display's content, so backdrops are only rendered once per group.
/// </summary>
public sealed record MirroringInfo(bool IsMirrored, string? MirrorGroup, string? LeaderDevicePath)
{
    /// <summary>Not mirrored — the common case.</summary>
    public static MirroringInfo None { get; } = new(false, null, null);
}
