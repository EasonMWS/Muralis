using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Models;
using Xunit;

namespace Muralis.Core.Tests.Canvas;

public sealed class CanvasAnchorMathTests
{
    private static readonly PixelRect Bounds = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(CanvasAnchor.TopLeft, 0, 0)]
    [InlineData(CanvasAnchor.TopCenter, 960, 0)]
    [InlineData(CanvasAnchor.TopRight, 1920, 0)]
    [InlineData(CanvasAnchor.CenterLeft, 0, 540)]
    [InlineData(CanvasAnchor.Center, 960, 540)]
    [InlineData(CanvasAnchor.CenterRight, 1920, 540)]
    [InlineData(CanvasAnchor.BottomLeft, 0, 1080)]
    [InlineData(CanvasAnchor.BottomCenter, 960, 1080)]
    [InlineData(CanvasAnchor.BottomRight, 1920, 1080)]
    public void ResolvePoint_NamesTheDisplayPoint(CanvasAnchor anchor, double x, double y)
    {
        Assert.Equal((x, y), CanvasAnchorMath.ResolvePoint(anchor, Bounds));
    }

    [Fact]
    public void TopLeftAnchor_PlacesTheItemCornerAtTheOffset()
    {
        var item = Item(CanvasAnchor.TopLeft, 40, 40);

        var placed = CanvasAnchorMath.PlaceItem(item, Bounds, 1.0);

        Assert.Equal(new PixelRect(40, 40, 96, 96), placed);
    }

    [Fact]
    public void BottomRightAnchor_MeasuresInsideTheCorner()
    {
        var item = Item(CanvasAnchor.BottomRight, -40, -40);

        var placed = CanvasAnchorMath.PlaceItem(item, Bounds, 1.0);

        Assert.Equal(new PixelRect(1920 - 40 - 96, 1080 - 40 - 96, 96, 96), placed);
    }

    [Fact]
    public void CenterAnchor_PlacesTheItemCentreAtTheOffset()
    {
        var item = Item(CanvasAnchor.Center, 0, -220);

        var placed = CanvasAnchorMath.PlaceItem(item, Bounds, 1.0);

        Assert.Equal(new PixelRect(960 - 48, 540 - 220 - 48, 96, 96), placed);
        Assert.Equal((960.0, 320.0), CanvasAnchorMath.CenterOf(placed));
    }

    [Fact]
    public void DpiScale_AppliesToOffsetsAndSize()
    {
        var item = Item(CanvasAnchor.TopLeft, 100, 100);

        var placed = CanvasAnchorMath.PlaceItem(item, Bounds, 1.5);

        Assert.Equal(new PixelRect(150, 150, 144, 144), placed);
    }

    [Fact]
    public void OffsetsOffTheDisplay_ClampTheItemFullyInside()
    {
        var farAway = Item(CanvasAnchor.Center, -4000, -4000);
        var alsoFar = Item(CanvasAnchor.BottomRight, 500, 500);

        var clamped = CanvasAnchorMath.PlaceItem(farAway, Bounds, 1.0);
        var clampedToo = CanvasAnchorMath.PlaceItem(alsoFar, Bounds, 1.0);

        Assert.Equal(new PixelRect(0, 0, 96, 96), clamped);
        Assert.Equal(new PixelRect(1920 - 96, 1080 - 96, 96, 96), clampedToo);
    }

    [Fact]
    public void ItemLargerThanTheDisplay_StillAnchorsAtTheOrigin()
    {
        var huge = Item(CanvasAnchor.Center, 0, 0, sizeDip: 4000);

        var placed = CanvasAnchorMath.PlaceItem(huge, Bounds, 1.0);

        Assert.Equal(new PixelRect(0, 0, 4000, 4000), placed);
    }

    [Fact]
    public void TheSameLayout_KeepsItsRelativePlaceAcrossResolutions()
    {
        // The same DIP offsets mean "40 DIP inside the corner" on any display: on a bigger screen
        // the item simply keeps hugging the same corner instead of drifting to the middle.
        var item = Item(CanvasAnchor.BottomRight, -40, -40);
        var fullHd = new PixelRect(0, 0, 1920, 1080);
        var quadHd = new PixelRect(0, 0, 2560, 1440);

        var onFullHd = CanvasAnchorMath.PlaceItem(item, fullHd, 1.0);
        var onQuadHd = CanvasAnchorMath.PlaceItem(item, quadHd, 1.0);

        Assert.Equal(1920 - onFullHd.X, 2560 - onQuadHd.X);
        Assert.Equal(1080 - onFullHd.Y, 1440 - onQuadHd.Y);
    }

    [Fact]
    public void TheSameLayout_KeepsItsRelativePlaceAcrossDpi()
    {
        // 40 DIP stays 40 DIP: at 150% DPI the same logical inset is more physical pixels.
        var item = Item(CanvasAnchor.BottomRight, -40, -40);

        var at100 = CanvasAnchorMath.PlaceItem(item, Bounds, 1.0);
        var at150 = CanvasAnchorMath.PlaceItem(item, Bounds, 1.5);

        Assert.Equal(1920 - at100.X, (int)Math.Round((1920 - at150.X) / 1.5));
        Assert.Equal(96 * 1.5, at150.Width, precision: 9);
    }

    [Fact]
    public void AWorkAreaOffsetIsHonoured()
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);

        var placed = CanvasAnchorMath.PlaceItem(Item(CanvasAnchor.BottomLeft, 40, -40), workArea, 1.0);

        Assert.Equal(1040 - 40 - 96, placed.Y);
    }

    [Theory]
    [InlineData(CanvasAnchor.TopLeft, 40, 40, 1.0)]
    [InlineData(CanvasAnchor.Center, 0, -220, 1.0)]
    [InlineData(CanvasAnchor.BottomRight, -40, -40, 1.0)]
    [InlineData(CanvasAnchor.Center, 0, -220, 1.5)]
    [InlineData(CanvasAnchor.BottomLeft, 40, -40, 2.0)]
    public void OffsetForCenter_IsTheInverseOfPlaceItem(CanvasAnchor anchor, double offsetX, double offsetY, double scale)
    {
        // Offsets that need no clamping, so PlaceItem gives back exactly what went in.
        var item = Item(anchor, offsetX, offsetY);

        var placed = CanvasAnchorMath.PlaceItem(item, Bounds, scale);
        var (centerX, centerY) = CanvasAnchorMath.CenterOf(placed);
        var (recoveredX, recoveredY) = CanvasAnchorMath.OffsetForCenter(Bounds, scale, anchor, centerX, centerY, item.SizeDip);

        Assert.Equal(item.OffsetXDip, recoveredX, precision: 6);
        Assert.Equal(item.OffsetYDip, recoveredY, precision: 6);
    }

    [Theory]
    [InlineData(1.0, 700, 300)]
    [InlineData(1.5, 678, 402)]
    [InlineData(2.0, 520, 640)]
    public void ADroppedItem_ComesBackWhereItWasDropped(double scale, int x, int y)
    {
        // The drag path: the drop point is turned into anchor + DIP offsets, and those offsets are
        // what the layout file stores. Re-placing the item has to land it back under the pointer.
        var item = Item(CanvasAnchor.Center, 0, -220);
        var size = CanvasAnchorMath.PlaceItem(item, Bounds, scale).Width;
        var dropped = new PixelRect(x, y, size, size);

        var (centerX, centerY) = CanvasAnchorMath.CenterOf(dropped);
        var (offsetX, offsetY) = CanvasAnchorMath.OffsetForCenter(Bounds, scale, item.Anchor, centerX, centerY, item.SizeDip);
        item.OffsetXDip = offsetX;
        item.OffsetYDip = offsetY;

        Assert.Equal(dropped, CanvasAnchorMath.PlaceItem(item, Bounds, scale));
    }

    private static DesktopItem Item(CanvasAnchor anchor, double offsetX, double offsetY, double sizeDip = 96) => new()
    {
        Anchor = anchor,
        OffsetXDip = offsetX,
        OffsetYDip = offsetY,
        SizeDip = sizeDip,
        Target = new FolderTarget { Path = @"C:\muralis\anchor-math" },
    };
}
