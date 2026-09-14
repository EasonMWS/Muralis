using Muralis.Core.Helpers;
using Xunit;

namespace Muralis.Core.Tests.Helpers;

public sealed class FileNameHelperTests
{
    [Theory]
    [InlineData(@"C:\pics\blue_ridge-4k.jpg", "Blue Ridge 4k")]
    [InlineData("img14.jpg", "Img 14")]
    [InlineData("sunset over the sea.PNG", "Sunset Over The Sea")]
    [InlineData("2026-09-14_wallpaper.png", "2026 09 14 Wallpaper")]
    public void ToTitle_ProducesFriendlyNames(string path, string expected) =>
        Assert.Equal(expected, FileNameHelper.ToTitle(path));

    [Fact]
    public void ToTitle_WithEmptyName_ReturnsFallback() =>
        Assert.Equal("Untitled", FileNameHelper.ToTitle(string.Empty));
}
