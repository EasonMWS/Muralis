using Muralis.Core.Dock;
using Xunit;

namespace Muralis.Core.Tests.Dock;

/// <summary>
/// Where a dragged item would land and how the rest of the dock makes room. Nothing here changes the
/// document: the new order is only written when the item is really dropped.
/// </summary>
public sealed class DockReorderTests
{
    // Four slots, 68 DIP apart.
    private static readonly double[] Slots = [370, 438, 506, 574];

    [Fact]
    public void AnItemDroppedWhereItStarted_KeepsItsPlace()
    {
        for (var index = 0; index < Slots.Length; index++)
        {
            Assert.Equal(index, DockReorder.TargetIndex(Slots[index], Slots, index));
        }
    }

    [Fact]
    public void AnItemCarriedPastItsNeighbour_TakesThatNeighboursPlace()
    {
        Assert.Equal(1, DockReorder.TargetIndex(440, Slots, draggedIndex: 0));
        Assert.Equal(2, DockReorder.TargetIndex(510, Slots, draggedIndex: 0));
        Assert.Equal(3, DockReorder.TargetIndex(600, Slots, draggedIndex: 0));
    }

    [Fact]
    public void AnItemCarriedBackwards_LandsBeforeTheOnesItPassed()
    {
        Assert.Equal(0, DockReorder.TargetIndex(300, Slots, draggedIndex: 3));
        Assert.Equal(1, DockReorder.TargetIndex(400, Slots, draggedIndex: 3));
        Assert.Equal(2, DockReorder.TargetIndex(490, Slots, draggedIndex: 3));
    }

    [Fact]
    public void TheTargetIndex_NeverGoesBackwardsWhileTheItemIsCarriedForwards()
    {
        var previous = 0;
        for (var along = 0.0; along <= 700; along += 10)
        {
            var target = DockReorder.TargetIndex(along, Slots, draggedIndex: 1);

            Assert.True(target >= previous, $"the target went from {previous} back to {target} at {along}");
            previous = target;
        }
    }

    [Fact]
    public void TheTargetIndex_StaysInsideTheDockHoweverFarTheItemIsCarried()
    {
        Assert.Equal(0, DockReorder.TargetIndex(-5000, Slots, draggedIndex: 2));
        Assert.Equal(3, DockReorder.TargetIndex(5000, Slots, draggedIndex: 2));
    }

    [Fact]
    public void ThePreview_OpensAGapWhereTheItemWouldLand()
    {
        // Carrying item 0 to the last place: everything else moves up one slot.
        var preview = DockReorder.PreviewCentres(Slots, draggedIndex: 0, targetIndex: 3);

        Assert.Equal(Slots[3], preview[0]);
        Assert.Equal(Slots[0], preview[1]);
        Assert.Equal(Slots[1], preview[2]);
        Assert.Equal(Slots[2], preview[3]);
    }

    [Fact]
    public void ThePreview_LeavesTheOrderAloneWhenNothingWouldMove()
    {
        var preview = DockReorder.PreviewCentres(Slots, draggedIndex: 2, targetIndex: 2);

        Assert.Equal(Slots, preview);
    }

    [Fact]
    public void ThePreview_MovesOnlyTheItemsBetweenTheOldAndNewPlaces()
    {
        // Carrying item 1 back to the front: items 0 moves up, item 1 takes the first slot, and the
        // items after it do not move at all.
        var preview = DockReorder.PreviewCentres(Slots, draggedIndex: 1, targetIndex: 0);

        Assert.Equal(Slots[1], preview[0]);
        Assert.Equal(Slots[0], preview[1]);
        Assert.Equal(Slots[2], preview[2]);
        Assert.Equal(Slots[3], preview[3]);
    }

    [Fact]
    public void TheOrderAfterADrop_IsThePermutationThePreviewDescribes()
    {
        var order = DockReorder.OrderAfterDrop(4, draggedIndex: 0, targetIndex: 3);

        Assert.Equal([1, 2, 3, 0], order);
    }

    [Fact]
    public void TheOrderAfterADrop_IsAlwaysAPermutationOfEveryItem()
    {
        for (var dragged = 0; dragged < 5; dragged++)
        {
            for (var target = 0; target < 5; target++)
            {
                var order = DockReorder.OrderAfterDrop(5, dragged, target);

                Assert.Equal(5, order.Count);
                Assert.Equal([0, 1, 2, 3, 4], order.OrderBy(index => index));
            }
        }
    }

    [Fact]
    public void AnEmptyDock_HasNothingToReorder()
    {
        Assert.Empty(DockReorder.PreviewCentres([], 0, 0));
        Assert.Empty(DockReorder.OrderAfterDrop(0, 0, 0));
    }

    [Fact]
    public void AnIndexThatIsNotInTheDock_IsLeftAlone()
    {
        Assert.Equal(Slots, DockReorder.PreviewCentres(Slots, draggedIndex: 9, targetIndex: 1));
    }
}
