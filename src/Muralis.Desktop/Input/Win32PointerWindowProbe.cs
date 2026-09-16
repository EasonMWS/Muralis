using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Input;

/// <summary>Reads the parent chain of real windows for the context walk.</summary>
internal sealed class Win32PointerWindowProbe : IPointerWindowProbe
{
    private const int MaxClassNameLength = 64;

    private readonly char[] _buffer = new char[MaxClassNameLength];

    internal Win32PointerWindowProbe() => DesktopWindow = NativeMethods.GetDesktopWindow();

    public nint DesktopWindow { get; }

    public nint ParentOf(nint window) => NativeMethods.GetAncestor(window, NativeMethods.GaParent);

    public string? ClassNameOf(nint window)
    {
        var length = NativeMethods.GetClassNameW(window, _buffer, _buffer.Length);
        return length > 0 ? new string(_buffer, 0, length) : null;
    }
}
