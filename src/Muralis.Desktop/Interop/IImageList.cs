using System.Runtime.InteropServices;

namespace Muralis.Desktop.Interop;

/// <summary>
/// The shell's image lists (small / large / extra large / jumbo), where real application icons are
/// kept as bitmaps the shell already rendered. Only <see cref="GetIcon"/> is ever used — one icon by
/// its index — but every method before it has to be declared so the vtable slots line up.
/// </summary>
/// <remarks>
/// The method order here is the one the shell implements, which is not the order the
/// documentation lists them in: the interface is COM, so slot numbers are what matter.
/// </remarks>
[ComImport]
[Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    [PreserveSig]
    int Add(nint hbmImage, nint hbmMask, ref int index);

    [PreserveSig]
    int ReplaceIcon(int index, nint icon, ref int replaced);

    [PreserveSig]
    int SetOverlayImage(int image, int overlay);

    [PreserveSig]
    int Replace(nint hbmImage, nint hbmMask);

    [PreserveSig]
    int AddMasked(nint hbmImage, uint mask, ref int index);

    [PreserveSig]
    int Draw(nint unknown, int index, nint dc, int x, int y, uint style);

    [PreserveSig]
    int Remove(int index);

    /// <summary>Hands back an icon handle for the image at <paramref name="index"/>; the caller destroys it.</summary>
    [PreserveSig]
    int GetIcon(int index, uint flags, out nint icon);
}
