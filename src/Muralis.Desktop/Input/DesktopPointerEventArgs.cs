namespace Muralis.Desktop.Input;

/// <summary>The pointer state at the moment an event was raised.</summary>
public sealed class DesktopPointerEventArgs(DesktopPointerState state) : EventArgs
{
    public DesktopPointerState State { get; } = state;
}
