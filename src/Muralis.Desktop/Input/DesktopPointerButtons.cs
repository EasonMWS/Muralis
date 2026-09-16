namespace Muralis.Desktop.Input;

/// <summary>The mouse buttons that are down at the moment a pointer event was read.</summary>
[Flags]
public enum DesktopPointerButtons
{
    None = 0,
    Left = 1,
    Right = 2,
}
