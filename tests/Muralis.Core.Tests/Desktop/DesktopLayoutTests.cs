using System.Text.Json;
using System.Text.Json.Serialization;
using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Dock;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The desktop layout document: it starts empty, it round-trips, and the parameter validation the
/// prototype already had still holds for version 3.
/// </summary>
public sealed class DesktopLayoutTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void AFreshLayout_IsEmptyAndValid()
    {
        var layout = DesktopLayout.CreateEmpty();

        Assert.Empty(layout.Validate());
        Assert.Empty(layout.Items);
        Assert.Equal(DesktopLayout.CurrentSchemaVersion, layout.SchemaVersion);
        Assert.Equal(DesktopLayout.DocumentKind, layout.Kind);
    }

    [Fact]
    public void Layout_RoundTripsThroughJson()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Proximity.MaxScale = 1.75;
        layout.Dock.Enabled = true;
        layout.Dock.Edge = DockEdge.Right;
        layout.Dock.Entries.Add(new DockEntry { ItemId = "lnk_1" });
        layout.Items.Add(new DesktopItem
        {
            Id = "lnk_1",
            Name = "Editor",
            Target = new ShortcutTarget { Path = @"C:\Users\me\Desktop\editor.lnk" },
            Anchor = CanvasAnchor.BottomRight,
            OffsetXDip = -40,
            OffsetYDip = -40,
        });
        layout.Items.Add(new DesktopItem
        {
            Id = "url_1",
            Name = "example.com",
            Target = new UrlTarget { Url = "https://example.com" },
            IconKey = "url",
        });

        var json = JsonSerializer.Serialize(layout, JsonOptions);
        var restored = JsonSerializer.Deserialize<DesktopLayout>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Contains("\"muralis.desktopLayout\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"url\"", json, StringComparison.Ordinal);
        Assert.Empty(restored.Validate());
        Assert.Equal(2, restored.Items.Count);
        Assert.IsType<ShortcutTarget>(restored.Items[0].Target);
        Assert.Equal(-40, restored.Items[0].OffsetXDip);
        Assert.Equal("https://example.com", restored.Items[1].Location);
        Assert.Equal(1.75, restored.Proximity.MaxScale);
        Assert.Equal(DockEdge.Right, restored.Dock.Edge);
        Assert.Equal("lnk_1", restored.Dock.Entries[0].ItemId);
        Assert.Equal("\"Smoothstep\"", JsonSerializer.Serialize(new CanvasProximityOptions().Falloff, JsonOptions));
    }

    [Fact]
    public void AHandEditedDocument_MayOmitAnythingButTheItems()
    {
        // The document is meant to be edited by hand: omitted parameters keep their defaults instead
        // of failing the load.
        const string json = """
            {
              "SchemaVersion": 3,
              "Kind": "muralis.desktopLayout",
              "Items": [
                {
                  "Id": "dir_1",
                  "Name": "Pictures",
                  "Target": { "kind": "folder", "Path": "C:\\Users\\me\\Pictures" }
                }
              ]
            }
            """;

        var restored = JsonSerializer.Deserialize<DesktopLayout>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Empty(restored.Validate());
        Assert.Single(restored.Items);
        Assert.Equal(1.6, restored.Proximity.MaxScale);
        Assert.Equal(600, restored.Dock.HideDelayMilliseconds);
        Assert.True(restored.Dock.AutoHide);
        Assert.Empty(restored.Dock.Entries);
        Assert.Equal(96, restored.Items[0].SizeDip);
        Assert.Equal(CanvasAnchor.Center, restored.Items[0].Anchor);
    }

    [Fact]
    public void TheDockNamesTheItemsItShows()
    {
        var layout = FromItems(Item("a"), Item("b"), Item("c"));
        layout.Dock.Entries.Add(new DockEntry { ItemId = "c" });
        layout.Dock.Entries.Add(new DockEntry { ItemId = "a" });

        Assert.Empty(layout.Validate());
        Assert.True(layout.IsDocked("c"));
        Assert.Equal(0, layout.Dock.IndexOf("c"));
        Assert.Equal(1, layout.Dock.IndexOf("a"));
        Assert.False(layout.IsDocked("b"));

        // The canvas is what the dock does not name, in the order the document holds them.
        Assert.Equal(["b"], layout.FreeItems().Select(item => item.Id));
    }

    [Fact]
    public void ADockedItemThatIsNotInTheItemList_IsRejected()
    {
        var layout = FromItems(Item("a"));
        layout.Dock.Entries.Add(new DockEntry { ItemId = "ghost" });

        Assert.Contains(layout.Validate(), problem => problem.Contains("not an item of this layout", StringComparison.Ordinal));
    }

    [Fact]
    public void AnItemCannotBeInTheDockTwice()
    {
        var layout = FromItems(Item("a"));
        layout.Dock.Entries.Add(new DockEntry { ItemId = "a" });
        layout.Dock.Entries.Add(new DockEntry { ItemId = "a" });

        Assert.Contains(layout.Validate(), problem => problem.Contains("in the dock twice", StringComparison.Ordinal));
    }

    [Fact]
    public void ADockEntryWithoutAnItemId_IsRejected()
    {
        var layout = FromItems(Item("a"));
        layout.Dock.Entries.Add(new DockEntry { ItemId = "  " });

        Assert.Contains(layout.Validate(), problem => problem.Contains("entry needs an item id", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitNullParameters_AreRejectedByValidation()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Proximity = null!;

        Assert.Contains(layout.Validate(), problem => problem.Contains("proximity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AMissingDock_IsRejectedByValidation()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Dock = null!;

        Assert.Contains(layout.Validate(), problem => problem.Contains("dock options are missing", StringComparison.Ordinal));
    }

    [Fact]
    public void ALayoutWithoutAnItemList_IsRejected()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Items = null!;

        Assert.Contains(layout.Validate(), problem => problem.Contains("item list", StringComparison.Ordinal));
    }

    [Fact]
    public void APrototypeTile_DoesNotLoadAsACurrentItem()
    {
        // Version 1 tiles carried a glyph key and a position but nothing to open. They must not slip
        // into the current document as items without a target.
        const string json = """
            {
              "SchemaVersion": 1,
              "Items": [ { "Id": "steam", "Name": "Steam", "IconKey": "steam" } ]
            }
            """;

        var restored = JsonSerializer.Deserialize<DesktopLayout>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Contains(restored.Validate(), problem => problem.Contains("needs a target", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ItemsNeedAnId(string id)
    {
        var layout = FromItems(Item("a"), Item("b"));
        layout.Items[0].Id = id;

        Assert.Contains(layout.Validate(), problem => problem.Contains("non-empty id", StringComparison.Ordinal));
    }

    [Fact]
    public void ItemIdsMustBeUnique()
    {
        var layout = FromItems(Item("a"), Item("b"));
        layout.Items[1].Id = layout.Items[0].Id;

        Assert.Contains(layout.Validate(), problem => problem.Contains("used twice", StringComparison.Ordinal));
    }

    [Fact]
    public void ItemsNeedAName()
    {
        var layout = FromItems(Item("a"));
        layout.Items[0].Name = "";

        Assert.Contains(layout.Validate(), problem => problem.Contains("needs a name", StringComparison.Ordinal));
    }

    [Fact]
    public void AnItemWithoutATarget_IsRejected()
    {
        var layout = FromItems(Item("a"));
        layout.Items[0].Target = null!;

        Assert.Contains(layout.Validate(), problem => problem.Contains("needs a target", StringComparison.Ordinal));
    }

    [Fact]
    public void ProximityParametersAreRangeChecked()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Proximity.MaxScale = 9;
        layout.Proximity.InfluenceRadiusDip = 0;

        var problems = layout.Validate();

        Assert.Contains(problems, problem => problem.Contains("max scale", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("influence radius", StringComparison.Ordinal));
    }

    [Fact]
    public void DockParametersAreRangeChecked()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Dock.HideDelayMilliseconds = -1;
        layout.Dock.ItemSizeDip = 4;
        layout.Dock.MaxScale = 9;
        layout.Dock.PeekSizeDip = 400;
        layout.Dock.Spring.DampingRatio = 5;

        var problems = layout.Validate();

        Assert.Contains(problems, problem => problem.Contains("delays", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("item size", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("magnification", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("peek size", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("dock spring", StringComparison.Ordinal));
    }

    [Fact]
    public void MotionSpringsAreRangeChecked()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Motion.Hover.PeriodSeconds = 0;

        var problems = layout.Validate();

        Assert.Contains(problems, problem => problem.Contains("hover spring", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewerSchema_IsRejectedRatherThanMisread()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.SchemaVersion = DesktopLayout.CurrentSchemaVersion + 1;

        Assert.Contains(layout.Validate(), problem => problem.Contains("newer version", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileThatIsNotADesktopLayout_IsRejected()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Kind = "muralis.somethingElse";

        Assert.Contains(layout.Validate(), problem => problem.Contains("kind must be", StringComparison.Ordinal));
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var layout = FromItems(Item("a"), Item("b"));
        layout.Items[0].OffsetXDip = -195;

        var clone = layout.Clone();
        clone.Items[0].OffsetXDip = 999;
        ((FolderTarget)clone.Items[0].Target).Path = @"C:\elsewhere";
        clone.Proximity.MaxScale = 3;
        clone.Dock.HideDelayMilliseconds = 10;
        clone.Items.RemoveAt(0);

        Assert.Equal(-195, layout.Items[0].OffsetXDip);
        Assert.Equal(@"C:\pictures", ((FolderTarget)layout.Items[0].Target).Path);
        Assert.Equal(1.6, layout.Proximity.MaxScale);
        Assert.Equal(600, layout.Dock.HideDelayMilliseconds);
        Assert.Equal(2, layout.Items.Count);
        Assert.Single(clone.Items);
    }

    private static DesktopLayout FromItems(params DesktopItem[] items)
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Items.AddRange(items);
        return layout;
    }

    private static DesktopItem Item(string id) => new()
    {
        Id = id,
        Name = id,
        Target = new FolderTarget { Path = @"C:\pictures" },
    };
}
