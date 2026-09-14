using Muralis.Core.Helpers;
using Xunit;

namespace Muralis.Core.Tests.Helpers;

public sealed class DisplayFormatTests
{
    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(2560, 1440, "16:9")]
    [InlineData(3440, 1440, "43:18")]
    [InlineData(1080, 1920, "9:16")]
    public void AspectRatio_ReturnsReducedRatio(int width, int height, string expected) =>
        Assert.Equal(expected, DisplayFormat.AspectRatio(width, height));

    [Fact]
    public void AspectRatio_WithUnusualRatio_FallsBackToDecimal() =>
        Assert.Equal("2.37:1", DisplayFormat.AspectRatio(2560, 1080));

    [Fact]
    public void AspectRatio_WithInvalidDimensions_ReturnsEmpty() =>
        Assert.Equal(string.Empty, DisplayFormat.AspectRatio(0, 1080));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void FileSize_FormatsWithUnit(long bytes, string expected) =>
        Assert.Equal(expected, DisplayFormat.FileSize(bytes));

    [Fact]
    public void Resolution_WithInvalidDimensions_ReturnsFallback() =>
        Assert.Equal("Unknown resolution", DisplayFormat.Resolution(0, 0));

    [Fact]
    public void RelativeTime_FormatsRecentValues()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("just now", DisplayFormat.RelativeTime(now.AddSeconds(-20), now));
        Assert.Equal("5 min ago", DisplayFormat.RelativeTime(now.AddMinutes(-5), now));
        Assert.Equal("3 h ago", DisplayFormat.RelativeTime(now.AddHours(-3), now));
        Assert.Equal("2 d ago", DisplayFormat.RelativeTime(now.AddDays(-2), now));
    }
}
