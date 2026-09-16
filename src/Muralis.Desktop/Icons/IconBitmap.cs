namespace Muralis.Desktop.Icons;

/// <summary>
/// One resolved icon: premultiplied BGRA pixels, top-down and tightly packed, which is the shape a
/// texture upload wants. It carries its own size, because the shell gives whatever resolution it
/// has for an icon rather than the one that was asked for.
/// </summary>
internal sealed class IconBitmap
{
    internal IconBitmap(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal byte[] Pixels { get; }

    internal int ByteCount => Pixels.Length;
}
