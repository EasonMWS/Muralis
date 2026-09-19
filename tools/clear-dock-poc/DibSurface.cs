namespace ClearDockPoc;

/// <summary>
/// A 32-bit premultiplied BGRA surface the layered window presents.
/// </summary>
/// <remarks>
/// <para>
/// The whole surface is cleared to alpha 0 on every frame, so anything not drawn is not painted at all and the
/// desktop behind shows through. That is the entire point of this renderer: there is no background pass, no
/// plate, and no fill to forget to remove.
/// </para>
/// <para>
/// Icon pixels arrive from the shell already premultiplied (see the icon reader's own contract), so drawing one
/// is a copy and not a blend. A blend here would double-multiply the alpha and darken every icon edge.
/// </para>
/// </remarks>
internal sealed class DibSurface
{
    public DibSurface(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = width * 4;
        Pixels = new byte[Stride * height];
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }

    /// <summary>
    /// Fills a rectangle with a premultiplied colour. Used only by the flat-colour mode, which keeps the pixel
    /// proof reproducible without depending on the shell.
    /// </summary>
    public void FillRect(int left, int top, int width, int height, byte b, byte g, byte r, byte a)
    {
        for (var y = top; y < top + height; y++)
        {
            if (y < 0 || y >= Height)
            {
                continue;
            }

            var row = (y * Stride) + (left * 4);
            for (var x = left; x < left + width; x++)
            {
                if (x < 0 || x >= Width)
                {
                    row += 4;
                    continue;
                }

                Pixels[row + 0] = b;
                Pixels[row + 1] = g;
                Pixels[row + 2] = r;
                Pixels[row + 3] = a;
                row += 4;
            }
        }
    }

    /// <summary>Returns every pixel to fully transparent.</summary>
    public void Clear() => Array.Clear(Pixels);

    /// <summary>
    /// Draws a premultiplied BGRA image so that it fills <paramref name="boxHeight"/> exactly, centred on
    /// <paramref name="centreX"/> with its bottom edge at <paramref name="bottomY"/>.
    /// </summary>
    /// <remarks>
    /// The scale is nearest-neighbour on purpose for this proof: it keeps the adapter free of a resampler that
    /// would have to be right before the transparency question it exists to answer could be settled. A real
    /// renderer would resample once per distinct size and cache it.
    /// </remarks>
    public void DrawIcon(
        byte[] source, int sourceWidth, int sourceHeight,
        int centreX, int bottomY, int boxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || boxHeight <= 0)
        {
            return;
        }

        // A square box, so the icon keeps its aspect ratio the way the XAML icon does with Stretch.Uniform.
        var box = boxHeight;
        var drawnWidth = box;
        var drawnHeight = box;

        var left = centreX - (drawnWidth / 2);
        var top = bottomY - drawnHeight;

        for (var y = 0; y < drawnHeight; y++)
        {
            var destinationY = top + y;
            if (destinationY < 0 || destinationY >= Height)
            {
                continue;
            }

            var sourceY = (int)((long)y * sourceHeight / drawnHeight);
            if (sourceY >= sourceHeight)
            {
                sourceY = sourceHeight - 1;
            }

            var destinationRow = (destinationY * Stride) + (left * 4);
            var sourceRow = sourceY * sourceWidth * 4;

            for (var x = 0; x < drawnWidth; x++)
            {
                var destinationX = left + x;
                if (destinationX < 0 || destinationX >= Width)
                {
                    destinationRow += 4;
                    continue;
                }

                var sourceX = (int)((long)x * sourceWidth / drawnWidth);
                if (sourceX >= sourceWidth)
                {
                    sourceX = sourceWidth - 1;
                }

                var s = sourceRow + (sourceX * 4);
                Pixels[destinationRow + 0] = source[s + 0];
                Pixels[destinationRow + 1] = source[s + 1];
                Pixels[destinationRow + 2] = source[s + 2];
                Pixels[destinationRow + 3] = source[s + 3];
                destinationRow += 4;
            }
        }
    }
}
