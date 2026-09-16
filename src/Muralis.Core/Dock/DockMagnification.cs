using Muralis.Core.Canvas;

namespace Muralis.Core.Dock;

/// <summary>Where one item is drawn: how large it is, and where its centre has slid to.</summary>
public readonly record struct DockItemLayout(double Scale, double CenterAlongDip);

/// <summary>Every item's place along the rail for one pointer position, and how long the run is.</summary>
public sealed class DockMagnificationLayout
{
    internal DockMagnificationLayout(IReadOnlyList<DockItemLayout> items, double runLengthDip)
    {
        Items = items;
        RunLengthDip = runLengthDip;
    }

    /// <summary>One entry per item, in dock order.</summary>
    public IReadOnlyList<DockItemLayout> Items { get; }

    /// <summary>How much rail the run occupies at these scales, padding excluded.</summary>
    public double RunLengthDip { get; }
}

/// <summary>
/// How the dock magnifies: the item under the pointer is largest, its neighbours grow less the
/// further away they are, and every item is placed so that nothing overlaps.
/// </summary>
/// <remarks>
/// <para>
/// Two steps, both pure. First each item's scale comes from the distance between the pointer and
/// the item's <em>resting</em> centre — the centre it has when nothing is magnified. Measuring
/// against the resting centres is what keeps the curve honest: measuring against the positions the
/// items have been pushed to would feed the packing back into the magnification and make the run
/// creep along the rail as the pointer moves.
/// </para>
/// <para>
/// Then the run is laid out edge to edge: each item takes its magnified width and is separated from
/// its neighbour by the resting gap grown in proportion to the two scales. Growing the gap with the
/// icons is what keeps the dock looking like itself at every size, and taking the widths from the
/// scales is what guarantees no two items ever overlap, however large the pointer makes them.
/// Finally the whole run is re-centred on the rail, so it stays where the user expects it instead of
/// drifting towards the magnified side.
/// </para>
/// </remarks>
public static class DockMagnification
{
    /// <summary>
    /// The layout for a pointer at <paramref name="pointerAlongDip"/> along the rail. A null pointer
    /// is "away": every item is at its natural size in its resting place.
    /// </summary>
    public static DockMagnificationLayout Compute(
        double? pointerAlongDip,
        IReadOnlyList<double> restingCentres,
        double railCentreAlongDip,
        DockOptions dock)
    {
        ArgumentNullException.ThrowIfNull(restingCentres);
        ArgumentNullException.ThrowIfNull(dock);

        var count = restingCentres.Count;
        if (count == 0)
        {
            return new DockMagnificationLayout([], 0);
        }

        var scales = new double[count];
        for (var i = 0; i < count; i++)
        {
            scales[i] = pointerAlongDip is { } pointer
                ? CanvasProximity.ScaleAt(
                    Math.Abs(pointer - restingCentres[i]),
                    dock.MaxScale,
                    dock.InfluenceRadiusDip,
                    dock.Falloff)
                : 1.0;
        }

        return Pack(dock, scales, railCentreAlongDip);
    }

    /// <summary>
    /// The same packing for scales that were decided elsewhere — the dock's own items use the pointer,
    /// and nothing else needs to know how the scales were arrived at.
    /// </summary>
    public static DockMagnificationLayout Pack(DockOptions dock, IReadOnlyList<double> scales, double railCentreAlongDip)
    {
        ArgumentNullException.ThrowIfNull(dock);
        ArgumentNullException.ThrowIfNull(scales);

        var count = scales.Count;
        if (count == 0)
        {
            return new DockMagnificationLayout([], 0);
        }

        var half = new double[count];
        var runLength = 0.0;
        for (var i = 0; i < count; i++)
        {
            half[i] = dock.ItemSizeDip * Math.Max(1.0, scales[i]) / 2.0;
            runLength += half[i] * 2;
            if (i > 0)
            {
                runLength += Gap(dock, scales[i - 1], scales[i]);
            }
        }

        var items = new DockItemLayout[count];
        var cursor = railCentreAlongDip - (runLength / 2.0);
        for (var i = 0; i < count; i++)
        {
            items[i] = new DockItemLayout(scales[i], cursor + half[i]);
            cursor += (half[i] * 2) + (i + 1 < count ? Gap(dock, scales[i], scales[i + 1]) : 0);
        }

        return new DockMagnificationLayout(items, runLength);
    }

    /// <summary>
    /// The gap between two neighbours: the resting gap, grown with how large the two of them are, so
    /// the dock keeps its proportions when magnified.
    /// </summary>
    private static double Gap(DockOptions dock, double left, double right) =>
        dock.SpacingDip * (Math.Max(1.0, left) + Math.Max(1.0, right)) / 2.0;
}
