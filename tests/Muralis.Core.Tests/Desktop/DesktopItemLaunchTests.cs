using Muralis.Core.Desktop;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The whole routing decision behind a launch, with nothing started: every kind hands the shell its
/// own location and the "open" verb, and an item with nothing to open hands over nothing.
/// </summary>
public sealed class DesktopItemLaunchTests
{
    public static IEnumerable<object[]> EveryKind()
    {
        yield return [new ApplicationTarget { Path = @"C:\apps\editor.exe" }, @"C:\apps\editor.exe"];
        yield return [new ShortcutTarget { Path = @"C:\Users\me\Desktop\editor.lnk" }, @"C:\Users\me\Desktop\editor.lnk"];
        yield return [new FileTarget { Path = @"C:\docs\notes.pdf" }, @"C:\docs\notes.pdf"];
        yield return [new FolderTarget { Path = @"C:\pictures" }, @"C:\pictures"];
        yield return [new UrlTarget { Url = "https://example.com" }, "https://example.com"];
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void EveryKind_OpensItsOwnLocation(DesktopItemTarget target, string location)
    {
        // No command line is ever assembled from a target: the shell gets a location and the verb a
        // double-click would use, and it decides what that means.
        var request = DesktopItemLaunch.Plan(Item(target));

        Assert.NotNull(request);
        Assert.Equal(location, request.File);
        Assert.Equal(DesktopItemLaunch.OpenVerb, request.Verb);
    }

    [Fact]
    public void AnEmptyLocation_PlansNothing()
    {
        Assert.Null(DesktopItemLaunch.Plan(Item(new UrlTarget { Url = " " })));
    }

    [Fact]
    public void AnItemWithoutATarget_PlansNothing()
    {
        var item = Item(new FolderTarget { Path = @"C:\pictures" });
        item.Target = null!;

        Assert.Null(DesktopItemLaunch.Plan(item));
    }

    private static DesktopItem Item(DesktopItemTarget target) => new()
    {
        Id = "item_1",
        Name = "item",
        Target = target,
    };
}
