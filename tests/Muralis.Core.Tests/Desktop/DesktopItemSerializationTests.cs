using System.Text.Json;
using System.Text.Json.Serialization;
using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The target is the part of the document a future phase will extend, so its shape is pinned here:
/// every kind writes its own discriminator, every kind comes back as itself, and nothing that is not
/// a known kind is guessed at.
/// </summary>
public sealed class DesktopItemSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IEnumerable<object[]> EveryKind()
    {
        yield return [new ApplicationTarget { Path = @"C:\apps\editor.exe" }, "application"];
        yield return [new ShortcutTarget { Path = @"C:\Users\me\Desktop\editor.lnk" }, "shortcut"];
        yield return [new FileTarget { Path = @"C:\docs\notes.pdf" }, "file"];
        yield return [new FolderTarget { Path = @"C:\pictures" }, "folder"];
        yield return [new UrlTarget { Url = "https://example.com/watch?v=1" }, "url"];
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void EveryKind_RoundTripsAsItsOwnType(DesktopItemTarget target, string kind)
    {
        var item = new DesktopItem { Id = "item_1", Name = "editor", Target = target };

        var json = JsonSerializer.Serialize(item, JsonOptions);
        var restored = JsonSerializer.Deserialize<DesktopItem>(json, JsonOptions);

        Assert.Contains($"\"kind\": \"{kind}\"", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal(target.GetType(), restored.Target.GetType());
        Assert.Equal(target.Kind, restored.Target.Kind);
        Assert.Equal(target.Location, restored.Target.Location);
        Assert.Empty(restored.Validate());
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void NoFact_IsWrittenTwice(DesktopItemTarget target, string kind)
    {
        // A document that said "kind" and "Kind" would be read by a case insensitive reader as a
        // conflict, and the copy would be a second truth about the same fact. Every object in the
        // file must name each of its facts once, whatever the reader's case rules are.
        var item = new DesktopItem
        {
            Id = "item_1",
            Name = "editor",
            Target = target,
        };

        var json = JsonSerializer.Serialize(item, JsonOptions);

        using var document = JsonDocument.Parse(json);
        AssertNoCaseCollision(document.RootElement, "the document");
        Assert.Contains($"\"kind\": \"{kind}\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Location", json, StringComparison.Ordinal);
    }

    private static void AssertNoCaseCollision(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                Assert.True(seen.Add(property.Name), $"{path} names '{property.Name}' more than once");
                AssertNoCaseCollision(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var entry in element.EnumerateArray())
            {
                AssertNoCaseCollision(entry, $"{path}[{index++}]");
            }
        }
    }

    [Fact]
    public void TheTarget_IsWrittenAsItsOwnObject()
    {
        // A bare path string could not say what kind of thing it is, which is exactly what the
        // migration needs to know, so the target is never flattened into one string.
        var item = new DesktopItem
        {
            Id = "item_1",
            Name = "editor",
            Target = new ShortcutTarget { Path = @"C:\Users\me\Desktop\editor.lnk" },
        };

        var json = JsonSerializer.Serialize(item, JsonOptions);

        Assert.Contains("\"Target\": {", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Target\": \"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKind_IsRefused()
    {
        const string json = """
            { "Id": "a", "Name": "A", "Target": { "kind": "packaged", "PackageFamilyName": "Vendor.App_1" } }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DesktopItem>(json, JsonOptions));
    }

    [Fact]
    public void ATargetWithoutAKind_IsRefused()
    {
        const string json = """
            { "Id": "a", "Name": "A", "Target": { "Path": "C:\\apps\\editor.exe" } }
            """;

        // Which refusal the serialiser picks is its business; that it refuses rather than reading an
        // untyped target as some kind is what the document depends on.
        var error = Record.Exception(() => JsonSerializer.Deserialize<DesktopItem>(json, JsonOptions));

        Assert.True(error is JsonException or NotSupportedException, $"unexpected error: {error}");
    }

    [Fact]
    public void AHandWrittenItem_LoadsAndValidates()
    {
        // The document stays meant for hand editing: property names are read without regard to case
        // and omitted parameters keep the defaults they are documented with.
        const string json = """
            {
              "id": "app_1",
              "name": "Editor",
              "target": { "kind": "application", "path": "C:\\apps\\editor.exe" },
              "Anchor": "BottomRight",
              "OffsetXDip": -40
            }
            """;

        var restored = JsonSerializer.Deserialize<DesktopItem>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Empty(restored.Validate());
        Assert.IsType<ApplicationTarget>(restored.Target);
        Assert.Equal(@"C:\apps\editor.exe", restored.Location);
        Assert.Equal(CanvasAnchor.BottomRight, restored.Anchor);
        Assert.Equal(-40, restored.OffsetXDip);
        Assert.Equal(96, restored.SizeDip);
        Assert.Equal(CanvasItemPlacement.Free, restored.Placement);
    }
}
