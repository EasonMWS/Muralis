using System.Runtime.InteropServices;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Icons;

/// <summary>
/// Reads an icon out of the shell. The shell says which slot a file's icon occupies in its image
/// lists, the image list hands out an icon handle for that slot, and the handle's pixels are copied
/// out through GDI. This is the only place in the desktop layer that touches the shell's icon
/// machinery: what leaves this class is premultiplied pixels in the shape a surface upload wants,
/// and what happens to them is not its business.
/// </summary>
/// <remarks>
/// Runs on the cache's STA worker, never on the shell thread: the shell's image lists want COM
/// initialized, and a shell call is exactly the kind of work the shell thread must not wait for.
/// No window is involved anywhere here.
/// </remarks>
internal static class ShellIconReader
{
    /// <summary>
    /// Which of the shell's image lists to read for an item drawn at roughly
    /// <paramref name="wantedPixels"/> device pixels. The jumbo list is sized for high-DPI displays
    /// and modern applications ship artwork for it; the smaller lists are for items small enough
    /// that a 256 pixel image would be wasted memory.
    /// </summary>
    internal static int ImageListFor(int wantedPixels) => wantedPixels switch
    {
        <= 16 => NativeMethods.ShilSmall,
        <= 20 => NativeMethods.ShilLarge,
        <= 44 => NativeMethods.ShilExtraLarge,
        _ => NativeMethods.ShilJumbo,
    };

    /// <summary>The icon for a file system path, or null when the shell has none to give.</summary>
    internal static IconBitmap? Read(string path, int wantedPixels)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // A target that is gone has no icon of its own: the shell would hand out the generic
        // "unknown file" picture, which is not what the item should show.
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        var info = default(NativeMethods.ShFileInfo);
        var found = NativeMethods.SHGetFileInfoW(
            path,
            0,
            ref info,
            (uint)Marshal.SizeOf<NativeMethods.ShFileInfo>(),
            NativeMethods.ShgfiSysIconIndex);

        if (found == nint.Zero)
        {
            return null;
        }

        IImageList? list = null;
        var icon = nint.Zero;
        try
        {
            if (NativeMethods.SHGetImageList(ImageListFor(wantedPixels), in NativeMethods.ImageListId, out list) < 0 || list is null)
            {
                return null;
            }

            if (list.GetIcon(info.IconIndex, NativeMethods.IldTransparent, out icon) < 0 || icon == nint.Zero)
            {
                return null;
            }

            return ReadPixels(icon);
        }
        finally
        {
            if (icon != nint.Zero)
            {
                NativeMethods.DestroyIcon(icon);
            }

            if (list is not null && Marshal.IsComObject(list))
            {
                Marshal.ReleaseComObject(list);
            }
        }
    }

    /// <summary>Copies an icon handle's colour bitmap out as top-down 32bpp premultiplied BGRA.</summary>
    private static IconBitmap? ReadPixels(nint icon)
    {
        if (!NativeMethods.GetIconInfo(icon, out var info))
        {
            return null;
        }

        // The colour bitmap holds the pixels; the mask is the legacy shape behind them and is only
        // consulted if the shell ever hands out an icon without a colour bitmap at all.
        var source = info.ColorBitmap != nint.Zero ? info.ColorBitmap : info.MaskBitmap;
        try
        {
            if (source == nint.Zero)
            {
                return null;
            }

            var shape = default(NativeMethods.Bitmap);
            if (NativeMethods.GetObjectW(source, Marshal.SizeOf<NativeMethods.Bitmap>(), ref shape) == 0
                || shape.Width <= 0
                || shape.Height <= 0)
            {
                return null;
            }

            var width = shape.Width;
            var height = shape.Height;
            var pixels = new byte[width * height * 4];
            var header = new NativeMethods.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                Width = width,

                // Negative height asks for top-down rows: the order a texture upload wants.
                Height = -height,
                Planes = 1,
                BitCount = 32,
                SizeImage = (uint)(width * height * 4),
            };

            // A device context is required for the conversion even though the source is a bitmap.
            var dc = NativeMethods.GetDC(nint.Zero);
            if (dc == nint.Zero)
            {
                return null;
            }

            int lines;
            try
            {
                lines = NativeMethods.GetDIBits(dc, source, 0, (uint)height, pixels, ref header, 0);
            }
            finally
            {
                NativeMethods.ReleaseDC(nint.Zero, dc);
            }

            if (lines == 0)
            {
                return null;
            }

            var transparencyMask = info.ColorBitmap == nint.Zero
                ? null
                : ReadTransparencyMask(info.MaskBitmap, width, height);
            ToPremultipliedBgra(pixels, shape.BitsPixel >= 32, transparencyMask);
            return new IconBitmap(width, height, pixels);
        }
        finally
        {
            if (info.ColorBitmap != nint.Zero)
            {
                NativeMethods.DeleteObject(info.ColorBitmap);
            }

            if (info.MaskBitmap != nint.Zero)
            {
                NativeMethods.DeleteObject(info.MaskBitmap);
            }
        }
    }

    /// <summary>
    /// Converts an icon that arrived as straight BGRA into the premultiplied form a composition
    /// surface wants: an alpha of 0 clears the colour, a partial alpha scales it. The one thing
    /// that needs repairing first is the icon with no usable alpha at all — it arrives as 32bpp
    /// with every alpha byte zero, and read literally that is a fully transparent image, so those
    /// pixels are declared opaque instead of being allowed to vanish.
    /// </summary>
    internal static void ToPremultipliedBgra(
        byte[] pixels,
        bool hasAlphaChannel,
        byte[]? transparencyMask = null)
    {
        var hasAlpha = false;
        if (hasAlphaChannel)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }
        }

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var pixel = i / 4;
            var maskedOut = !hasAlpha
                && transparencyMask is not null
                && pixel < transparencyMask.Length
                && transparencyMask[pixel] != 0;
            var alpha = hasAlpha ? pixels[i + 3] : maskedOut ? (byte)0 : (byte)255;

            if (alpha == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
            else if (alpha != 255)
            {
                pixels[i] = (byte)(pixels[i] * alpha / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
            }

            pixels[i + 3] = alpha;
        }
    }

    /// <summary>
    /// Reads the icon's legacy AND mask as one byte per pixel: non-zero means transparent. This is
    /// essential for classic icons whose 32-bit colour bitmap has no alpha data; treating every pixel
    /// as opaque gives those otherwise-correct icons a black or white square background.
    /// </summary>
    private static byte[]? ReadTransparencyMask(nint maskBitmap, int width, int height)
    {
        if (maskBitmap == nint.Zero || width <= 0 || height <= 0)
        {
            return null;
        }

        var shape = default(NativeMethods.Bitmap);
        if (NativeMethods.GetObjectW(maskBitmap, Marshal.SizeOf<NativeMethods.Bitmap>(), ref shape) == 0)
        {
            return null;
        }

        var maskWidth = Math.Min(width, Math.Abs(shape.Width));
        var maskHeight = Math.Min(height, Math.Abs(shape.Height));
        if (maskWidth <= 0 || maskHeight <= 0)
        {
            return null;
        }

        var bgra = new byte[maskWidth * maskHeight * 4];
        var header = new NativeMethods.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
            Width = maskWidth,
            Height = -maskHeight,
            Planes = 1,
            BitCount = 32,
            SizeImage = (uint)bgra.Length,
        };

        var dc = NativeMethods.GetDC(nint.Zero);
        if (dc == nint.Zero)
        {
            return null;
        }

        try
        {
            if (NativeMethods.GetDIBits(dc, maskBitmap, 0, (uint)maskHeight, bgra, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(nint.Zero, dc);
        }

        var mask = new byte[width * height];
        for (var y = 0; y < maskHeight; y++)
        {
            for (var x = 0; x < maskWidth; x++)
            {
                var source = (y * maskWidth + x) * 4;
                mask[y * width + x] = (byte)((bgra[source] | bgra[source + 1] | bgra[source + 2]) == 0 ? 0 : 1);
            }
        }

        return mask;
    }
}
