using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The item model: what validation refuses, how a missing target is reported per kind, and that a
/// clone cannot reach back into the original.
/// </summary>
public sealed class DesktopItemTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("desktop-item");

    public void Dispose() => _workspace.Dispose();

    public static IEnumerable<object[]> EveryKind()
    {
        yield return [new ApplicationTarget { Path = @"C:\apps\editor.exe" }];
        yield return [new ShortcutTarget { Path = @"C:\Users\me\Desktop\editor.lnk" }];
        yield return [new FileTarget { Path = @"C:\docs\notes.pdf" }];
        yield return [new FolderTarget { Path = @"C:\pictures" }];
        yield return [new UrlTarget { Url = "https://example.com/watch?v=1" }];
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void AWellFormedItem_ValidatesForEveryKind(DesktopItemTarget target)
    {
        var item = Item(target);

        Assert.Empty(item.Validate());
    }

    [Fact]
    public void AnItemWithoutAnId_IsRejected()
    {
        var item = Item(new FolderTarget { Path = @"C:\pictures" });
        item.Id = "  ";

        Assert.Contains(item.Validate(), problem => problem.Contains("non-empty id", StringComparison.Ordinal));
    }

    [Fact]
    public void AnItemWithoutAName_IsRejected()
    {
        var item = Item(new FolderTarget { Path = @"C:\pictures" });
        item.Name = "";

        Assert.Contains(item.Validate(), problem => problem.Contains("needs a name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(513)]
    public void AnItemWithAnImpossibleSize_IsRejected(double sizeDip)
    {
        var item = Item(new FolderTarget { Path = @"C:\pictures" });
        item.SizeDip = sizeDip;

        Assert.Contains(item.Validate(), problem => problem.Contains("size", StringComparison.Ordinal));
    }

    [Fact]
    public void AnItemWithANonFiniteOffset_IsRejected()
    {
        var item = Item(new FolderTarget { Path = @"C:\pictures" });
        item.OffsetXDip = double.NaN;
        item.OffsetYDip = double.PositiveInfinity;

        Assert.Contains(item.Validate(), problem => problem.Contains("non-finite offset", StringComparison.Ordinal));
    }

    [Fact]
    public void AnItemWithoutATarget_IsRejected()
    {
        var item = Item(new FolderTarget { Path = @"C:\pictures" });
        item.Target = null!;

        Assert.Contains(item.Validate(), problem => problem.Contains("needs a target", StringComparison.Ordinal));
    }

    [Fact]
    public void ARelativeTargetPath_IsRejected()
    {
        // Positions are absolute and so are targets: a bare file name would resolve against whatever
        // directory the app happened to start in.
        var item = Item(new FileTarget { Path = "notes.pdf" });

        Assert.Contains(item.Validate(), problem => problem.Contains("fully qualified", StringComparison.Ordinal));
    }

    [Fact]
    public void ATargetPathWithInvalidCharacters_IsRejected()
    {
        var item = Item(new FileTarget { Path = _workspace.PathOf("bad\0name.pdf") });

        Assert.Contains(item.Validate(), problem => problem.Contains("invalid characters", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyTargetLocation_IsRejected()
    {
        var item = Item(new ShortcutTarget { Path = " " });

        Assert.Contains(item.Validate(), problem => problem.Contains("empty target path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ftp://files.example.com/tool.exe")]
    [InlineData("mailto:someone@example.com")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("muralis://open")]
    public void AnAddressThatIsntHttp_IsRejected(string address)
    {
        // Anything with a registered protocol handler would otherwise turn a link into a way to start
        // a program, so only the two web schemes are representable.
        var item = Item(new UrlTarget { Url = address });

        Assert.Contains(item.Validate(), problem => problem.Contains("http and https", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyAddress_IsRejected()
    {
        var item = Item(new UrlTarget { Url = string.Empty });

        Assert.Contains(item.Validate(), problem => problem.Contains("empty address", StringComparison.Ordinal));
    }

    [Fact]
    public void IsMissing_SaysNoWhileTheFileIsStillThere()
    {
        var file = _workspace.FileAt("tool.exe");
        var item = Item(new ApplicationTarget { Path = file });

        Assert.False(item.IsMissing());
    }

    [Fact]
    public void IsMissing_SaysYesWhenTheFileIsGone()
    {
        // The user moved or uninstalled it: the item is marked, never removed.
        var file = _workspace.FileAt("tool.exe");
        var item = Item(new ApplicationTarget { Path = file });

        File.Delete(file);

        Assert.True(item.IsMissing());
    }

    [Fact]
    public void IsMissing_ProbesAFolderWithDirectoryExists()
    {
        var folder = _workspace.DirectoryAt("Pictures");
        var item = Item(new FolderTarget { Path = folder });
        Assert.False(item.IsMissing());

        Directory.Delete(folder);

        Assert.True(item.IsMissing());
    }

    [Fact]
    public void IsMissing_SeesAShortcutTheSameWayAsAFile()
    {
        var link = _workspace.FileAt("editor.lnk");
        var item = Item(new ShortcutTarget { Path = link });
        Assert.False(item.IsMissing());

        File.Delete(link);

        Assert.True(item.IsMissing());
    }

    [Fact]
    public void IsMissing_TreatsAnAddressAsAlwaysThere()
    {
        var item = Item(new UrlTarget { Url = "https://example.com" });

        Assert.False(item.IsMissing());
    }

    [Fact]
    public void IsMissing_ReportsAnItemThatHasNoTargetAtAll()
    {
        var item = Item(new FolderTarget { Path = @"C:\pictures" });
        item.Target = null!;

        Assert.True(item.IsMissing());
        Assert.Equal(string.Empty, item.Location);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var item = Item(new ShortcutTarget { Path = @"C:\links\editor.lnk" });
        item.OffsetXDip = -195;

        var clone = item.Clone();
        clone.Name = "renamed";
        clone.OffsetXDip = 999;
        ((ShortcutTarget)clone.Target).Path = @"C:\links\other.lnk";

        Assert.Equal("editor", item.Name);
        Assert.Equal(-195, item.OffsetXDip);
        Assert.Equal(@"C:\links\editor.lnk", item.Location);
        Assert.NotSame(item.Target, clone.Target);
    }

    private static DesktopItem Item(DesktopItemTarget target) => new()
    {
        Id = "item_1",
        Name = "editor",
        Target = target,
    };
}
