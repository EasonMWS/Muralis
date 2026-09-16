using Muralis.Desktop.Interop;
using Muralis.Desktop.Icons;
using Xunit;

namespace Muralis.Desktop.Tests.Icons;

/// <summary>
/// Covers the two things the reader decides without the shell: which image list an icon of a given
/// drawn size should come from, and the shape GDI's straight BGRA is turned into before it can be
/// uploaded — premultiplied, with an icon that has no alpha channel of its own declared opaque.
/// </summary>
public sealed class ShellIconReaderTests
{
    [Theory]
    [InlineData(1, NativeMethods.ShilSmall)]
    [InlineData(16, NativeMethods.ShilSmall)]
    [InlineData(17, NativeMethods.ShilLarge)]
    [InlineData(20, NativeMethods.ShilLarge)]
    [InlineData(21, NativeMethods.ShilExtraLarge)]
    [InlineData(32, NativeMethods.ShilExtraLarge)]
    [InlineData(44, NativeMethods.ShilExtraLarge)]
    [InlineData(45, NativeMethods.ShilJumbo)]
    [InlineData(96, NativeMethods.ShilJumbo)]
    [InlineData(256, NativeMethods.ShilJumbo)]
    public void ImageListFor_BucketsADrawnSizeIntoTheShellsLists(int wantedPixels, int expected)
    {
        Assert.Equal(expected, ShellIconReader.ImageListFor(wantedPixels));
    }

    [Fact]
    public void ToPremultipliedBgra_ScalesColourByAlpha()
    {
        // The surface blends in premultiplied space, so a half-transparent pixel's colour is scaled
        // here; leaving it straight would darken the icon once the engine blends it.
        var pixels = new byte[] { 200, 100, 50, 128 };

        ShellIconReader.ToPremultipliedBgra(pixels, hasAlphaChannel: true);

        Assert.Equal([100, 50, 25, 128], pixels);
    }

    [Fact]
    public void ToPremultipliedBgra_ClearsFullyTransparentPixels()
    {
        // Premultiplying is not optional for these: a transparent pixel keeping its colour shows up
        // as a bright ghost wherever the engine adds the colour back in. The second pixel is what
        // tells the array it really has an alpha channel at all.
        var pixels = new byte[] { 200, 100, 50, 0, 200, 100, 50, 128 };

        ShellIconReader.ToPremultipliedBgra(pixels, hasAlphaChannel: true);

        Assert.Equal([0, 0, 0, 0, 100, 50, 25, 128], pixels);
    }

    [Fact]
    public void ToPremultipliedBgra_LeavesOpaquePixelsUntouched()
    {
        var pixels = new byte[] { 200, 100, 50, 255 };

        ShellIconReader.ToPremultipliedBgra(pixels, hasAlphaChannel: true);

        Assert.Equal([200, 100, 50, 255], pixels);
    }

    [Fact]
    public void ToPremultipliedBgra_TreatsAnAllZeroAlphaChannelAsOpaque()
    {
        // A 32bpp icon from the shell often carries no alpha channel at all, and read literally
        // every pixel of it would be invisible.
        var pixels = new byte[] { 200, 100, 50, 0, 10, 20, 30, 0 };

        ShellIconReader.ToPremultipliedBgra(pixels, hasAlphaChannel: true);

        Assert.Equal([200, 100, 50, 255, 10, 20, 30, 255], pixels);
    }

    [Fact]
    public void ToPremultipliedBgra_WithNoAlphaChannelForcesOpaque()
    {
        var pixels = new byte[] { 200, 100, 50, 0, 10, 20, 30, 0 };

        ShellIconReader.ToPremultipliedBgra(pixels, hasAlphaChannel: false);

        Assert.Equal([200, 100, 50, 255, 10, 20, 30, 255], pixels);
    }

    [Fact]
    public void Read_OfATargetThatIsGone_GivesNothing()
    {
        // Answered before any shell call, so a target the user deleted or moved cannot come back
        // as the shell's generic "unknown file" picture.
        var missing = Path.Combine(Path.GetTempPath(), $"muralis-missing-{Guid.NewGuid():N}.lnk");

        Assert.Null(ShellIconReader.Read(missing, 96));
        Assert.Null(ShellIconReader.Read("  ", 96));
    }
}
