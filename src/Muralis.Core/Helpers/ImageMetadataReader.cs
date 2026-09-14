namespace Muralis.Core.Helpers;

/// <summary>
/// Reads pixel dimensions from PNG/JPEG/BMP headers without decoding image data.
/// Returns <see langword="false"/> for unsupported or malformed files instead of throwing.
/// </summary>
public static class ImageMetadataReader
{
    public static bool TryReadDimensions(string filePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using var stream = File.OpenRead(filePath);
            return TryReadDimensions(stream, out width, out height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryReadDimensions(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;

        Span<byte> buffer = stackalloc byte[16];
        if (!TryReadExactly(stream, buffer[..8]))
        {
            return false;
        }

        // PNG: 89 50 4E 47 ... then IHDR width/height as big-endian int32.
        if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
        {
            return TryReadPngDimensions(stream, out width, out height);
        }

        // JPEG: FF D8, scan segments for a start-of-frame marker.
        if (buffer[0] == 0xFF && buffer[1] == 0xD8)
        {
            return TryReadJpegDimensions(stream, out width, out height);
        }

        // BMP: 'BM', dimensions at offset 18 (little-endian int32 each).
        if (buffer[0] == (byte)'B' && buffer[1] == (byte)'M')
        {
            if (stream.CanSeek)
            {
                stream.Seek(18, SeekOrigin.Begin);
            }

            if (!TryReadExactly(stream, buffer[..8]))
            {
                return false;
            }

            width = BitConverter.ToInt32(buffer[..4]);
            height = Math.Abs(BitConverter.ToInt32(buffer[4..8]));
            return width > 0 && height > 0;
        }

        return false;
    }

    private static bool TryReadPngDimensions(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;

        Span<byte> ihdr = stackalloc byte[16];
        if (!TryReadExactly(stream, ihdr))
        {
            return false;
        }

        if (ihdr[4] != (byte)'I' || ihdr[5] != (byte)'H' || ihdr[6] != (byte)'D' || ihdr[7] != (byte)'R')
        {
            return false;
        }

        width = (ihdr[8] << 24) | (ihdr[9] << 16) | (ihdr[10] << 8) | ihdr[11];
        height = (ihdr[12] << 24) | (ihdr[13] << 16) | (ihdr[14] << 8) | ihdr[15];
        return width > 0 && height > 0;
    }

    private static bool TryReadJpegDimensions(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;

        // The caller has already consumed the SOI marker; segment scanning starts after it.
        if (!stream.CanSeek)
        {
            return false;
        }

        stream.Seek(2, SeekOrigin.Begin);

        Span<byte> buffer = stackalloc byte[8];
        while (true)
        {
            if (!TryReadExactly(stream, buffer[..2]))
            {
                return false;
            }

            if (buffer[0] != 0xFF)
            {
                return false;
            }

            var marker = buffer[1];

            // Markers without a payload.
            if (marker is 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (marker == 0xD9)
            {
                return false;
            }

            if (!TryReadExactly(stream, buffer[..2]))
            {
                return false;
            }

            var segmentLength = (buffer[0] << 8) | buffer[1];
            if (segmentLength < 2)
            {
                return false;
            }

            var isStartOfFrame = marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
                or 0xC5 or 0xC6 or 0xC7
                or 0xC9 or 0xCA or 0xCB
                or 0xCD or 0xCE or 0xCF;

            if (isStartOfFrame)
            {
                Span<byte> sof = stackalloc byte[5];
                if (!TryReadExactly(stream, sof))
                {
                    return false;
                }

                height = (sof[1] << 8) | sof[2];
                width = (sof[3] << 8) | sof[4];
                return width > 0 && height > 0;
            }

            if (!stream.CanSeek)
            {
                return false;
            }

            stream.Seek(segmentLength - 2, SeekOrigin.Current);
        }
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = stream.Read(destination[totalRead..]);
            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }
}
