namespace Muralis.Core.Dock;

/// <summary>
/// Where an item being dragged along the dock would land, and where its neighbours have to move to
/// make room for it. Pure, so the drag can be driven at pointer rate without touching the document:
/// nothing is reordered until the item is really dropped.
/// </summary>
public static class DockReorder
{
    /// <summary>
    /// The index the dragged item would take if it were released where the pointer is now. Counted
    /// against the centres the other items are still at, which is monotone in the pointer position
    /// and so never flickers between two answers as the pointer jitters.
    /// </summary>
    public static int TargetIndex(double draggedAlongDip, IReadOnlyList<double> restingCentres, int draggedIndex)
    {
        ArgumentNullException.ThrowIfNull(restingCentres);

        var target = 0;
        for (var i = 0; i < restingCentres.Count; i++)
        {
            if (i == draggedIndex)
            {
                continue;
            }

            if (restingCentres[i] < draggedAlongDip)
            {
                target++;
            }
        }

        return Math.Clamp(target, 0, Math.Max(0, restingCentres.Count - 1));
    }

    /// <summary>
    /// Where every item should be drawn while the drag is in progress, as resting centres: the run
    /// opens a gap at the insertion point and everything else slides along by one place, without
    /// anything having been reordered yet.
    /// </summary>
    /// <remarks>
    /// The entry for the dragged item is the slot it would take on release. The renderer keeps it
    /// under the pointer until then, so the hand never loses the item it is carrying.
    /// </remarks>
    public static IReadOnlyList<double> PreviewCentres(
        IReadOnlyList<double> restingCentres,
        int draggedIndex,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(restingCentres);

        var count = restingCentres.Count;
        if (count == 0 || draggedIndex < 0 || draggedIndex >= count)
        {
            return restingCentres;
        }

        var order = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (i != draggedIndex)
            {
                order.Add(i);
            }
        }

        order.Insert(Math.Clamp(targetIndex, 0, order.Count), draggedIndex);

        var centres = new double[count];
        for (var place = 0; place < order.Count; place++)
        {
            centres[order[place]] = restingCentres[place];
        }

        return centres;
    }

    /// <summary>The same order as <see cref="PreviewCentres"/>, as the list of item indices the dock will hold.</summary>
    public static IReadOnlyList<int> OrderAfterDrop(int itemCount, int draggedIndex, int targetIndex)
    {
        if (itemCount <= 0 || draggedIndex < 0 || draggedIndex >= itemCount)
        {
            return [];
        }

        var order = new List<int>(itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            if (i != draggedIndex)
            {
                order.Add(i);
            }
        }

        order.Insert(Math.Clamp(targetIndex, 0, order.Count), draggedIndex);
        return order;
    }
}
