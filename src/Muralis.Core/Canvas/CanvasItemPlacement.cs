namespace Muralis.Core.Canvas;

/// <summary>Where an item lives on the canvas.</summary>
public enum CanvasItemPlacement
{
    /// <summary>Sits at its own anchor + offset and can be dragged anywhere on the desktop.</summary>
    Free,

    /// <summary>Part of the edge dock rail; its position and order come from the dock, not the item.</summary>
    Dock,
}
