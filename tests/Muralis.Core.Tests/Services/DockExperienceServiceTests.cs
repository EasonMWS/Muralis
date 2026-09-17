using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Xunit;

namespace Muralis.Core.Tests.Services;

/// <summary>
/// Who gets to decide whether the dock is on the screen. There are two answers — the user's own setting
/// and a desktop mode that has hidden Explorer's icons and needs something in their place — and this is
/// the one place they are reconciled, so that no mode change can hide the only thing on the desktop.
/// </summary>
public sealed class DockExperienceServiceTests
{
    private readonly FakeDockHost _host = new();
    private readonly FakeShelfService _shelf = new();
    private readonly FakeSettingsService _settings = new();

    [Fact]
    public async Task Restore_WithTheSettingOn_ShowsTheDock()
    {
        _settings.Current.Dock.IsVisible = true;
        using var service = Create();

        var shown = await service.RestoreAsync();

        Assert.True(shown);
        Assert.True(service.IsVisible);
        Assert.Equal(1, _host.ShowCount);
    }

    [Fact]
    public async Task Restore_WithTheSettingOff_LeavesTheDockHidden()
    {
        _settings.Current.Dock.IsVisible = false;
        using var service = Create();

        await service.RestoreAsync();

        Assert.False(service.IsVisible);
        Assert.Equal(0, _host.ShowCount);
        Assert.Equal(1, _host.HideCount);
    }

    [Fact]
    public async Task Restore_ReadsTheFoldersBeforeTheDockAppears()
    {
        // The Shelf is what makes the dock worth a window, so the folders are read before it is on
        // screen rather than into a visible strip that is still empty.
        _settings.Current.Dock.IsVisible = true;
        var log = new List<string>();
        _host.Log = log;
        _shelf.Log = log;
        using var service = Create();

        await service.RestoreAsync();

        Assert.Equal(["shelf:start", "host:prepare", "host:show"], log);
    }

    [Fact]
    public async Task EnsureVisible_ShowsADockTheUserHadSwitchedOff()
    {
        _settings.Current.Dock.IsVisible = false;
        using var service = Create();
        await service.RestoreAsync();

        var shown = await service.EnsureVisibleAsync();

        Assert.True(shown);
        Assert.True(service.IsVisible);
        Assert.Equal(1, _host.ShowCount);
    }

    [Fact]
    public async Task Release_GivesTheDockBackToWhatTheUserAskedFor()
    {
        _settings.Current.Dock.IsVisible = false;
        using var service = Create();
        await service.RestoreAsync();
        await service.EnsureVisibleAsync();
        var hidesBefore = _host.HideCount;

        var stillUp = await service.ReleaseAsync();

        Assert.True(stillUp);
        Assert.False(service.IsVisible);
        Assert.Equal(hidesBefore + 1, _host.HideCount);
    }

    [Fact]
    public async Task Release_LeavesUpADockTheUserWanted()
    {
        _settings.Current.Dock.IsVisible = true;
        using var service = Create();
        await service.RestoreAsync();

        await service.EnsureVisibleAsync();
        await service.ReleaseAsync();

        // Nothing flickered on the way through: the window was already showing and stayed showing.
        Assert.True(service.IsVisible);
        Assert.Equal(1, _host.ShowCount);
        Assert.Equal(0, _host.HideCount);
    }

    [Fact]
    public async Task SetVisible_PersistsTheChoiceAndAppliesIt()
    {
        _settings.Current.Dock.IsVisible = true;
        using var service = Create();
        await service.RestoreAsync();
        var changes = new List<bool>();
        service.VisibilityChanged += (_, visible) => changes.Add(visible);

        var hidden = await service.SetVisibleAsync(false);

        Assert.True(hidden);
        Assert.False(_settings.Current.Dock.IsVisible);
        Assert.True(_settings.SaveCount > 0);
        Assert.Equal([false], changes);

        await service.SetVisibleAsync(true);

        Assert.True(_settings.Current.Dock.IsVisible);
        Assert.True(service.IsVisible);
        Assert.Equal([false, true], changes);
    }

    [Fact]
    public async Task SetVisible_ToWhatIsAlreadyTrue_DoesNotSaveOrDisturbTheWindow()
    {
        _settings.Current.Dock.IsVisible = true;
        using var service = Create();
        await service.RestoreAsync();
        var changes = new List<bool>();
        service.VisibilityChanged += (_, visible) => changes.Add(visible);

        await service.SetVisibleAsync(true);

        Assert.Equal(0, _settings.SaveCount);
        Assert.Empty(changes);
        Assert.Equal(1, _host.ShowCount);
    }

    [Fact]
    public async Task WhenTheSettingCannotBeSaved_TheDockIsLeftAlone()
    {
        _settings.Current.Dock.IsVisible = true;
        using var service = Create();
        await service.RestoreAsync();
        _settings.FailSave = true;

        var result = await service.SetVisibleAsync(false);

        Assert.False(result);
        Assert.Equal(0, _host.HideCount);
        Assert.True(service.IsVisible);
    }

    [Fact]
    public async Task WhenTheWindowCannotBeShown_ItIsReportedAndCanBeRetried()
    {
        _settings.Current.Dock.IsVisible = true;
        _host.FailShow = true;
        using var service = Create();

        Assert.False(await service.RestoreAsync());
        Assert.False(service.IsVisible);

        // A window that would not open is not remembered as open, so the next attempt really tries.
        _host.FailShow = false;
        Assert.True(await service.SetVisibleAsync(true));
        Assert.True(service.IsVisible);
    }

    [Fact]
    public void IsAvailable_IsWhateverTheWindowSays()
    {
        _host.IsAvailable = false;
        using var service = Create();

        Assert.False(service.IsAvailable);
    }

    [Fact]
    public async Task Shutdown_ClosesTheWindowRatherThanHidingIt()
    {
        // Hidden is still open, and an open window is what keeps the process alive once the main window
        // has gone: quitting has to close the dock's window for real.
        _settings.Current.Dock.IsVisible = true;
        using var service = Create();
        await service.RestoreAsync();

        Assert.True(await service.ShutdownAsync());

        Assert.Equal(1, _host.CloseCount);
        Assert.Equal(0, _host.HideCount);
        Assert.False(service.IsVisible);
    }

    [Fact]
    public async Task Shutdown_IsIdempotent()
    {
        _settings.Current.Dock.IsVisible = true;
        using var service = Create();
        await service.RestoreAsync();

        await service.ShutdownAsync();
        await service.ShutdownAsync();

        Assert.Equal(1, _host.CloseCount);
    }

    [Fact]
    public async Task AfterShutdown_NothingPutsTheWindowBackUp()
    {
        // The tray can be asked to show the dock while the app is on its way out; a window that came
        // back here would outlive the window the user closed.
        _settings.Current.Dock.IsVisible = true;
        using var service = Create();
        await service.RestoreAsync();
        await service.ShutdownAsync();
        var showsBefore = _host.ShowCount;

        Assert.False(await service.EnsureVisibleAsync());
        Assert.False(await service.RestoreAsync());

        Assert.Equal(showsBefore, _host.ShowCount);
    }

    [Fact]
    public async Task Shutdown_ReportsAWindowThatWouldNotClose()
    {
        _settings.Current.Dock.IsVisible = true;
        _host.FailClose = true;
        using var service = Create();
        await service.RestoreAsync();

        Assert.False(await service.ShutdownAsync());
    }

    private DockExperienceService Create() =>
        new(_host, _shelf, _settings, NullLogger<DockExperienceService>.Instance);

    private sealed class FakeDockHost : IDesktopDockHost
    {
        public bool IsAvailable { get; set; } = true;

        public bool FailShow { get; set; }

        public bool FailClose { get; set; }

        public int ShowCount { get; private set; }

        public int HideCount { get; private set; }

        public int CloseCount { get; private set; }

        public List<string>? Log { get; set; }

        public Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            Log?.Add("host:prepare");
            return Task.CompletedTask;
        }

        public Task ShowAsync(CancellationToken cancellationToken = default)
        {
            Log?.Add("host:show");
            if (FailShow)
            {
                throw new InvalidOperationException("the window would not open");
            }

            ShowCount++;
            return Task.CompletedTask;
        }

        public Task HideAsync(CancellationToken cancellationToken = default)
        {
            Log?.Add("host:hide");
            HideCount++;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            Log?.Add("host:close");
            if (FailClose)
            {
                throw new InvalidOperationException("the window would not close");
            }

            CloseCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShelfService : IDesktopShelfService
    {
        public DesktopShelfSnapshot Snapshot { get; } = DesktopShelfSnapshot.Empty;

        public event EventHandler<DesktopShelfSnapshot>? Changed;

        public List<string>? Log { get; set; }

        public Task<DesktopShelfSnapshot> StartAsync(CancellationToken cancellationToken = default)
        {
            Log?.Add("shelf:start");
            Changed?.Invoke(this, Snapshot);
            return Task.FromResult(Snapshot);
        }

        public Task<DesktopShelfSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task OpenAsync(DesktopShelfItem item, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public event EventHandler<AppSettings>? SettingsChanged;

        public int SaveCount { get; private set; }

        public bool FailSave { get; set; }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return FailSave ? Task.FromException(new IOException("the settings file is not writable")) : Task.CompletedTask;
        }

        public void Update(Action<AppSettings> mutate)
        {
            mutate(Current);
            SettingsChanged?.Invoke(this, Current);
        }
    }
}
