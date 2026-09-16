namespace Muralis.Core.Models;

/// <summary>Where the desktop canvas prototype is in its lifecycle.</summary>
public enum CanvasPrototypeState
{
    /// <summary>Off. Nothing of the canvas exists and the desktop is untouched.</summary>
    Disabled,

    /// <summary>Being put on the desktop.</summary>
    Starting,

    /// <summary>Showing above the desktop icons.</summary>
    Active,

    /// <summary>Could not be shown; the desktop is untouched.</summary>
    Failed,
}

/// <summary>The canvas prototype's state as the UI sees it.</summary>
public sealed record CanvasPrototypeStatus(CanvasPrototypeState State, int ItemCount = 0, string? Error = null)
{
    public static CanvasPrototypeStatus Disabled { get; } = new(CanvasPrototypeState.Disabled);
}
