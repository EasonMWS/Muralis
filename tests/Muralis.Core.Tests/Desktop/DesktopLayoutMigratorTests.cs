using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The Phase 2 prototype file is read once. Its parameters describe the canvas and carry over; its
/// tiles were never anything but pictures of programs, so they are counted and left behind. Nothing
/// here writes a file — a migration that fails must cost the user nothing.
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
              "Dock": { "Edge": "Right", "HideDelayMilliseconds": 900 },
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
        Assert.Equal(CanvasDockEdge.Right, result.Layout.Dock.Edge);
        Assert.Equal(900, result.Layout.Dock.HideDelayMilliseconds);
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
}
