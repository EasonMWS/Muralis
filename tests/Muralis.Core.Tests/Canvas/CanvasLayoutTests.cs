using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Canvas;
using Xunit;

namespace Muralis.Core.Tests.Canvas;

public sealed class CanvasLayoutTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public CanvasLayoutTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-canvas-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SeedLayout_IsValid()
    {
        Assert.Empty(CanvasLayout.CreateSeed().Validate());
    }

    [Fact]
    public void SeedLayout_ShowsFourFreeAndFourDockItems()
    {
        var layout = CanvasLayout.CreateSeed();

        Assert.Equal(8, layout.Items.Count);
        Assert.Equal(4, layout.Items.Count(item => item.Placement == CanvasItemPlacement.Free));
        Assert.Equal(4, layout.Items.Count(item => item.Placement == CanvasItemPlacement.Dock));
        Assert.Equal(8, layout.Items.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void SeedLayout_FreeItemsSitInARowAroundTheCentre()
    {
        var row = CanvasLayout.CreateSeed().Items
            .Where(item => item.Placement == CanvasItemPlacement.Free)
            .ToList();

        Assert.All(row, item => Assert.Equal(CanvasAnchor.Center, item.Anchor));
        Assert.All(row, item => Assert.Equal(row[0].OffsetYDip, item.OffsetYDip));
        Assert.Equal(row.Select(item => item.OffsetXDip).OrderBy(value => value), row.Select(item => item.OffsetXDip));
    }

    [Fact]
    public void Layout_RoundTripsThroughJson()
    {
        var layout = CanvasLayout.CreateSeed();
        layout.Items[0].OffsetXDip = -123.5;
        layout.Proximity.MaxScale = 1.75;
        layout.Dock.Edge = CanvasDockEdge.Right;

        var json = JsonSerializer.Serialize(layout, JsonOptions);
        var restored = JsonSerializer.Deserialize<CanvasLayout>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Empty(restored.Validate());
        Assert.Equal(layout.Items.Count, restored.Items.Count);
        Assert.Equal(-123.5, restored.Items[0].OffsetXDip);
        Assert.Equal(CanvasItemPlacement.Dock, restored.Items[5].Placement);
        Assert.Equal(1.75, restored.Proximity.MaxScale);
        Assert.Equal(CanvasDockEdge.Right, restored.Dock.Edge);
        Assert.Equal(ProximityFalloff.Smoothstep, restored.Proximity.Falloff);
        Assert.Equal("\"Smoothstep\"", JsonSerializer.Serialize(new CanvasProximityOptions().Falloff, JsonOptions));
    }

    [Fact]
    public void HandEditedJson_MayOmitAnythingButTheItems()
    {
        // The prototype file is meant to be edited by hand: omitted parameters keep their
        // defaults instead of failing the load.
        const string json = """
            {
              "Items": [
                { "Id": "steam", "Name": "Steam", "IconKey": "steam" }
              ]
            }
            """;

        var restored = JsonSerializer.Deserialize<CanvasLayout>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Empty(restored.Validate());
        Assert.Single(restored.Items);
        Assert.Equal(1.6, restored.Proximity.MaxScale);
        Assert.Equal(600, restored.Dock.HideDelayMilliseconds);
        Assert.Equal(96, restored.Items[0].SizeDip);
        Assert.Equal(CanvasAnchor.Center, restored.Items[0].Anchor);
    }

    [Fact]
    public void ExplicitNullParameters_AreRejectedByValidation()
    {
        var layout = CanvasLayout.CreateSeed();
        layout.Proximity = null!;

        Assert.Contains(layout.Validate(), problem => problem.Contains("proximity", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ItemsNeedAnId(string id)
    {
        var layout = CanvasLayout.CreateSeed();
        layout.Items[0].Id = id;

        Assert.NotEmpty(layout.Validate());
    }

    [Fact]
    public void ItemIdsMustBeUnique()
    {
        var layout = CanvasLayout.CreateSeed();
        layout.Items[1].Id = layout.Items[0].Id;

        Assert.Contains(layout.Validate(), problem => problem.Contains("used twice", StringComparison.Ordinal));
    }

    [Fact]
    public void ItemsNeedNameAndIcon()
    {
        var layout = CanvasLayout.CreateSeed();
        layout.Items[2].Name = "";
        layout.Items[3].IconKey = "";

        var problems = layout.Validate();

        Assert.Contains(problems, problem => problem.Contains("needs a name", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("needs an icon key", StringComparison.Ordinal));
    }

    [Fact]
    public void ProximityParametersAreRangeChecked()
    {
        var layout = CanvasLayout.CreateSeed();
        layout.Proximity.MaxScale = 9;
        layout.Proximity.InfluenceRadiusDip = 0;

        var problems = layout.Validate();

        Assert.Contains(problems, problem => problem.Contains("max scale", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("influence radius", StringComparison.Ordinal));
    }

    [Fact]
    public void DockParametersAreRangeChecked()
    {
        var layout = CanvasLayout.CreateSeed();
        layout.Dock.HideDelayMilliseconds = -1;
        layout.Dock.CollapsedScale = 1.4;
        layout.Dock.ExpandedScale = 1.0;

        var problems = layout.Validate();

        Assert.Contains(problems, problem => problem.Contains("delays", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("expanded dock scale", StringComparison.Ordinal));
    }

    [Fact]
    public void MotionSpringsAreRangeChecked()
    {
        var layout = CanvasLayout.CreateSeed();
        layout.Motion.Hover.PeriodSeconds = 0;
        layout.Motion.Dock.DampingRatio = 5;

        var problems = layout.Validate();

        Assert.Contains(problems, problem => problem.Contains("hover spring", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("dock spring", StringComparison.Ordinal));
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var layout = CanvasLayout.CreateSeed();
        var clone = layout.Clone();

        clone.Items[0].OffsetXDip = 999;
        clone.Proximity.MaxScale = 3;
        clone.Dock.HideDelayMilliseconds = 10;
        clone.Items.RemoveAt(0);

        Assert.Equal(-255, layout.Items[0].OffsetXDip);
        Assert.Equal(1.6, layout.Proximity.MaxScale);
        Assert.Equal(600, layout.Dock.HideDelayMilliseconds);
        Assert.Equal(8, layout.Items.Count);
        Assert.Equal(8, clone.Items.Count + 1);
    }

    [Fact]
    public async Task Store_SavesAndRestoresALayout()
    {
        var store = NewStore();
        var layout = CanvasLayout.CreateSeed();
        layout.Items[0].OffsetXDip = 42;

        await store.SaveAsync(layout);
        var restored = await store.LoadAsync();

        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(42, restored.Items[0].OffsetXDip);
        Assert.Equal(layout.Items.Select(item => item.Id), restored.Items.Select(item => item.Id));
        Assert.Empty(restored.Validate());
    }

    [Fact]
    public async Task Store_FallsBackToTheSeedWhenTheFileIsMissing()
    {
        var restored = await NewStore().LoadAsync();

        Assert.Equal(8, restored.Items.Count);
    }

    [Fact]
    public async Task Store_SetsAsideAFileThatDoesNotParse()
    {
        var store = NewStore();
        await File.WriteAllTextAsync(store.FilePath, "{ this is not json");

        var restored = await store.LoadAsync();

        Assert.Equal(8, restored.Items.Count);
        Assert.False(File.Exists(store.FilePath));
        Assert.True(File.Exists(store.FilePath + ".bad"));
    }

    [Fact]
    public async Task Store_SetsAsideAFileThatDoesNotValidate()
    {
        var store = NewStore();
        await File.WriteAllTextAsync(
            store.FilePath,
            """{ "Items": [ { "Id": "a", "Name": "A", "IconKey": "a", "SizeDip": 99999 } ] }""");

        var restored = await store.LoadAsync();

        Assert.Equal(8, restored.Items.Count);
        Assert.True(File.Exists(store.FilePath + ".bad"));
    }

    [Fact]
    public async Task Store_OverwritesThePreviousLayoutAtomically()
    {
        var store = NewStore();
        var first = CanvasLayout.CreateSeed();
        var second = CanvasLayout.CreateSeed();
        second.Items.RemoveRange(4, 4);

        await store.SaveAsync(first);
        await store.SaveAsync(second);
        var restored = await store.LoadAsync();

        Assert.Equal(4, restored.Items.Count);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    private CanvasLayoutStore NewStore() =>
        new(NullLogger<CanvasLayoutStore>.Instance, Path.Combine(_directory, "desktop-canvas-prototype.json"));
}
