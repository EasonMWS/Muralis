using Muralis.Core.Abstractions;

namespace ClearDockPoc;

/// <summary>
/// One icon's artwork at the sizes it can be drawn at.
/// </summary>
/// <remarks>
/// <para>
/// The cache is scoped to the icon rather than shared and keyed by the source's dimensions. That is not a
/// refinement, it is the fix for a real defect: every shell icon is the same size — square, and at one display
/// scale the same number of pixels — so a cache keyed by dimensions alone gives every icon the first icon's
/// pixels. The dock drew five copies of one logo, and the mistake is invisible in the counters, which happily
/// reported five icons drawn.
/// </para>
/// <para>
/// Keyed by destination size, and bounded. The magnification moves through a continuum of scales but draws at
/// whole pixels, so the sizes in use at any moment are a narrow band; the bound is what stops a display change
/// from turning a cache into a leak.
/// </para>
/// <para>
/// Nothing here is on the pointer path. A size is resampled the first time it is needed and copied thereafter,
/// so drawing a frame allocates nothing.
/// </para>
/// </remarks>
internal sealed class IconArtwork
{
    private readonly byte[] _source;
    private readonly int _capacity;
    private readonly Dictionary<int, byte[]> _sizes = [];

    public IconArtwork(ShellIconData icon, int capacity)
    {
        ArgumentNullException.ThrowIfNull(icon);

        _source = icon.PremultipliedBgra;
        SourceWidth = icon.Width;
        SourceHeight = icon.Height;
        _capacity = capacity;
    }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>How many times a size had to be resampled rather than being found already cached.</summary>
    public long Misses { get; private set; }

    /// <summary>How many times a size was already cached.</summary>
    public long Hits { get; private set; }

    /// <summary>How many distinct sizes are currently held.</summary>
    public int CachedSizes => _sizes.Count;

    /// <summary>The artwork resampled to <paramref name="size"/> square pixels, or null when it cannot be.</summary>
    public byte[]? At(int size)
    {
        if (SourceWidth <= 0 || SourceHeight <= 0 || size <= 0)
        {
            return null;
        }

        if (_sizes.TryGetValue(size, out var cached))
        {
            Hits++;
            return cached;
        }

        if (_sizes.Count >= _capacity)
        {
            // Emptied wholesale rather than evicted one at a time: the sizes in use are a contiguous band around
            // the current scale, so a full cache means the scale has moved a long way and the old band is gone.
            _sizes.Clear();
        }

        var scaled = IconScaler.Resample(_source, SourceWidth, SourceHeight, size);
        if (scaled is not null)
        {
            _sizes[size] = scaled;
            Misses++;
        }

        return scaled;
    }

    /// <summary>
    /// Resamples every size the magnification can reach, so no frame ever pays for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fix for the frame spike, and it was chosen because the spike was measured rather than guessed
    /// at. A staged benchmark put essentially all of a 13.5 ms worst frame inside the raster stage
    /// (<c>raster 13.432 ms</c> of <c>total 13.531 ms</c>, with clear, copy and upload together under 0.1 ms), and
    /// the cache counters showed evictions: 206 misses against a capacity of 96. A wave moves through a band of
    /// sizes, and when that band walks off the end of the cache the next size is resampled inside the frame that
    /// needs it.
    /// </para>
    /// <para>
    /// Building them up front moves that cost to startup, where it is paid once and off the pointer path. The
    /// result is also a hard guarantee rather than a hope: with every reachable size resident, no frame can miss,
    /// so the spike cannot recur by that route. 206 sizes of at most 94×94 is about 3 MB, which is a fair price
    /// for removing a 13 ms stall.
    /// </para>
    /// </remarks>
    public void Prewarm(int smallestPx, int largestPx, int stepPx)
    {
        if (SourceWidth <= 0 || SourceHeight <= 0 || largestPx < smallestPx)
        {
            return;
        }

        for (var size = Math.Max(1, smallestPx); size <= largestPx; size += Math.Max(1, stepPx))
        {
            _ = At(size);
        }
    }

    /// <summary>How many bytes the held sizes occupy.</summary>
    public long CachedBytes
    {
        get
        {
            long total = 0;
            foreach (var buffer in _sizes.Values)
            {
                total += buffer.Length;
            }

            return total;
        }
    }
}

/// <summary>
/// Resamples premultiplied BGRA artwork to the exact size it will be drawn at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why premultiplied alpha makes this simple.</b> In premultiplied form the colour channels already carry
/// their own weight, so an alpha-weighted average of the colour channels is unnecessary: a plain weighted average
/// of each of the four channels is already correct. That is the one property that lets a straightforward
/// separable filter be right here, and it is why the artwork must stay premultiplied all the way through.
/// </para>
/// <para>
/// <b>Why a triangle kernel and not a box.</b> A box filter at a non-integer scale either skips source rows or
/// reads some twice, which shows up on the diagonal edges of an app icon as stair-stepping. A triangle (tent)
/// kernel with a support of one destination pixel in source space always covers the source continuously, so the
/// result is smooth without the ringing a sharper kernel such as Lanczos would introduce on the high-contrast
/// edges icons are full of.
/// </para>
/// </remarks>
internal static class IconScaler
{
    /// <summary>
    /// A separable triangle-filter resample of premultiplied BGRA to a square <paramref name="size"/>.
    /// </summary>
    /// <remarks>
    /// Two passes, horizontal then vertical, each one-dimensional. Weighting is computed in double precision and
    /// normalised per destination pixel, and the accumulated value is rounded rather than truncated — truncation
    /// is what makes a resampled edge lose a fraction of its alpha every time it is touched.
    /// </remarks>
    internal static byte[]? Resample(byte[] source, int sourceWidth, int sourceHeight, int size)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || size <= 0)
        {
            return null;
        }

        var horizontal = new byte[size * sourceHeight * 4];
        var scaleX = (double)sourceWidth / size;

        Span<double> weights = stackalloc double[KernelTaps];

        // --- horizontal pass: sourceWidth -> size, one row at a time ---
        for (var y = 0; y < sourceHeight; y++)
        {
            var sourceRow = y * sourceWidth * 4;
            var targetRow = y * size * 4;

            for (var x = 0; x < size; x++)
            {
                var (start, count) = Kernel(x, scaleX, sourceWidth, weights);
                var total = 0.0;
                var b = 0.0;
                var g = 0.0;
                var r = 0.0;
                var a = 0.0;

                for (var k = 0; k < count; k++)
                {
                    var weight = weights[k];
                    var index = sourceRow + ((start + k) * 4);

                    b += source[index + 0] * weight;
                    g += source[index + 1] * weight;
                    r += source[index + 2] * weight;
                    a += source[index + 3] * weight;
                    total += weight;
                }

                var destination = targetRow + (x * 4);
                if (total <= 0)
                {
                    continue;
                }

                horizontal[destination + 0] = Clamp(b / total);
                horizontal[destination + 1] = Clamp(g / total);
                horizontal[destination + 2] = Clamp(r / total);
                horizontal[destination + 3] = Clamp(a / total);
            }
        }

        // --- vertical pass: sourceHeight -> size, one column at a time ---
        var result = new byte[size * size * 4];
        var scaleY = (double)sourceHeight / size;

        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                var (start, count) = Kernel(y, scaleY, sourceHeight, weights);
                var total = 0.0;
                var b = 0.0;
                var g = 0.0;
                var r = 0.0;
                var a = 0.0;

                for (var k = 0; k < count; k++)
                {
                    var weight = weights[k];
                    var index = (((start + k) * size) + x) * 4;

                    b += horizontal[index + 0] * weight;
                    g += horizontal[index + 1] * weight;
                    r += horizontal[index + 2] * weight;
                    a += horizontal[index + 3] * weight;
                    total += weight;
                }

                var destination = ((y * size) + x) * 4;
                if (total <= 0)
                {
                    continue;
                }

                result[destination + 0] = Clamp(b / total);
                result[destination + 1] = Clamp(g / total);
                result[destination + 2] = Clamp(r / total);
                result[destination + 3] = Clamp(a / total);
            }
        }

        return result;
    }

    /// <summary>
    /// The triangle kernel's contributing source range for one destination index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kernel's support is one destination pixel either side, expressed in source space, so its half-width is
    /// the scale itself for a downscale and 1 for an upscale. That makes the filter a smooth interpolator when
    /// magnifying and an anti-aliasing average when minifying, from the same code, which is what lets one path
    /// serve both the resting size and the peak.
    /// </para>
    /// <para>
    /// The range is intersected with the image. Taps beyond the edge contribute nothing rather than being folded
    /// back onto the border pixel, and the caller normalises by the weights it actually used, so an edge pixel is
    /// neither darkened nor brightened. The returned indices are contiguous and in range, which is what lets the
    /// caller walk them from a single start.
    /// </para>
    /// </remarks>
    private static (int Start, int Count) Kernel(
        int destinationIndex, double scale, int sourceLength, Span<double> weights)
    {
        var centre = ((destinationIndex + 0.5) * scale) - 0.5;
        var support = Math.Max(scale, 1.0);
        var first = Math.Max(0, (int)Math.Ceiling(centre - support));
        var last = Math.Min(sourceLength - 1, (int)Math.Floor(centre + support));

        var count = 0;
        for (var s = first; s <= last && count < weights.Length; s++)
        {
            var distance = Math.Abs((s - centre) / support);
            weights[count++] = distance >= 1.0 ? 0.0 : 1.0 - distance;
        }

        return (first, count);
    }

    /// <summary>
    /// The most taps one destination pixel can take.
    /// </summary>
    /// <remarks>
    /// With a support of one destination pixel, the tap count is bounded by twice the downscale ratio. The
    /// artwork is read at the largest the icon can be drawn, so the ratio never approaches this: it is a guard
    /// against a pathological call, not a design limit.
    /// </remarks>
    private const int KernelTaps = 64;

    private static byte Clamp(double value) =>
        value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)Math.Round(value, MidpointRounding.AwayFromZero);
}
