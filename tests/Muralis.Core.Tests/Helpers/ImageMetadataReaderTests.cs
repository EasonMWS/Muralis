using Muralis.Core.Helpers;
using Xunit;

namespace Muralis.Core.Tests.Helpers;

public sealed class ImageMetadataReaderTests
{
    [Fact]
    public void TryReadDimensions_ParsesPngHeader()
    {
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D,
            (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0x00, 0x00, 0x0F, 0x00,
            0x00, 0x00, 0x08, 0x70,
        ];

        var success = ImageMetadataReader.TryReadDimensions(new MemoryStream(png), out var width, out var height);

        Assert.True(success);
        Assert.Equal(3840, width);
        Assert.Equal(2160, height);
    }

    [Fact]
    public void TryReadDimensions_ParsesJpegHeader()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            0x04, 0x38,
            0x0F, 0x00,
        ];

        var success = ImageMetadataReader.TryReadDimensions(new MemoryStream(jpeg), out var width, out var height);

        Assert.True(success);
        Assert.Equal(3840, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void TryReadDimensions_ParsesBmpHeader()
    {
        var bmp = new byte[26];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(1920).CopyTo(bmp, 18);
        BitConverter.GetBytes(1080).CopyTo(bmp, 22);

        var success = ImageMetadataReader.TryReadDimensions(new MemoryStream(bmp), out var width, out var height);

        Assert.True(success);
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void TryReadDimensions_WithGarbage_ReturnsFalse()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        Assert.False(ImageMetadataReader.TryReadDimensions(new MemoryStream(garbage), out _, out _));
    }

    [Fact]
    public void TryReadDimensions_WithMissingFile_ReturnsFalse() =>
        Assert.False(ImageMetadataReader.TryReadDimensions(@"C:\definitely\missing\file.png", out _, out _));
}
