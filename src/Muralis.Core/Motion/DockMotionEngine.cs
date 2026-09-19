namespace Muralis.Core.Motion;

/// <summary>
/// The Nexus Motion Engine: given where the pointer is and where the icons rest, it says how large each
/// icon is drawn and how far it has moved.
/// </summary>
/// <remarks>
/// <para>
/// The engine is pure arithmetic and holds no state but its own scratch arrays, so it can be driven at
/// pointer rate from the UI thread without allocating, and unit-tested without a window. It knows
/// nothing about XAML, composition, or the pointer's own type.
/// </para>
/// <para>
/// Three steps, in this order:
/// </para>
/// <list type="number">
/// <item>
/// <b>Influence.</b> Each icon's scale comes from the distance between the pointer and the centre the
/// icon <em>rests</em> at — never the centre it has been pushed to. Measuring against the resting
/// centres is what keeps the wave honest: feeding the displacement back in would make the peak creep
/// along the dock as the pointer moved, and the wave would lag its own cause.
/// </item>
/// <item>
/// <b>Space.</b> Enlarging an icon alone would make it overlap its neighbours. Every other icon is
/// therefore pushed away from the peak by exactly the extra half-width the icons between it and the
/// peak have taken. Because the push is the accumulated difference of half-widths, the order of the
/// icons is preserved, no two ever overlap, and icons outside the wave are pushed by nothing at all.
/// </item>
/// <item>
/// <b>Lift.</b> The icons near the pointer rise a little. The lift is the square of the scale's own
/// influence, so it dies away faster than the size does and the dock never looks like it is jumping.
/// </list>
/// <para>
/// The pointer is not an icon. It is a continuous position: parked between two icons it gives them the
/// same influence, and the peak of the wave stays exactly under the hand.
/// </remarks>
public sealed class DockMotionEngine
{
    /// <summary>
    /// Anything this far from changing is not changing. The far end of a long dock lands within a
    /// billionth of its resting scale, and a billionth of a scale is not a value worth writing to a
    /// compositor or calling a movement: it is the tail of a curve that has already reached zero.
    /// </summary>
    private const double Negligible = 1e-9;

    private readonly DockMotionSample[] _samples;
    private readonly double[] _scales;
    private readonly double[] _halfWidths;
    private readonly double[] _translations;

    public DockMotionEngine(int capacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _samples = new DockMotionSample[capacity];
        _scales = new double[capacity];
        _halfWidths = new double[capacity];
        _translations = new double[capacity];
        for (var i = 0; i < capacity; i++)
        {
            _samples[i] = new DockMotionSample();
        }
    }

    /// <summary>What the engine worked out on the last call to <see cref="Apply"/>.</summary>
    public IReadOnlyList<DockMotionSample> Samples => _samples;

    /// <summary>How many icons the last call covered.</summary>
    public int Count { get; private set; }

    /// <summary>The largest scale the last call produced. 1 when the engine was not moving anything.</summary>
    public double PeakScale { get; private set; } = 1;

    /// <summary>
    /// How far the furthest icon was pushed from where it rests, in DIP, on the last call. The dock
    /// window needs this much room on each side, so a magnified icon is never clipped.
    /// </summary>
    public double HorizontalReach { get; private set; }

    /// <summary>Whether the last call actually moved anything.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Lays the dock out for a pointer at <paramref name="pointerAlongDip"/>.
    /// </summary>
    /// <param name="layout">Where the icons rest. Must not change while the pointer is moving.</param>
    /// <param name="pointerAlongDip">
    /// Where the pointer is along the dock, in DIP, in the same space as the layout's centres. Null means
    /// the pointer is away, and every icon is put back to rest.
    /// </param>
    /// <param name="profile">The numbers to use.</param>
    public void Apply(DockMotionLayout layout, double? pointerAlongDip, DockMotionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(profile);

        var count = layout.Count;
        if (count > _samples.Length || count > _scales.Length)
        {
            throw new ArgumentException(
                $"This engine was built for {_samples.Length} icons but the layout has {count}.",
                nameof(layout));
        }

        Count = count;
        if (count == 0)
        {
            PeakScale = 1;
            HorizontalReach = 0;
            IsActive = false;
            return;
        }

        var resting = layout.Centres;
        var widths = layout.Widths;

        // Away, switched off, or a layout that is still settling: rest. This is the one path that has to
        // be right when everything else has gone wrong, because it is what an icon does when the engine
        // cannot run at all.
        if (pointerAlongDip is not { } pointer || !profile.IsMoving || !layout.IsSane)
        {
            for (var i = 0; i < count; i++)
            {
                Reset(_samples[i], i);
            }

            PeakScale = 1;
            HorizontalReach = 0;
            IsActive = false;
            return;
        }

        // The peak is the largest icon, and the one place that does not move. It is found from the scales
        // themselves — and found here, where each scale has just been written — rather than from where the
        // running total turns over: with a wide wave the total keeps rising for a while past the peak, and
        // near the rim it turns over early. Its sign is a statement about the whole run, not about which
        // icon is largest.
        var peak = 1.0;
        var peakIndex = -1;
        for (var i = 0; i < count; i++)
        {
            var scale = ScaleAt(Math.Abs(pointer - resting[i]), profile);
            if (scale - 1.0 < Negligible)
            {
                scale = 1.0;
            }

            _scales[i] = scale;

            // The first icon claims the peak outright. A comparison against a running maximum would leave
            // the peak unclaimed on a dock where the pointer rounds every scale down to exactly one.
            if (peakIndex < 0 || scale > peak)
            {
                peak = scale;
                peakIndex = i;
            }
        }

        peakIndex = Math.Max(0, peakIndex);

        // Everything that has grown has to be given room, and the room is taken from both sides of the peak
        // at once so the icon under the pointer keeps the neighbours it had.
        var half = _halfWidths;
        for (var i = 0; i < count; i++)
        {
            half[i] = (_scales[i] - 1.0) * widths[i] / 2.0;
        }

        Shift(_translations, count, half, peakIndex, profile.NeighbourSpread);

        var reach = 0.0;
        var span = Math.Max(0.0000001, peak - 1.0);
        for (var i = 0; i < count; i++)
        {
            var sample = _samples[i];
            var influence = (_scales[i] - 1.0) / span;

            sample.Index = i;
            sample.Scale = _scales[i];
            sample.TranslateX = _translations[i];
            sample.Lift = profile.MaximumLift * influence * influence;
            sample.Influence = influence;

            var distance = Math.Abs(_translations[i]);
            if (distance > reach)
            {
                reach = distance;
            }
        }

        PeakScale = peak;
        HorizontalReach = reach;
        IsActive = peak > 1.0000001;
    }

    /// <summary>
    /// The scale an icon takes when the pointer is <paramref name="distanceDip"/> away from where it
    /// rests. Exactly <paramref name="profile"/>'s <see cref="DockMotionProfile.MaxScale"/> at zero, and
    /// exactly 1 at or beyond the influence radius, with a smooth curve in between and no corner at
    /// either end.
    /// </summary>
    public static double ScaleAt(double distanceDip, DockMotionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var ceiling = Math.Max(1.0, profile.MaxScale);
        var radius = profile.InfluenceRadius;
        if (radius <= 0 || ceiling <= 1.0)
        {
            return 1.0;
        }

        var distance = Math.Max(0.0, distanceDip);
        if (distance >= radius)
        {
            return 1.0;
        }

        return 1.0 + ((ceiling - 1.0) * Falloff(1.0 - (distance / radius)));
    }

    /// <summary>
    /// The influence curve: 1 under the pointer, 0 at the rim, and never negative in between.
    /// </summary>
    /// <remarks>
    /// A Gaussian normalised so that the rim lands on zero. Normalising is what lets the wave have an
    /// end at all — an unnormalised Gaussian would still be 4% of full size one radius out, which reads
    /// as the whole dock breathing rather than as a wave. The curve also has to be flat enough at the
    /// top: a straight line or a smoothstep between the two ends would make the icon under the pointer
    /// grow fastest exactly where the pointer is, which is what a discrete hover looks like.
    /// </remarks>
    public static double Falloff(double t)
    {
        const double RadiusOverSigma = 2.5;
        var clamped = Math.Clamp(t, 0.0, 1.0);
        if (clamped <= 0)
        {
            return 0;
        }

        var edge = Math.Exp(-RadiusOverSigma * RadiusOverSigma);
        var away = 1.0 - clamped;
        var value = Math.Exp(-away * away * RadiusOverSigma * RadiusOverSigma);
        return (value - edge) / (1.0 - edge);
    }

    /// <summary>
    /// How much room a run of icons needs on each side of the dock at the largest wave it can produce.
    /// </summary>
    /// <remarks>
    /// The dock window is sized from this rather than from a constant, so a dock with thirty pins cannot
    /// have its peak clipped by a reserve that was measured for ten.
    /// </remarks>
    public static double ReserveFor(int itemCount, double baseWidth, double spacing, DockMotionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (itemCount <= 0 || !profile.IsMoving)
        {
            return 0;
        }

        var pitch = baseWidth + spacing;
        var halfRun = ((itemCount - 1) * pitch / 2.0) + (baseWidth / 2.0);
        var centres = new double[itemCount];
        var widths = new double[itemCount];
        for (var i = 0; i < itemCount; i++)
        {
            centres[i] = (i * pitch) - halfRun + (baseWidth / 2.0);
            widths[i] = baseWidth;
        }

        var layout = new DockMotionLayout(centres, widths);
        var engine = new DockMotionEngine(itemCount);
        var reach = 0.0;

        // The worst case is not at an icon: parked between two of them the peak sits between two indices and
        // both sides of the run take their full share at once. So every icon centre and every gap between
        // two of them is measured, which is the whole span the pointer can occupy.
        for (var i = 0; i < itemCount; i++)
        {
            engine.Apply(layout, centres[i], profile);
            reach = Math.Max(reach, engine.HorizontalReach);

            if (i + 1 < itemCount)
            {
                engine.Apply(layout, (centres[i] + centres[i + 1]) / 2.0, profile);
                reach = Math.Max(reach, engine.HorizontalReach);
            }
        }

        return reach;
    }

    /// <summary>
    /// Where every icon has to be drawn so that no two enlarged icons overlap and the run keeps the shape
    /// it was laid out with.
    /// </summary>
    /// <param name="translations">Filled with one shift per icon, in DIP.</param>
    /// <param name="count">How many icons there are.</param>
    /// <param name="halfWidths">The extra half-width each icon has taken by being enlarged.</param>
    /// <param name="peakIndex">The index of the largest icon.</param>
    /// <param name="spread">How much of the room the widths demand is actually opened, 0 to 1.</param>
    /// <remarks>
    /// <para>
    /// Walked outwards from the peak: the icon beside the peak gives up the room the two of them grew into
    /// the gap they share, the icon beyond that gives up the same plus the room of the gap beyond it, and
    /// so on. The peak itself does not move.
    /// </para>
    /// <para>
    /// That is what leaves every gap in the run — the two beside the peak and all of them past the wave —
    /// at exactly the gap the dock was laid out with, so the magnification opens the run outwards instead
    /// of bursting through its neighbours. A dock whose pointer stops moving therefore keeps its icon pitch
    /// to the pixel, whichever icon the pointer is on.
    /// </para>
    /// <para>
    /// Pure and separate from the engine so it can be tested on its own: it is the one piece whose
    /// correctness is a statement about every pair of neighbours at once, which is exactly the kind of
    /// thing that is easy to get subtly wrong and hard to see in a running app.
    /// </para>
    /// </remarks>
    public static void Shift(double[] translations, int count, double[] halfWidths, int peakIndex, double spread)
    {
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(halfWidths);

        if (count <= 0)
        {
            return;
        }

        peakIndex = Math.Clamp(peakIndex, 0, count - 1);

        // Outwards from the peak, one icon at a time: the icon beside the peak gives up the room the two of
        // them grew into the gap they share, the next one gives up that plus the room of the gap beyond it,
        // and so on. The peak itself does not move. That is what leaves every gap in the run — the two
        // beside the peak and every one past the wave — at exactly the gap the dock was laid out with, so
        // the magnification opens the run outwards instead of bursting through its neighbours.
        translations[peakIndex] = 0;

        var running = 0.0;
        for (var i = peakIndex; i + 1 < count; i++)
        {
            running += halfWidths[i] + halfWidths[i + 1];
            translations[i + 1] = running * spread;
        }

        running = 0.0;
        for (var i = peakIndex; i > 0; i--)
        {
            running += halfWidths[i] + halfWidths[i - 1];
            translations[i - 1] = -running * spread;
        }
    }

    private static void Reset(DockMotionSample sample, int index)
    {
        sample.Index = index;
        sample.Scale = 1;
        sample.TranslateX = 0;
        sample.Lift = 0;
        sample.Influence = 0;
    }
}
