using Muralis.Core.Models;
using Xunit;

namespace Muralis.Core.Tests.Models;

public sealed class WallpaperTagsTests
{
    [Theory]
    [InlineData("Nature", "Nature")]
    [InlineData("  Nature  ", "Nature")]
    [InlineData("wide  shot", "wide shot")]
    [InlineData("a,b", "a b")] // commas would corrupt the comma-separated column
    [InlineData("a;b\tc", "a b c")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalize_CleansRawInput(string? raw, string? expected)
    {
        Assert.Equal(expected, WallpaperTags.Normalize(raw));
    }

    [Fact]
    public void Normalize_CapsTheLength()
    {
        var normalized = WallpaperTags.Normalize(new string('x', 40) + " tail");

        Assert.Equal(WallpaperTags.MaxTagLength, normalized!.Length);
        Assert.Equal(new string('x', 32), normalized);
    }

    [Fact]
    public void Contains_IgnoresCasingAndSurroundingSpace()
    {
        var tags = new[] { "Nature", "4K" };

        Assert.True(WallpaperTags.Contains(tags, "nature"));
        Assert.True(WallpaperTags.Contains(tags, " 4k "));
        Assert.False(WallpaperTags.Contains(tags, "space"));
        Assert.False(WallpaperTags.Contains(tags, null));
    }

    [Fact]
    public void Remove_MatchesRegardlessOfCasing()
    {
        var tags = new List<string> { "Nature", "4K" };

        Assert.True(WallpaperTags.Remove(tags, "4k"));
        Assert.Equal(["Nature"], tags);
        Assert.False(WallpaperTags.Remove(tags, "4k"));
    }
}
