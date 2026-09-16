namespace Muralis.Desktop.Input;

/// <summary>
/// The window facts the context walk needs. A seam: the walk is pure logic over a parent chain, so
/// tests hand it a made-up chain instead of real windows.
/// </summary>
internal interface IPointerWindowProbe
{
    /// <summary>The window's true parent (never its owner, unlike <c>GetParent</c>), or zero.</summary>
    nint ParentOf(nint window);

    /// <summary>The window's class name, or null when it cannot be read.</summary>
    string? ClassNameOf(nint window);

    /// <summary>The desktop window: the top of every parent chain.</summary>
    nint DesktopWindow { get; }
}
