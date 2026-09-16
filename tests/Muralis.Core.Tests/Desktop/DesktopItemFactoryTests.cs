using Muralis.Core.Desktop;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// What the import entry produces from what the user picked: the kind follows the path, the name is
/// the one Windows shows, and the item only ever references the original — nothing is copied, moved
/// or scanned.
/// </summary>
public sealed class DesktopItemFactoryTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("desktop-item-factory");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void AnExecutable_BecomesAnApplicationItem()
    {
        var path = _workspace.FileAt("My Editor.exe");

        var item = DesktopItemFactory.CreateFromPath(path);

        Assert.IsType<ApplicationTarget>(item.Target);
        Assert.Equal(DesktopItemKind.Application, item.Target.Kind);
        Assert.Equal(path, item.Location);
        Assert.Empty(item.Validate());
    }

    [Fact]
    public void AShortcut_BecomesAShortcutItem()
    {
        _workspace.FileAt("My Editor.exe");
        var path = _workspace.FileAt("My Editor.lnk");

        var item = DesktopItemFactory.CreateFromPath(path);

        Assert.IsType<ShortcutTarget>(item.Target);
        Assert.Equal("My Editor", item.Name);
    }

    [Fact]
    public void AFolder_BecomesAFolderItem()
    {
        var path = _workspace.DirectoryAt("My Pictures");

        var item = DesktopItemFactory.CreateFromPath(path);

        Assert.IsType<FolderTarget>(item.Target);
        Assert.Equal("My Pictures", item.Name);
        Assert.Equal("folder", item.IconKey);
    }

    [Fact]
    public void AnyOtherFile_BecomesAFileItem()
    {
        var path = _workspace.FileAt("Notes.pdf");

        var item = DesktopItemFactory.CreateFromPath(path);

        Assert.IsType<FileTarget>(item.Target);
        Assert.Equal("Notes", item.Name);
    }

    [Fact]
    public void AnItemOnlyReferencesWhatWasPicked()
    {
        var path = _workspace.FileAt("My Editor.exe");

        var item = DesktopItemFactory.CreateFromPath(path);

        Assert.Equal(path, item.Location);
        Assert.Single(Directory.GetFileSystemEntries(_workspace.Root));
    }

    [Fact]
    public void EveryItemGetsItsOwnId()
    {
        var first = DesktopItemFactory.CreateFromPath(_workspace.FileAt("editor.exe"));
        var second = DesktopItemFactory.CreateFromPath(_workspace.PathOf("editor.exe"));

        Assert.NotEqual(first.Id, second.Id);
        Assert.StartsWith("app_", first.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdPrefix_SaysWhatTheItemIs()
    {
        Assert.StartsWith("lnk_", DesktopItemFactory.CreateFromPath(_workspace.FileAt("a.lnk")).Id, StringComparison.Ordinal);
        Assert.StartsWith("dir_", DesktopItemFactory.CreateFromPath(_workspace.DirectoryAt("a-folder")).Id, StringComparison.Ordinal);
        Assert.StartsWith("file_", DesktopItemFactory.CreateFromPath(_workspace.FileAt("a.txt")).Id, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPath_IsRefused(string path)
    {
        Assert.Throws<ArgumentException>(() => DesktopItemFactory.CreateFromPath(path));
    }

    [Fact]
    public void AnAddress_BecomesAUrlItem()
    {
        var item = DesktopItemFactory.CreateFromUrl("https://example.com/videos?id=7");

        Assert.IsType<UrlTarget>(item.Target);
        Assert.Equal("https://example.com/videos?id=7", item.Location);
        Assert.Equal("example.com", item.Name);
        Assert.Equal("url", item.IconKey);
        Assert.StartsWith("url_", item.Id, StringComparison.Ordinal);
        Assert.Empty(item.Validate());
    }

    [Fact]
    public void ABareAddress_GetsHttps()
    {
        var item = DesktopItemFactory.CreateFromUrl("example.com");

        Assert.Equal("https://example.com", item.Location);
        Assert.Equal("example.com", item.Name);
        Assert.Empty(item.Validate());
    }

    [Fact]
    public void AnAddressIsTrimmed()
    {
        var item = DesktopItemFactory.CreateFromUrl("  https://example.com  ");

        Assert.Equal("https://example.com", item.Location);
    }

    [Theory]
    [InlineData("ftp://files.example.com/tools")]
    [InlineData("mailto:someone@example.com")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("muralis://open")]
    public void AnythingThatIsNotAWebAddress_IsRefused(string address)
    {
        // Only http and https may be opened: the shell has a protocol handler for much more than
        // that, and none of it belongs behind a desktop item the user did not pick.
        Assert.Throws<ArgumentException>(() => DesktopItemFactory.CreateFromUrl(address));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyAddress_IsRefused(string address)
    {
        Assert.Throws<ArgumentException>(() => DesktopItemFactory.CreateFromUrl(address));
    }
}
