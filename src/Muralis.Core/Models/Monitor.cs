namespace Muralis.Core.Models;

/// <summary>
/// The persistent identity of a display: the only facts allowed to answer "which display is
/// this" across sessions, restarts and re-arrangements. Runtime facts such as
/// <c>IsPrimary</c>, position or DPI never belong here — they live in
/// <see cref="MonitorRuntimeInfo"/>. Profiles bind to <see cref="StableId"/> only.
/// </summary>
public sealed record MonitorIdentity(
    string StableId,
    EdidInfo? Edid,
    IdentityConfidence Confidence,
    string? LastKnownFriendlyName)
{
    /// <summary>
    /// Two identities describe the same display when their <see cref="StableId"/> matches. The
    /// EDID block, the confidence and the friendly-name hint are descriptive metadata that can
    /// drift (a failing EDID read, a renamed display) without meaning "a different display".
    /// </summary>
    public bool Equals(MonitorIdentity? other) =>
        other is not null && string.Equals(StableId, other.StableId, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(StableId);
}

/// <summary>
/// Facts about a display that may legitimately change between sessions and while the app runs:
/// where it is, how big it is, whether it is primary. None of these may take part in identity
/// matching.
/// </summary>
public sealed record MonitorRuntimeInfo(
    string DevicePath,
    string DeviceName,
    string FriendlyName,
    bool IsPrimary,
    int OrderIndex,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint Dpi,
    double ScaleFactor,
    MonitorOrientation Orientation,
    MirroringInfo Mirroring);

/// <summary>One display as the running app sees it: stable identity plus current runtime facts.</summary>
public sealed record Monitor(MonitorIdentity Identity, MonitorRuntimeInfo Runtime);
