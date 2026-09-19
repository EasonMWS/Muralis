namespace Muralis.Core.Motion;

/// <summary>
/// What one icon is doing this frame: how large it is drawn and where it has been pushed to, measured
/// from where it rests.
/// </summary>
/// <remarks>
/// A mutable class rather than a record so the engine can reuse one array of these for the life of the
/// dock. The pointer path is not allowed to allocate.
/// </remarks>
public sealed class DockMotionSample
{
    /// <summary>The icon this belongs to, in dock order.</summary>
    public int Index { get; internal set; }

    /// <summary>How large the icon is drawn, as a multiple of its resting size. Never below 1.</summary>
    public double Scale { get; internal set; } = 1;

    /// <summary>How far the icon has been pushed along the dock, in DIP. Positive is away from the pointer.</summary>
    public double TranslateX { get; internal set; }

    /// <summary>How far the icon has risen towards the pointer, in DIP. Positive is up.</summary>
    public double Lift { get; internal set; }

    /// <summary>
    /// The influence the pointer has on this icon, 1 directly under it and 0 beyond the radius. Kept for
    /// the Lab and for the tests; the renderer never reads it.
    /// </summary>
    public double Influence { get; internal set; }

    /// <summary>Whether this icon is being moved at all, within a hair of floating point noise.</summary>
    public bool IsAtRest => Scale <= 1.0000001 && Math.Abs(TranslateX) < 0.0001 && Math.Abs(Lift) < 0.0001;

    public override string ToString() =>
        $"#{Index} scale={Scale:0.###} x={TranslateX:0.##} lift={Lift:0.##}";
}
