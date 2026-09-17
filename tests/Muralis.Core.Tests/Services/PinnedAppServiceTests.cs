using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Services;

/// <summary>
/// The pinned list as the dock uses it. The shell is behind two interfaces here, so what a launch asks
/// for, what an add refuses and what gets written can all be checked without starting a program or
/// touching the user's own files.
/// </summary>
public sealed class PinnedAppServiceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("pinned-service");
    private readonly string _settingsPath;
    private readonly FakeInspector _inspector = new();
    private readonly FakeLauncher _launcher = new();

    public PinnedAppServiceTests()
    {
        _settingsPath = _workspace.PathOf("settings.json");
    }

    [Fact]
    public async Task Restore_CarriesTheSavedPinsInTheirOrder()
    {
        var settings = await SeedAsync(Pin("pin-a", "A", @"C:\tools\a.exe"), Pin("pin-b", "B", @"C:\tools\b.exe"));
        var service = new PinnedAppService(settings, _inspector, _launcher, NullLogger<PinnedAppService>.Instance);
        var published = 0;
        service.Changed += (_, _) => published++;

        var restored = await service.RestoreAsync();

        Assert.Equal(["pin-a", "pin-b"], restored.Select(app => app.Id));
        Assert.Equal(["pin-a", "pin-b"], service.Items.Select(app => app.Id));
        Assert.Equal(1, published);
    }

    [Fact]
    public async Task Restore_LeavesOutAnEntryItCannotReadAndKeepsTheRest()
    {
        // One unreadable entry is not a reason to lose the dock. It is not repaired into a pin the user
        // never made either, so the broken entry is simply not there.
        var settings = await SeedAsync(
            Pin("pin-a", "A", @"C:\tools\a.exe"),
            new PinnedAppSettings { Id = string.Empty, DisplayName = "Broken", LaunchTarget = @"C:\tools\broken.exe" },
            Pin("pin-c", "C", @"C:\tools\c.exe"));
        var service = new PinnedAppService(settings, _inspector, _launcher, NullLogger<PinnedAppService>.Instance);

        var restored = await service.RestoreAsync();

        Assert.Equal(["pin-a", "pin-c"], restored.Select(app => app.Id));
    }

    [Fact]
    public async Task Add_RefusesAPathTheDockDoesNotPinWithoutAskingTheShell()
    {
        var service = await RestoredAsync();

        var result = await service.AddAsync(@"C:\Users\e\Desktop\notes.txt");

        Assert.Equal(PinnedAppAddOutcome.Unsupported, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Equal(0, _inspector.DescribeCount);
    }

    [Fact]
    public async Task Add_RefusesAnEmptyPath()
    {
        var service = await RestoredAsync();

        Assert.Equal(PinnedAppAddOutcome.Unsupported, (await service.AddAsync("   ")).Outcome);
    }

    [Fact]
    public async Task Add_WhenTheShellCannotReadTheFile_RefusesInWordsTheCallerCanShow()
    {
        _inspector.OnDescribe = _ => Task.FromResult<ApplicationDescription?>(null);
        var path = _workspace.FileAt("unreadable.exe");
        var service = await RestoredAsync();

        var result = await service.AddAsync(path);

        Assert.Equal(PinnedAppAddOutcome.Unsupported, result.Outcome);
        Assert.Contains("unreadable.exe", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_WhenTheShellItselfFails_RefusesInsteadOfThrowing()
    {
        _inspector.OnDescribe = _ => throw new InvalidOperationException("the shell refused");
        var path = _workspace.FileAt("broken.exe");
        var service = await RestoredAsync();

        var result = await service.AddAsync(path);

        Assert.Equal(PinnedAppAddOutcome.Unsupported, result.Outcome);
        Assert.Contains("the shell refused", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_PinsTheApplicationDescribesItAndSavesIt()
    {
        var path = _workspace.FileAt("editor.exe");
        _inspector.Describe(path, "Editor");
        var service = await RestoredAsync();
        var published = 0;
        service.Changed += (_, _) => published++;

        var result = await service.AddAsync(path);

        Assert.True(result.Succeeded);
        Assert.Equal("Editor", result.Item!.DisplayName);
        Assert.Equal(PinnedAppKind.Application, result.Item.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Item.Id));
        Assert.Equal(1, published);

        var saved = (await ReloadAsync()).PinnedApps.Single();
        Assert.Equal("Editor", saved.DisplayName);
        Assert.Equal(path, saved.LaunchTarget);
        Assert.Equal(result.Item.Id, saved.Id);
    }

    [Fact]
    public async Task Add_RefusesAPinReachedThroughAShortcutOfSomethingAlreadyPinned()
    {
        // The point of resolving a shortcut: the same program must not be pinned twice just because the
        // user happened to pick the shortcut instead of the program the second time.
        var program = _workspace.FileAt("music.exe");
        var shortcut = _workspace.FileAt("Music.lnk");
        _inspector.Describe(program, "Music");
        _inspector.Describe(shortcut, "Music", resolvedTarget: program);
        var service = await RestoredAsync();
        await service.AddAsync(program);

        var result = await service.AddAsync(shortcut);

        Assert.Equal(PinnedAppAddOutcome.Duplicate, result.Outcome);
        Assert.Equal(service.Items.Single().Id, result.ExistingId);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task Add_RefusesATargetThatIsNoLongerThere()
    {
        var path = _workspace.FileAt("gone.exe");
        _inspector.Describe(path, "Gone");
        var service = await RestoredAsync();
        File.Delete(path);

        var result = await service.AddAsync(path);

        Assert.Equal(PinnedAppAddOutcome.Unsupported, result.Outcome);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task Add_WhenTheZoneIsFull_RefusesRatherThanGrowing()
    {
        var settings = await SeedAsync(Enumerable.Range(0, PinnedApps.MaximumCount)
            .Select(index => Pin($"pin-{index}", $"App{index}", $@"C:\tools\app{index}.exe"))
            .ToArray());
        var path = _workspace.FileAt("extra.exe");
        _inspector.Describe(path, "Extra");
        var service = new PinnedAppService(settings, _inspector, _launcher, NullLogger<PinnedAppService>.Instance);
        await service.RestoreAsync();

        var result = await service.AddAsync(path);

        Assert.Equal(PinnedAppAddOutcome.LimitReached, result.Outcome);
        Assert.Equal(PinnedApps.MaximumCount, service.Items.Count);
    }

    [Fact]
    public async Task Remove_TakesThePinOutSavesTheRestAndNeverTouchesTheFile()
    {
        var program = _workspace.FileAt("editor.exe");
        var settings = await SeedAsync(Pin("pin-editor", "Editor", program), Pin("pin-other", "Other", @"C:\tools\other.exe"));
        var service = new PinnedAppService(settings, _inspector, _launcher, NullLogger<PinnedAppService>.Instance);
        await service.RestoreAsync();
        var published = 0;
        service.Changed += (_, _) => published++;

        var removed = await service.RemoveAsync("pin-editor");

        Assert.True(removed);
        Assert.Equal(["pin-other"], service.Items.Select(app => app.Id));
        Assert.Equal(1, published);

        // Unpinning is a change to the dock, never to the application the pin pointed at.
        Assert.True(File.Exists(program));
        Assert.Equal(["pin-other"], (await ReloadAsync()).PinnedApps.Select(entry => entry.Id));
    }

    [Fact]
    public async Task Remove_OfSomethingNotPinned_ChangesNothing()
    {
        var service = await RestoredAsync(Pin("pin-a", "A", @"C:\tools\a.exe"));
        var published = 0;
        service.Changed += (_, _) => published++;

        Assert.False(await service.RemoveAsync("pin-gone"));
        Assert.Equal(0, published);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task Move_PutsThePinWhereTheDropWasAndSavesThatOrder()
    {
        var service = await RestoredAsync(
            Pin("pin-a", "A", @"C:\tools\a.exe"),
            Pin("pin-b", "B", @"C:\tools\b.exe"),
            Pin("pin-c", "C", @"C:\tools\c.exe"));
        var published = 0;
        service.Changed += (_, _) => published++;

        var moved = await service.MoveAsync("pin-a", 2);

        Assert.Equal(["pin-b", "pin-c", "pin-a"], moved.Select(app => app.Id));
        Assert.Equal(1, published);
        Assert.Equal(["pin-b", "pin-c", "pin-a"], (await ReloadAsync()).PinnedApps.Select(entry => entry.Id));
    }

    [Fact]
    public async Task Move_ToWhereTheItemAlreadyIs_SavesNothing()
    {
        var service = await RestoredAsync(Pin("pin-a", "A", @"C:\tools\a.exe"), Pin("pin-b", "B", @"C:\tools\b.exe"));
        var published = 0;
        service.Changed += (_, _) => published++;

        await service.MoveAsync("pin-b", 1);

        Assert.Equal(0, published);
        Assert.Equal(["pin-a", "pin-b"], service.Items.Select(app => app.Id));
    }

    [Fact]
    public async Task Move_OfSomethingNotPinned_ChangesNothing()
    {
        var service = await RestoredAsync(Pin("pin-a", "A", @"C:\tools\a.exe"));
        var published = 0;
        service.Changed += (_, _) => published++;

        await service.MoveAsync("pin-gone", 0);

        Assert.Equal(0, published);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task Launch_AsksTheShellForTheTargetWithThePinsOwnArgumentsAndFolder()
    {
        var program = _workspace.FileAt("editor.exe");
        var settings = await SeedAsync(Pin("pin-editor", "Editor", program, arguments: "--new", workingDirectory: @"C:\Projects"));
        var service = new PinnedAppService(settings, _inspector, _launcher, NullLogger<PinnedAppService>.Instance);
        await service.RestoreAsync();

        var result = await service.LaunchAsync("pin-editor");

        Assert.Equal(ApplicationLaunchOutcome.Launched, result.Outcome);
        var request = Assert.Single(_launcher.Requests);
        Assert.Equal(program, request.Target);
        Assert.Equal("--new", request.Arguments);
        Assert.Equal(@"C:\Projects", request.WorkingDirectory);
    }

    [Fact]
    public async Task Launch_LeavesThePinsOptionalSettingsEmptyWhenTheUserSetNone()
    {
        var program = _workspace.FileAt("editor.exe");
        var service = await RestoredAsync(Pin("pin-editor", "Editor", program));

        await service.LaunchAsync("pin-editor");

        // Empty means "let the application decide", which is not the same as passing an empty value.
        var request = Assert.Single(_launcher.Requests);
        Assert.Null(request.Arguments);
        Assert.Null(request.WorkingDirectory);
    }

    [Fact]
    public async Task Launch_OfSomethingNotPinned_IsRefusedWithoutStartingAnything()
    {
        var service = await RestoredAsync();

        var result = await service.LaunchAsync("pin-gone");

        Assert.Equal(ApplicationLaunchOutcome.Failed, result.Outcome);
        Assert.Empty(_launcher.Requests);
    }

    [Fact]
    public async Task Launch_OfAPinWhoseTargetIsGone_ReportsItAndStartsNothing()
    {
        var program = _workspace.FileAt("gone.exe");
        var settings = await SeedAsync(Pin("pin-gone", "Gone", program));
        var service = new PinnedAppService(settings, _inspector, _launcher, NullLogger<PinnedAppService>.Instance);
        await service.RestoreAsync();
        File.Delete(program);

        var result = await service.LaunchAsync("pin-gone");

        Assert.Equal(ApplicationLaunchOutcome.Missing, result.Outcome);
        Assert.Empty(_launcher.Requests);

        // The pin stays where it was: a target that will be put back is not a reason to lose the pin.
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task Launch_WhenTheShellRefuses_HandsTheFailureBackToTheCaller()
    {
        var program = _workspace.FileAt("editor.exe");
        _launcher.Result = ApplicationLaunchResult.Failed("the shell refused");
        var service = await RestoredAsync(Pin("pin-editor", "Editor", program));

        var result = await service.LaunchAsync("pin-editor");

        Assert.Equal(ApplicationLaunchOutcome.Failed, result.Outcome);
        Assert.Equal("the shell refused", result.Error);
    }

    [Fact]
    public async Task IsAvailable_SaysWhetherTheThingThePinPointsAtIsStillThere()
    {
        var present = _workspace.FileAt("here.exe");
        var absent = _workspace.PathOf("gone.exe");
        var service = await RestoredAsync(Pin("pin-here", "Here", present), Pin("pin-gone", "Gone", absent));

        Assert.True(service.IsAvailable(service.Items[0]));
        Assert.False(service.IsAvailable(service.Items[1]));
    }

    private async Task<PinnedAppService> RestoredAsync(params PinnedAppSettings[] pins)
    {
        var settings = await SeedAsync(pins);
        var service = new PinnedAppService(settings, _inspector, _launcher, NullLogger<PinnedAppService>.Instance);
        await service.RestoreAsync();
        return service;
    }

    private async Task<SettingsService> SeedAsync(params PinnedAppSettings[] pins)
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, _settingsPath);
        await settings.LoadAsync();
        settings.Update(current => current.Dock.PinnedApps = [.. pins]);
        await settings.SaveAsync();
        return settings;
    }

    private async Task<DockSettings> ReloadAsync()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, _settingsPath);
        await settings.LoadAsync();
        return settings.Current.Dock;
    }

    private static PinnedAppSettings Pin(
        string id,
        string name,
        string path,
        string? arguments = null,
        string? workingDirectory = null) =>
        PinnedAppSettings.From(new PinnedApp(
            id,
            name,
            path,
            path,
            PinnedAppTargets.KindOf(path) ?? PinnedAppKind.Application,
            PinnedAppIdentity.Normalize(path),
            arguments,
            workingDirectory));

    public void Dispose() => _workspace.Dispose();

    private sealed class FakeInspector : IApplicationInspector
    {
        private readonly Dictionary<string, ApplicationDescription> _descriptions = new(StringComparer.OrdinalIgnoreCase);

        public int DescribeCount { get; private set; }

        public Func<string, Task<ApplicationDescription?>>? OnDescribe { get; set; }

        public void Describe(string path, string displayName, string? resolvedTarget = null) =>
            _descriptions[path] = new ApplicationDescription(
                PinnedAppTargets.KindOf(path) ?? PinnedAppKind.Application,
                displayName,
                path,
                resolvedTarget);

        public Task<ApplicationDescription?> DescribeAsync(string path, CancellationToken cancellationToken = default)
        {
            DescribeCount++;
            return OnDescribe is not null
                ? OnDescribe(path)
                : Task.FromResult(_descriptions.TryGetValue(path, out var description) ? description : null);
        }
    }

    /// <summary>Records what a launch asked for instead of starting anything.</summary>
    private sealed class FakeLauncher : IApplicationLauncher
    {
        public List<ApplicationLaunchRequest> Requests { get; } = [];

        public ApplicationLaunchResult Result { get; set; } = ApplicationLaunchResult.Launched;

        public Task<ApplicationLaunchResult> LaunchAsync(
            ApplicationLaunchRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }
}
