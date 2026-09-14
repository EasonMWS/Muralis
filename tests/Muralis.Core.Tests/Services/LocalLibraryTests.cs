using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Services;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class LocalLibraryTests : IDisposable
{
    private readonly string _directory;
    private readonly LocalLibrary _library = new(NullLogger<LocalLibrary>.Instance);

    public LocalLibraryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-library-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Import_AddsSupportedImagesWithMetadata()
    {
        var path = CreateImage("wide shot-1.png", 1920, 1080);

        var result = await _library.ImportAsync([path]);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Failed);
        var item = Assert.Single(_library.Items);
        Assert.Equal("Wide Shot 1", item.Title);
        Assert.Equal(1920, item.Width);
        Assert.Equal(1080, item.Height);
        Assert.True(item.FileSize > 0);
    }

    [Fact]
    public async Task Import_SkipsDuplicates()
    {
        var path = CreateImage("one.png");

        await _library.ImportAsync([path]);
        var second = await _library.ImportAsync([path]);

        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Duplicates);
        Assert.Single(_library.Items);
    }

    [Fact]
    public async Task Import_CountsUnsupportedFilesAsFailed()
    {
        var textFile = Path.Combine(_directory, "notes.txt");
        await File.WriteAllTextAsync(textFile, "not an image");

        var result = await _library.ImportAsync([textFile, Path.Combine(_directory, "missing.png")]);

        Assert.Equal(0, result.Added);
        Assert.Equal(2, result.Failed);
        Assert.Empty(_library.Items);
    }

    [Fact]
    public async Task Remove_DropsRecordButKeepsFileOnDisk()
    {
        var path = CreateImage("keep-me.png");
        await _library.ImportAsync([path]);
        var id = _library.Items[0].Id;

        var removed = _library.Remove(id);

        Assert.True(removed);
        Assert.Empty(_library.Items);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task MarkUsed_UpdatesLastUsedAt()
    {
        var path = CreateImage("used.png");
        await _library.ImportAsync([path]);
        var when = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        _library.MarkUsed(_library.Items[0].Id, when);

        Assert.Equal(when, _library.Items[0].LastUsedAt);
    }

    [Fact]
    public async Task Import_RaisesChangedOnlyWhenSomethingWasAdded()
    {
        var path = CreateImage("event.png");
        var raised = 0;
        _library.Changed += (_, _) => raised++;

        await _library.ImportAsync([path]);
        await _library.ImportAsync([path]);

        Assert.Equal(1, raised);
    }

    private string CreateImage(string name, int width = 320, int height = 200)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, CreatePngHeader(width, height));
        return path;
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        var bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        bytes[11] = 0x0D; // IHDR length 13
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        return bytes;
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
