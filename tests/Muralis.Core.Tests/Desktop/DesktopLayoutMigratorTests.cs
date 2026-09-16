using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Dock;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// Older desktop documents are read once and brought forward. Nothing here writes a file — a
/// migration that fails must cost the user nothing — and nothing invents items: a prototype tile
/// that pointed at nothing stays a count, and a version 2 item keeps every field it had.
/// </summary>
public sealed class DesktopLayoutMigratorTests
{
    [Fact]
    public void APrototypeLayout_CarriesItsParametersAndDropsItsTiles()
    {
        const string json = """
            {
              "SchemaVersion": 1,
              "Proximity": { "MaxScale": 1.75, "InfluenceRadiusDip": 220, "Falloff": "Gaussian" },
              "Motion": { "Hover": { "PeriodSeconds": 0.4, "DampingRatio": 0.8 } },
              "Dock": { "Edge": "Right", "HideDelayMilliseconds": 900, "TriggerSizeDip": 18 },
              "Items": [
                { "Id": "steam", "Name": "Steam", "IconKey": "steam" },
                { "Id": "chrome", "Name": "Chrome", "IconKey": "chrome" },
                { "Id": "blender", "Name": "Blender", "IconKey": "blender" }
              ]
            }
            """;

        var result = DesktopLayoutMigrator.MigrateFromPrototype(json);

        Assert.Null(result.Error);
        Assert.NotNull(result.Layout);
        Assert.Equal(3, result.DroppedItems);
        Assert.Empty(result.Layout.Items);
        Assert.Empty(result.Layout.Validate());
        Assert.Equal(1.75, result.Layout.Proximity.MaxScale);
        Assert.Equal(220, result.Layout.Proximity.InfluenceRadiusDip);
        Assert.Equal(ProximityFalloff.Gaussian, result.Layout.Proximity.Falloff);
        Assert.Equal(0.4, result.Layout.Motion.Hover.PeriodSeconds);
        Assert.Equal(DockEdge.Right, result.Layout.Dock.Edge);
        Assert.Equal(900, result.Layout.Dock.HideDelayMilliseconds);
        Assert.Equal(18, result.Layout.Dock.TriggerThicknessDip);

        // The prototype had no dock to speak of: nothing in it and nothing switched on.
        Assert.Empty(result.Layout.Dock.Entries);
        Assert.False(result.Layout.Dock.Enabled);
    }

    [Fact]
    public void APrototypeWithoutAVersion_IsStillAPrototype()
    {
        // A hand-edited file may have left the version out; that is exactly the case the numbers are
        // defaulted for.
        var result = DesktopLayoutMigrator.MigrateFromPrototype("""{ "Items": [] }""");

        Assert.Null(result.Error);
        Assert.NotNull(result.Layout);
        Assert.Equal(0, result.DroppedItems);
    }

    [Fact]
    public void APrototypeWithoutItems_IsStillMigrated()
    {
        var result = DesktopLayoutMigrator.MigrateFromPrototype("""{ "SchemaVersion": 1 }""");

        Assert.Null(result.Error);
        Assert.NotNull(result.Layout);
        Assert.Equal(0, result.DroppedItems);
        Assert.Empty(result.Layout.Validate());
    }

    [Fact]
    public void ACurrentDocument_IsNotMistakenForAPrototype()
    {
        var result = DesktopLayoutMigrator.MigrateFromPrototype(
            $$"""{ "SchemaVersion": {{DesktopLayout.CurrentSchemaVersion}}, "Items": [] }""");

        Assert.Null(result.Layout);
        Assert.Contains("not a prototype", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenJson_IsReported()
    {
        var result = DesktopLayoutMigrator.MigrateFromPrototype("{ this is not json");

        Assert.Null(result.Layout);
        Assert.Contains("not valid JSON", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFile_IsReported()
    {
        var result = DesktopLayoutMigrator.MigrateFromPrototype("null");

        Assert.Null(result.Layout);
        Assert.Contains("empty", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ImpossibleParameters_StopTheMigration()
    {
        var result = DesktopLayoutMigrator.MigrateFromPrototype(
            """{ "SchemaVersion": 1, "Proximity": { "MaxScale": 9 } }""");

        Assert.Null(result.Layout);
        Assert.Contains("max scale", result.Error!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ version 2

    private const string Version2Document = """
        {
          "SchemaVersion": 2,
          "Kind": "muralis.desktopLayout",
          "Proximity": { "MaxScale": 1.5 },
          "Motion": {
            "Hover": { "PeriodSeconds": 0.3, "DampingRatio": 0.85 },
            "Dock": { "PeriodSeconds": 0.5, "DampingRatio": 0.6 }
          },
          "Dock": {
            "Edge": "Bottom",
            "TriggerSizeDip": 24,
            "ShowDelayMilliseconds": 200,
            "HideDelayMilliseconds": 700,
            "CollapsedScale": 0.25,
            "ExpandedScale": 1.0,
            "ItemSizeDip": 56,
            "ItemSpacingDip": 16,
            "EdgeMarginDip": 10,
            "PaddingDip": 10,
            "Proximity": { "MaxScale": 1.8, "InfluenceRadiusDip": 140, "Falloff": "Gaussian" }
          },
          "Items": [
            {
              "Id": "app_1",
              "Name": "Editor",
              "Target": { "kind": "application", "Path": "C:\\apps\\editor.exe" },
              "Placement": "Dock",
              "Anchor": "Center",
              "OffsetXDip": 0,
              "OffsetYDip": 0,
              "SizeDip": 96,
              "Z": 0,
              "IsVisible": true
            },
            {
              "Id": "dir_1",
              "Name": "Pictures",
              "Target": { "kind": "folder", "Path": "C:\\Users\\me\\Pictures" },
              "Placement": "Free",
              "Anchor": "BottomRight",
              "OffsetXDip": -60,
              "OffsetYDip": -60,
              "SizeDip": 96,
              "Z": 3,
              "IsVisible": true
            },
            {
              "Id": "link_1",
              "Name": "Notes",
              "Target": { "kind": "shortcut", "Path": "C:\\links\\notes.lnk" },
              "Placement": "Dock",
              "IconKey": "note"
            }
          ]
        }
        """;

    [Fact]
    public void AVersion2Layout_MovesThePlacementTagsIntoDockEntries()
    {
        var result = DesktopLayoutMigrator.UpgradeFromVersion2(Version2Document);

        Assert.Null(result.Error);
        Assert.NotNull(result.Layout);
        Assert.Empty(result.Layout.Validate());
        Assert.Equal(3, result.Layout.Items.Count);

        // Two of the three items said they were docked; the order they are read in is the order the
        // document held them, which is the only record of the order the user had.
        Assert.Equal(["app_1", "link_1"], result.Layout.Dock.Entries.Select(entry => entry.ItemId));
        Assert.True(result.Layout.Dock.Enabled);
        Assert.Equal(["dir_1"], result.Layout.FreeItems().Select(item => item.Id));
    }

    [Fact]
    public void AVersion2Item_KeepsEveryFieldItHad()
    {
        var result = DesktopLayoutMigrator.UpgradeFromVersion2(Version2Document);

        var pictures = result.Layout!.Items.Single(item => item.Id == "dir_1");
        Assert.Equal("Pictures", pictures.Name);
        Assert.IsType<FolderTarget>(pictures.Target);
        Assert.Equal(@"C:\Users\me\Pictures", pictures.Location);
        Assert.Equal(CanvasAnchor.BottomRight, pictures.Anchor);
        Assert.Equal(-60, pictures.OffsetXDip);
        Assert.Equal(3, pictures.Z);

        var notes = result.Layout.Items.Single(item => item.Id == "link_1");
        Assert.Equal("note", notes.IconKey);
        Assert.Equal(96, notes.SizeDip);
    }

    [Fact]
    public void AVersion2Dock_KeepsItsNumbersAndTurnsItsCollapsedScaleIntoAPeek()
    {
        var result = DesktopLayoutMigrator.UpgradeFromVersion2(Version2Document);
        var dock = result.Layout!.Dock;

        Assert.Equal(DockEdge.Bottom, dock.Edge);
        Assert.Equal(24, dock.TriggerThicknessDip);
        Assert.Equal(200, dock.ShowDelayMilliseconds);
        Assert.Equal(700, dock.HideDelayMilliseconds);
        Assert.Equal(56, dock.ItemSizeDip);
        Assert.Equal(16, dock.SpacingDip);
        Assert.Equal(10, dock.EdgeMarginDip);
        Assert.Equal(10, dock.PaddingDip);
        Assert.True(dock.AutoHide);
        Assert.Equal(1.8, dock.MaxScale);
        Assert.Equal(140, dock.InfluenceRadiusDip);
        Assert.Equal(ProximityFalloff.Gaussian, dock.Falloff);

        // The rail was 76 DIP thick and the old collapsed scale showed a quarter of it.
        Assert.Equal(19, dock.PeekSizeDip);

        // The dock's spring moves in with the rest of the dock's parameters.
        Assert.Equal(0.5, dock.Spring.PeriodSeconds);
        Assert.Equal(0.6, dock.Spring.DampingRatio);
        Assert.Equal(0.3, result.Layout.Motion.Hover.PeriodSeconds);
    }

    [Fact]
    public void AVersion2LayoutWithNoDockedItems_LeavesTheDockSwitchedOff()
    {
        const string json = """
            {
              "SchemaVersion": 2,
              "Kind": "muralis.desktopLayout",
              "Items": [
                {
                  "Id": "dir_1",
                  "Name": "Pictures",
                  "Target": { "kind": "folder", "Path": "C:\\Users\\me\\Pictures" },
                  "Placement": "Free"
                }
              ]
            }
            """;

        var result = DesktopLayoutMigrator.UpgradeFromVersion2(json);

        Assert.Null(result.Error);
        Assert.NotNull(result.Layout);
        Assert.False(result.Layout.Dock.Enabled);
        Assert.Empty(result.Layout.Dock.Entries);
        Assert.Single(result.Layout.Items);
    }

    [Fact]
    public void AVersion2LayoutWithoutADockSection_StillReads()
    {
        const string json = """
            {
              "SchemaVersion": 2,
              "Kind": "muralis.desktopLayout",
              "Items": []
            }
            """;

        var result = DesktopLayoutMigrator.UpgradeFromVersion2(json);

        Assert.Null(result.Error);
        Assert.NotNull(result.Layout);
        Assert.Empty(result.Layout.Validate());
        Assert.Equal(600, result.Layout.Dock.HideDelayMilliseconds);
    }

    [Fact]
    public void AVersion2ItemWithABrokenTarget_StopsTheUpgrade()
    {
        const string json = """
            {
              "SchemaVersion": 2,
              "Kind": "muralis.desktopLayout",
              "Items": [ { "Id": "x", "Name": "X", "Target": { "kind": "teleport" } } ]
            }
            """;

        var result = DesktopLayoutMigrator.UpgradeFromVersion2(json);

        Assert.Null(result.Layout);
        Assert.Contains("version 2 layout", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportedVersion_IsReadWithoutBindingToAnyShape()
    {
        Assert.Equal(3, DesktopLayoutMigrator.SchemaVersionOf("""{ "SchemaVersion": 3 }"""));
        Assert.Equal(0, DesktopLayoutMigrator.SchemaVersionOf("""{ "Items": [] }"""));
        Assert.Null(DesktopLayoutMigrator.SchemaVersionOf("{ not json"));
        Assert.Null(DesktopLayoutMigrator.SchemaVersionOf("[ 1, 2 ]"));
    }

    // ------------------------------------------------------------ version 3

    private const string Version3Document = """
        {
          "SchemaVersion": 3,
          "Kind": "muralis.desktopLayout",
          "Proximity": { "MaxScale": 1.7 },
          "Motion": { "Hover": { "PeriodSeconds": 0.35, "DampingRatio": 0.9 } },
          "Dock": {
            "Enabled": true,
            "Edge": "Left",
            "Entries": [ { "ItemId": "app_1" } ]
          },
          "Items": [
            {
              "Id": "app_1",
              "Name": "Editor",
              "Target": { "kind": "application", "Path": "C:\\apps\\editor.exe" },
              "Anchor": "Center",
              "OffsetXDip": 12,
              "SizeDip": 96,
              "Z": 1,
              "IsVisible": true
            }
          ]
        }
        """;

    [Fact]
    public void AVersion3Layout_ReadsAsItIsUnderTheCurrentVersion()
    {
        var result = DesktopLayoutMigrator.UpgradeFromVersion3(Version3Document);

        Assert.Null(result.Error);
        Assert.NotNull(result.Layout);
        Assert.Empty(result.Layout.Validate());

        // Version 4 only added sections, so everything the old file did say is read back unchanged.
        Assert.Equal(DesktopLayout.CurrentSchemaVersion, result.Layout.SchemaVersion);
        Assert.Equal(1.7, result.Layout.Proximity.MaxScale);
        Assert.Equal(0.35, result.Layout.Motion.Hover.PeriodSeconds);
        Assert.Equal(DockEdge.Left, result.Layout.Dock.Edge);
        Assert.Equal(["app_1"], result.Layout.Dock.Entries.Select(entry => entry.ItemId));
        Assert.Equal(12, result.Layout.Items[0].OffsetXDip);
        Assert.Equal(@"C:\apps\editor.exe", result.Layout.Items[0].Location);
    }

    [Fact]
    public void AVersion3Layout_HasNoTakeoverAndNoSources()
    {
        // What the old file has no opinion about keeps the default reading: the native desktop, and
        // items that were not adopted from anywhere.
        var result = DesktopLayoutMigrator.UpgradeFromVersion3(Version3Document);

        Assert.Equal(DesktopMode.Native, result.Layout!.Takeover.Mode);
        Assert.True(result.Layout.Takeover.AdoptDesktopItems);
        Assert.Empty(result.Layout.Takeover.IgnoredSourcePaths);
        Assert.Equal(string.Empty, result.Layout.Items[0].SourcePath);
    }

    [Fact]
    public void AVersion3LayoutWithABrokenTarget_StopsTheUpgrade()
    {
        var result = DesktopLayoutMigrator.UpgradeFromVersion3(
            """{ "SchemaVersion": 3, "Kind": "muralis.desktopLayout", "Items": [ { "Id": "x", "Name": "X" } ] }""");

        Assert.Null(result.Layout);
        Assert.Contains("needs a target", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDispatch_ChoosesTheStepTheVersionNeeds()
    {
        // Version 1 is read as a prototype: nothing of it can be an item.
        var prototype = DesktopLayoutMigrator.Upgrade(Version2Document.Replace("\"SchemaVersion\": 2", "\"SchemaVersion\": 1"), 1);
        Assert.Null(prototype.Error);
        Assert.Empty(prototype.Layout!.Items);

        // Version 2 keeps its items and gains dock entries.
        var two = DesktopLayoutMigrator.Upgrade(Version2Document, 2);
        Assert.Null(two.Error);
        Assert.Equal(3, two.Layout!.Items.Count);
        Assert.Equal(2, two.Layout.Dock.Entries.Count);

        // Version 3 is the current shape already.
        var three = DesktopLayoutMigrator.Upgrade(Version3Document, 3);
        Assert.Null(three.Error);
        Assert.Equal(DesktopLayout.CurrentSchemaVersion, three.Layout!.SchemaVersion);
    }

    [Fact]
    public void TheDispatch_RefusesAVersionItHasNoStepFor()
    {
        var result = DesktopLayoutMigrator.Upgrade("""{ "SchemaVersion": 99 }""", 99);

        Assert.Null(result.Layout);
        Assert.Contains("99", result.Error!, StringComparison.Ordinal);
    }
}
