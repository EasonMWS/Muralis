namespace Muralis.Core.Models;

/// <summary>
/// The desktop canvas prototype switch. Whether the prototype is on lives here; where its items
/// sit lives in its own file (<see cref="Helpers.AppPaths.CanvasPrototypeFile"/>), so resetting the
/// layout never touches general settings.
/// </summary>
public sealed class DesktopCanvasSettings
{
    /// <summary>Whether the canvas is put back on the desktop the next time the app starts.</summary>
    public bool Enabled { get; set; }
}
