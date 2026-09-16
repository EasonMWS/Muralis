using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The document on disk: it is written atomically, a file that cannot be trusted is set aside rather
/// than deleted, and a Phase 2 prototype is read exactly once.
/// </summary>
public sealed class DesktopLayoutStoreTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("desktop-layout-store");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task LoadingForTheFirstTime_StartsEmptyWithoutWritingAnything()
    {
        var store = NewStore();

        var layout = await store.LoadAsync();

        Assert.Empty(layout.Items);
        Assert.Empty(layout.Validate());
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task TheSavedLayout_ComesBackAsItWentOut()
    {
        var store = NewStore();
        var layout = DesktopLayout.CreateEmpty();
        layout.Items.Add(Item("lnk_1", @"C:\links\editor.lnk"));
        layout.Items[0].OffsetXDip = 42;
        layout.Proximity.MaxScale = 1.8;

        await store.SaveAsync(layout);
        var restored = await store.LoadAsync();

        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(42, restored.Items[0].OffsetXDip);
        Assert.Equal(1.8, restored.Proximity.MaxScale);
        Assert.Equal(@"C:\links\editor.lnk", restored.Items[0].Location);
        Assert.Empty(restored.Validate());
    }

    [Fact]
    public async Task Saving_ReplacesTheDocumentInOnePiece()
    {
        var store = NewStore();
        var first = DesktopLayout.CreateEmpty();
        first.Items.Add(Item("a", @"C:\a.exe"));
        first.Items.Add(Item("b", @"C:\b.exe"));
        var second = DesktopLayout.CreateEmpty();
        second.Items.Add(Item("a", @"C:\a.exe"));

        await store.SaveAsync(first);
        await store.SaveAsync(second);
        var restored = await store.LoadAsync();

        Assert.Single(restored.Items);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public async Task AFileThatDoesNotParse_IsSetAsideInsteadOfDeleted()
    {
        var store = NewStore();
        await File.WriteAllTextAsync(store.FilePath, "{ this is not json");

        var layout = await store.LoadAsync();

        Assert.Empty(layout.Items);
        Assert.False(File.Exists(store.FilePath));
        Assert.True(File.Exists(store.FilePath + ".bad"));
    }

    [Fact]
    public async Task AFileThatDoesNotValidate_IsSetAside()
    {
        var store = NewStore();
        await File.WriteAllTextAsync(
            store.FilePath,
            """
            {
              "SchemaVersion": 2,
              "Kind": "muralis.desktopLayout",
              "Items": [
                { "Id": "a", "Name": "A", "SizeDip": 99999, "Target": { "kind": "file", "Path": "C:\\a.txt" } }
              ]
            }
            """);

        var layout = await store.LoadAsync();

        Assert.Empty(layout.Items);
        Assert.True(File.Exists(store.FilePath + ".bad"));
    }

    [Fact]
    public async Task AFileThatIsNotADesktopLayout_IsRefused()
    {
        var store = NewStore();
        await File.WriteAllTextAsync(store.FilePath, """{ "Kind": "muralis.settings", "Items": [] }""");

        var layout = await store.LoadAsync();

        Assert.Empty(layout.Items);
        Assert.True(File.Exists(store.FilePath + ".bad"));
    }

    [Fact]
    public async Task AnItemWhoseTargetKindCannotBeRead_IsSetAside()
    {
        // A hand edit that mistypes or drops the discriminator must cost the user the file, not the
        // app: the serialiser refuses the shape and the store treats it like any other bad file.
        var store = NewStore();
        await File.WriteAllTextAsync(
            store.FilePath,
            """
            {
              "SchemaVersion": 2,
              "Kind": "muralis.desktopLayout",
              "Items": [ { "Id": "a", "Name": "A", "Target": { "Path": "C:\\a.exe" } } ]
            }
            """);

        var layout = await store.LoadAsync();

        Assert.Empty(layout.Items);
        Assert.Empty(layout.Validate());
        Assert.True(File.Exists(store.FilePath + ".bad"));
    }

    [Fact]
    public async Task APrototypeLayout_IsMigratedOnceAndKeptAsABackup()
    {
        var store = NewStore();
        await File.WriteAllTextAsync(
            PrototypePath,
            """
            {
              "SchemaVersion": 1,
              "Proximity": { "MaxScale": 1.75, "InfluenceRadiusDip": 220, "Falloff": "Gaussian" },
              "Items": [
                { "Id": "steam", "Name": "Steam", "IconKey": "steam" },
                { "Id": "chrome", "Name": "Chrome", "IconKey": "chrome" }
              ]
            }
            """);

        var migrated = await store.LoadAsync();

        Assert.Empty(migrated.Items);
        Assert.Empty(migrated.Validate());
        Assert.Equal(1.75, migrated.Proximity.MaxScale);
        Assert.Equal(220, migrated.Proximity.InfluenceRadiusDip);
        Assert.False(File.Exists(PrototypePath));
        Assert.True(File.Exists(PrototypePath + ".v1.bak"));
        Assert.True(File.Exists(store.FilePath), "the migrated document is written out so it is only migrated once");

        var second = await NewStore().LoadAsync();

        Assert.Empty(second.Items);
        Assert.Equal(1.75, second.Proximity.MaxScale);
    }

    [Fact]
    public async Task APrototypeThatCannotBeMigrated_IsLeftWhereItIs()
    {
        var store = NewStore();
        await File.WriteAllTextAsync(PrototypePath, "{ this is not json");
        var before = await File.ReadAllTextAsync(PrototypePath);

        var layout = await store.LoadAsync();

        Assert.Empty(layout.Items);
        Assert.Equal(before, await File.ReadAllTextAsync(PrototypePath));
        Assert.False(File.Exists(PrototypePath + ".v1.bak"));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task APrototypeIsNotTouchedWhileTheCurrentDocumentExists()
    {
        var store = NewStore();
        var layout = DesktopLayout.CreateEmpty();
        layout.Items.Add(Item("a", @"C:\a.exe"));
        await store.SaveAsync(layout);
        await File.WriteAllTextAsync(PrototypePath, """{ "SchemaVersion": 1, "Items": [] }""");

        var restored = await store.LoadAsync();

        Assert.Single(restored.Items);
        Assert.True(File.Exists(PrototypePath));
        Assert.False(File.Exists(PrototypePath + ".v1.bak"));
    }

    private string PrototypePath => _workspace.PathOf("desktop-canvas-prototype.json");

    private DesktopLayoutStore NewStore()
    {
        // The store creates its own directory when it saves; tests that plant a file by hand do not,
        // so the folder the document lives in exists from the start.
        _workspace.DirectoryAt("desktop");
        return new(
            NullLogger<DesktopLayoutStore>.Instance,
            _workspace.PathOf(Path.Combine("desktop", "layout.json")),
            PrototypePath);
    }

    private static DesktopItem Item(string id, string path) => new()
    {
        Id = id,
        Name = id,
        Target = new ApplicationTarget { Path = path },
    };
}
