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

    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var sanitized = FileNameHelper.SanitizeFileName("a/b:c*d?e");

        Assert.DoesNotContain('/', sanitized);
        Assert.DoesNotContain(':', sanitized);
        Assert.DoesNotContain('?', sanitized);
        Assert.DoesNotContain('*', sanitized);
    }

    [Fact]
    public void SanitizeFileName_WithOnlyInvalidCharacters_ReturnsFallback() =>
        Assert.Equal("wallpaper", FileNameHelper.SanitizeFileName("???"));

    [Fact]
    public void EnsureUniqueFilePath_WhenFileExists_AppendsNumericSuffix()
    {
        var directory = Path.Combine(Path.GetTempPath(), "muralis-name-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = Path.Combine(directory, "sunset.jpg");
            File.WriteAllText(original, "x");

            var second = FileNameHelper.EnsureUniqueFilePath(original);
            File.WriteAllText(second, "x");
            var third = FileNameHelper.EnsureUniqueFilePath(original);

            Assert.Equal("sunset (2).jpg", Path.GetFileName(second));
            Assert.Equal("sunset (3).jpg", Path.GetFileName(third));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
