namespace Muralis.Desktop.Surfaces;

/// <summary>Identifies one registered surface. Opaque to callers; formatted for diagnostics only.</summary>
public readonly record struct DesktopSurfaceId(Guid Value)
{
    public static DesktopSurfaceId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
