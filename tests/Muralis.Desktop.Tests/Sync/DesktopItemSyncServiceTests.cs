using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Dock;
using Muralis.Core.Models;
using Muralis.Desktop.Sync;
using Xunit;

namespace Muralis.Desktop.Tests.Sync;

/// <summary>
/// The sync's own job is small and worth pinning down: it reads the desktop, it hands the adoption to
/// the canvas service rather than editing anything itself, and it notices a change without syncing once
/// per event or once per frame.
/// </summary>
public sealed class DesktopItemSyncServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"muralis-sync-{Guid.NewGuid():N}");
    private readonly string _desktop;
    private readonly string _shared;
    private readonly string _layoutFile;

    public DesktopItemSyncServiceTests()
    {
        _desktop = Path.Combine(_root, "Desktop");
        _shared = Path.Combine(_root, "Public");
        _layoutFile = Path.Combine(_root, "desktop-layout.json");

        Directory.CreateDirectory(_desktop);
        Directory.CreateDirectory(_shared);
    }

    [Fact]
    public async Task Preview_SaysWhatWouldBeAdoptedAndChangesNothing()
    {
        File.WriteAllText(Path.Combine(_desktop, "notes.txt"), "the user's own file");
        Directory.CreateDirectory(Path.Combine(_desktop, "a folder"));

        var canvas = new RecordingCanvas();
        using var sync = Service(canvas);

        var plan = await sync.PreviewAsync();

        // A file and a folder are both desktop content, so both are reported as adoptable.
        Assert.Equal(
            ["a folder", "notes.txt"],
            plan.ToAdopt.Select(entry => Path.GetFileName(entry.SourcePath)).Order(StringComparer.Ordinal));
        Assert.Empty(plan.AlreadyAdopted);
        Assert.Equal(0, canvas.AdoptCalls);
        Assert.False(File.Exists(_layoutFile));
        Assert.Equal("the user's own file", File.ReadAllText(Path.Combine(_desktop, "notes.txt")));
    }

    [Fact]
    public async Task Sync_HandsTheAdoptionToTheCanvasService()
    {
        File.WriteAllText(Path.Combine(_desktop, "notes.txt"), "x");

        var canvas = new RecordingCanvas { Added = 1 };
        using var sync = Service(canvas);

        var result = await sync.SyncAsync();

        Assert.Equal(1, canvas.AdoptCalls);
        Assert.Single(result.Added);
        Assert.NotNull(canvas.LastPlan);
        Assert.Single(canvas.LastPlan!.ToAdopt);
    }

    [Fact]
    public async Task Sync_WithNothingOnTheDesktop_DoesNotAskTheCanvasAtAll()
    {
        var canvas = new RecordingCanvas();
        using var sync = Service(canvas);

        var result = await sync.SyncAsync();

        Assert.Empty(result.Added);
        Assert.Equal(0, canvas.AdoptCalls);
    }

    [Fact]
    public async Task AnEntryTheUserTurnedDown_IsNeverAdopted()
    {
        var path = Path.Combine(_desktop, "notes.txt");
        File.WriteAllText(path, "x");

        var layout = new DesktopLayout();
        layout.Takeover.Ignore(path);
        await new DesktopLayoutStore(NullLogger<DesktopLayoutStore>.Instance, _layoutFile).SaveAsync(layout);

        var canvas = new RecordingCanvas { Added = 1 };
        using var sync = Service(canvas);

        var plan = await sync.PreviewAsync();
        var result = await sync.SyncAsync();

        Assert.Empty(plan.ToAdopt);
        Assert.Single(plan.Declined);
        Assert.Empty(result.Added);
        Assert.Equal(0, canvas.AdoptCalls);
    }

    [Fact]
    public async Task WithAdoptionTurnedOff_TheDesktopIsStillReadAndNothingIsAdopted()
    {
        // The flag is the user's answer to "adopt what is already on my desktop", and it has to hold for
        // a mounted canvas too: that is the path a takeover runs on, and it is the one that does not go
        // through the adopter's own check. The preview keeps reporting what is there either way.
        File.WriteAllText(Path.Combine(_desktop, "notes.txt"), "x");

        var layout = new DesktopLayout();
        layout.Takeover.AdoptDesktopItems = false;
        await new DesktopLayoutStore(NullLogger<DesktopLayoutStore>.Instance, _layoutFile).SaveAsync(layout);

        var canvas = new RecordingCanvas { Added = 1 };
        using var sync = Service(canvas);

        var plan = await sync.PreviewAsync();
        var result = await sync.SyncAsync();

        Assert.Single(plan.ToAdopt);
        Assert.Empty(result.Added);
        Assert.Equal(0, canvas.AdoptCalls);
    }

    [Fact]
    public async Task Watching_TheDesktopFolders_IsOffUntilItIsAskedFor()
    {
        using var sync = Service(new RecordingCanvas());

        Assert.False(sync.IsWatching);

        sync.StartWatching();
        Assert.True(sync.IsWatching);

        sync.StartWatching();
        Assert.True(sync.IsWatching);

        sync.StopWatching();
        Assert.False(sync.IsWatching);

        sync.StopWatching();
        Assert.False(sync.IsWatching);
    }

    [Fact]
    public void Watching_IsNotOfferedOnADesktopFolderThatIsGone()
    {
        // The scanner was built with folders that do not exist, which is what an unplugged drive looks
        // like: there is nothing to watch, and asking for a watch must not throw.
        using var sync = new DesktopItemSyncService(
            NullLogger<DesktopItemSyncService>.Instance,
            new DesktopContentScanner(NullLogger<DesktopContentScanner>.Instance, [Path.Combine(_root, "gone")]),
            new DesktopLayoutStore(NullLogger<DesktopLayoutStore>.Instance, _layoutFile),
            new RecordingCanvas());

        sync.StartWatching();

        Assert.False(sync.IsWatching);
    }

    private DesktopItemSyncService Service(IDesktopCanvasService canvas) =>
        new(
            NullLogger<DesktopItemSyncService>.Instance,
            new DesktopContentScanner(NullLogger<DesktopContentScanner>.Instance, [_desktop, _shared]),
            new DesktopLayoutStore(NullLogger<DesktopLayoutStore>.Instance, _layoutFile),
            canvas);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Stands in for the canvas service and records what it was asked to do.</summary>
    private sealed class RecordingCanvas : IDesktopCanvasService
    {
        internal int AdoptCalls { get; private set; }

        internal DesktopAdoptionPlan? LastPlan { get; private set; }

        internal int Added { get; init; }

        public CanvasPrototypeStatus Status => new(CanvasPrototypeState.Disabled);

        // Nothing here listens for the canvas changing, so the event is a no-op rather than a field
        // the compiler would warn about.
        public event EventHandler<CanvasPrototypeStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public CanvasDiagnosticsSnapshot? Diagnostics => null;

        public Task<CanvasPrototypeStatus> EnableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task DisableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DesktopItem>> GetItemsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DesktopItem>>([]);

        public Task<bool> AddItemAsync(DesktopItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RemoveItemAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<DesktopAdoptionResult> AdoptAsync(
            DesktopAdoptionPlan plan,
            CancellationToken cancellationToken = default)
        {
            AdoptCalls++;
            LastPlan = plan;

            var added = plan.ToAdopt
                .Take(Added)
                .Select(entry => DesktopItemFactory.CreateFromTarget(entry.Target, entry.Name, entry.SourcePath))
                .ToList();

            return Task.FromResult(new DesktopAdoptionResult(added));
        }

        public Task<DockOptions> GetDockAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockOptions());

        public Task<bool> UpdateDockAsync(DockOptions dock, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<DesktopTakeoverOptions> GetTakeoverOptionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopTakeoverOptions());

        public Task UpdateTakeoverOptionsAsync(
            DesktopTakeoverOptions options,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SuspendInteractionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
