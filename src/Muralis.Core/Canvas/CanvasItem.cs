namespace Muralis.Core.Canvas;

/// <summary>Where an item lives on the canvas.</summary>
public enum CanvasItemPlacement
{
    /// <summary>Sits at its own anchor + offset and can be dragged anywhere on the desktop.</summary>
    Free,

    /// <summary>Part of the edge dock rail; its position and order come from the dock, not the item.</summary>
    Dock,
}

/// <summary>
/// One item on the desktop canvas. Data only: the saved position is <see cref="Anchor"/> plus the
/// DIP offsets, and pixels are recomputed on every layout pass. <see cref="Z"/> orders the free
/// items (higher draws on top); dock items keep their order in <see cref="CanvasLayout.Items"/>.
/// </summary>
public sealed class CanvasItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Logical icon name; the renderer maps it to a real app icon when one is found.</summary>
    public string IconKey { get; set; } = string.Empty;

    public CanvasItemPlacement Placement { get; set; } = CanvasItemPlacement.Free;

    public CanvasAnchor Anchor { get; set; } = CanvasAnchor.Center;

    public double OffsetXDip { get; set; }

    public double OffsetYDip { get; set; }

    /// <summary>Edge length in DIP; items are square in this prototype.</summary>
    public double SizeDip { get; set; } = 96;

    public int Z { get; set; }

    public bool IsVisible { get; set; } = true;

    public CanvasItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        IconKey = IconKey,
        Placement = Placement,
        Anchor = Anchor,
        OffsetXDip = OffsetXDip,
        OffsetYDip = OffsetYDip,
        SizeDip = SizeDip,
        Z = Z,
        IsVisible = IsVisible,
    };
}
