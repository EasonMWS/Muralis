using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// Where a newly added item lands: closest to the middle of the display that is still free. The
/// answer is an anchor offset in DIP, so the choice survives a change of resolution or DPI.
/// </summary>
public sealed class DesktopItemPlacerTests
{
    private const double DisplayWidth = 2560;
    private const double DisplayHeight = 1440;
    private const double SizeDip = 96;

    [Fact]
    public void AnEmptyDisplay_ReceivesTheFirstItemInTheMiddle()
    {
        var spot = DesktopItemPlacer.NextFreeSpot([], null, DisplayWidth, DisplayHeight, SizeDip);

        Assert.Equal((0.0, 0.0), spot);
    }

    [Fact]
    public void AnItemInTheMiddle_PushesTheNextOneAside()
    {
        var spot = DesktopItemPlacer.NextFreeSpot([Free(0, 0)], null, DisplayWidth, DisplayHeight, SizeDip);

        Assert.Equal((0.0, -DesktopItemPlacer.SpacingDip), spot);
    }

    [Fact]
    public void AnItemTheDockShows_DoesNotOccupyASpot()
    {
        // A docked item is not on this canvas at all, so the middle of the display is still free.
        var docked = Free(0, 0);

        var spot = DesktopItemPlacer.NextFreeSpot([docked], [docked.Id], DisplayWidth, DisplayHeight, SizeDip);

        Assert.Equal((0.0, 0.0), spot);
    }

    [Fact]
    public void ADisplayWithNoRoom_StillGivesAUsableOffset()
    {
        var spot = DesktopItemPlacer.NextFreeSpot([Free(0, 0)], null, 100, 100, 96);

        Assert.Equal((0.0, 0.0), spot);
    }

    [Fact]
    public void ItemsAreSpacedOut()
    {
        // The whole point of the placer: adding one item after another never stacks them.
        var items = new List<DesktopItem>();
        for (var index = 0; index < 5; index++)
        {
            var (x, y) = DesktopItemPlacer.NextFreeSpot(items, null, DisplayWidth, DisplayHeight, SizeDip);
            items.Add(Free(x, y));
        }

        Assert.Equal(5, items.Select(item => (item.OffsetXDip, item.OffsetYDip)).Distinct().Count());
        foreach (var first in items)
        {
            foreach (var second in items)
            {
                if (ReferenceEquals(first, second))
                {
                    continue;
                }

                var farEnoughApart = Math.Abs(first.OffsetXDip - second.OffsetXDip) >= DesktopItemPlacer.SpacingDip * 0.6
                    || Math.Abs(first.OffsetYDip - second.OffsetYDip) >= DesktopItemPlacer.SpacingDip * 0.6;
                Assert.True(farEnoughApart, $"'{first.Id}' and '{second.Id}' were placed on top of each other");
            }
        }
    }

    private static DesktopItem Free(double offsetX, double offsetY) => new()
    {
        Id = $"item_{offsetX}_{offsetY}",
        Name = "item",
        Anchor = CanvasAnchor.Center,
        OffsetXDip = offsetX,
        OffsetYDip = offsetY,
        SizeDip = SizeDip,
        Target = new FolderTarget { Path = @"C:\pictures" },
    };
}
