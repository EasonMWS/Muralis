namespace Muralis.Core.Models;

/// <summary>How trustworthy a derived <see cref="MonitorIdentity.StableId"/> is.</summary>
public enum IdentityConfidence
{
    /// <summary>The id was derived from a complete EDID block (manufacturer + product + serial).</summary>
    Exact,

    /// <summary>
    /// The EDID could not be read; the id falls back to a signature of the display name, pixel
    /// size and relative order. It can mis-match after displays are re-arranged, so consumers
    /// may treat it as less authoritative.
    /// </summary>
    SignatureFallback,
}
