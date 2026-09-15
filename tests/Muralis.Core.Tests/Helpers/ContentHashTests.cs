using Muralis.Core.Helpers;
using Xunit;

namespace Muralis.Core.Tests.Helpers;

public sealed class ContentHashTests : IDisposable
{
    private readonly string _directory;

    public ContentHashTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-hash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Compute_ReturnsTheKnownSha256OfTheContent()
    {
        var path = Path.Combine(_directory, "hello.txt");
        await File.WriteAllTextAsync(path, "hello");

        var hash = await ContentHash.TryComputeAsync(path);

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    [Fact]
    public async Task Compute_ReturnsTheSameHashForCopiesOfTheFile()
    {
        var first = Path.Combine(_directory, "first.bin");
        await File.WriteAllBytesAsync(first, [1, 2, 3, 4, 5]);
        var second = Path.Combine(_directory, "second.bin");
        File.Copy(first, second);

        Assert.Equal(await ContentHash.TryComputeAsync(first), await ContentHash.TryComputeAsync(second));
    }

    [Fact]
    public async Task Compute_ReturnsNullForAMissingFile()
    {
        Assert.Null(await ContentHash.TryComputeAsync(Path.Combine(_directory, "missing.png")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }
}
