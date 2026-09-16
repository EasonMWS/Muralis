using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Dock;
using Muralis.Core.Models;
using Muralis.Desktop.Modes;
using Xunit;

namespace Muralis.Desktop.Tests.Modes;

/// <summary>
/// The order the three desktop modes are moved in, and what is remembered when a move does not finish.
/// This is the part that decides whether a failure leaves the user with a desktop or with nothing to
/// click, so it is pinned here rather than only measured live: the canvas is mounted and verified
/// before the icons may be hidden, the desktop is given back before the canvas comes off, and only
/// what really happened is written down.
/// </summary>
public sealed class DesktopModeServiceTests
{
    private readonly List<string> _steps = [];
    private readonly FakeCanvas _canvas;
    private readonly FakeTakeover _takeover;
    private readonly FakeSync _sync;

    public DesktopModeServiceTests()
    {
        _canvas = new FakeCanvas(_steps);
        _takeover = new FakeTakeover(_steps);
        _sync = new FakeSync(_steps);
    }

    [Fact]
    public async Task ATakeover_IsNeverAskedForWhileTheCanvasIsNotShowing()
    {
        _canvas.EnableResult = new CanvasPrototypeStatus(
            CanvasPrototypeState.Failed, 0, "the desktop layer would not start");

        var status = await Service().ApplyAsync(DesktopMode.Takeover);

        // Hiding the icons on a desktop with nothing else to click would be taking something away
        // rather than offering a replacement, so the takeover is not asked for at all — and because
        // nothing was achieved, nothing is remembered either.
        Assert.Equal(0, _takeover.TakeCalls);
        Assert.Equal(DesktopMode.Native, status.Mode);
        Assert.Equal(DesktopTakeoverState.Native, status.Takeover);
        Assert.False(status.OwnsTheDesktop);
        Assert.Contains("desktop layer", status.Error);
        Assert.Equal(0, _canvas.PersistCalls);
    }

    [Fact]
    public async Task ATakeoverThatCannotHideTheIcons_IsRememberedAsAPreview()
    {
        _takeover.TakeResult = DesktopTakeoverOutcome.AlreadyNative("the shell refused the view flags");

        var status = await Service().ApplyAsync(DesktopMode.Takeover);

        // The canvas is on and the icons are the user's: that is a preview, it is what really
        // happened, and it is what is saved — the takeover's own reason is reported beside it so the
        // downgrade is not silent.
        Assert.Equal(DesktopMode.Preview, status.Mode);
        Assert.Equal(DesktopMode.Preview, _canvas.Options.Mode);
        Assert.Contains("refused", status.Error);
    }

    [Fact]
    public async Task ADesktopThatOwesAGiveBack_IsGivenBackBeforeAnyOtherModeIsBuilt()
    {
        _takeover.State = DesktopTakeoverState.RecoveryRequired;

        var status = await Service().ApplyAsync(DesktopMode.Preview);

        // Any mode but a takeover means the native desktop has to be the user's again, and that is
        // paid before the canvas is mounted — so the desktop is never layered over while a give-back
        // is still owed.
        Assert.Equal(["takeover:release", "canvas:enable"], _steps.Take(2));
        Assert.Equal(DesktopMode.Preview, status.Mode);
        Assert.Equal(DesktopTakeoverState.Native, status.Takeover);
    }

    [Fact]
    public async Task ADesktopThatOwesAGiveBack_RefusesANewTakeoverRatherThanHidingIconsAgain()
    {
        _takeover.State = DesktopTakeoverState.RecoveryRequired;
        _takeover.Problem = "the icons could not be verified as back";

        var status = await Service().ApplyAsync(DesktopMode.Takeover);

        // The icons may still be hidden on a desktop nobody can vouch for, so a new takeover is
        // refused outright rather than releasing and re-hiding: the emergency give-back is the way out.
        Assert.Empty(_steps);
        Assert.Equal(0, _takeover.ReleaseCalls);
        Assert.Equal(0, _canvas.EnableCalls);
        Assert.Equal(0, _takeover.TakeCalls);
        Assert.Equal(0, _canvas.PersistCalls);
        Assert.Equal(DesktopTakeoverState.RecoveryRequired, status.Takeover);
        Assert.True(status.NeedsRecovery);
        Assert.False(status.OwnsTheDesktop);
        Assert.Contains("verified", status.Error);
    }

    [Fact]
    public async Task AGiveBackThatFails_RefusesTheModeInsteadOfHidingIconsOnAnUnknownDesktop()
    {
        _takeover.State = DesktopTakeoverState.RecoveryRequired;
        _takeover.ReleaseResult = new DesktopTakeoverOutcome(
            DesktopTakeoverState.RecoveryRequired, DesktopIconStrategy.None, null, "the icons are still hidden");

        var status = await Service().ApplyAsync(DesktopMode.Preview);

        // The give-back did not come off, so nothing else is even attempted: the desktop stays owed a
        // recovery, the canvas is not mounted over it, and the mode is not written down as if it were.
        Assert.Equal(1, _takeover.ReleaseCalls);
        Assert.Equal(0, _canvas.EnableCalls);
        Assert.Equal(0, _takeover.TakeCalls);
        Assert.Equal(0, _canvas.PersistCalls);
        Assert.Equal(DesktopTakeoverState.RecoveryRequired, status.Takeover);
        Assert.True(status.NeedsRecovery);
        Assert.Contains("still hidden", status.Error);
    }

    [Fact]
    public async Task GivingTheDesktopBack_NeedsNoCanvasAndNoLayoutAndNoMode()
    {
        var service = Service();

        await service.ApplyAsync(DesktopMode.Takeover);

        // The canvas cannot even be taken off the desktop and every read of the layout fails: the
        // emergency give-back still has to hand the user their desktop back, and it does, because
        // nothing on that path reads either of them.
        _canvas.ThrowOnDisable = true;
        _sync.ThrowOnEverything = true;

        var status = await service.RestoreNativeDesktopAsync();

        Assert.Equal(1, _takeover.EmergencyCalls);
        Assert.Equal(DesktopTakeoverState.Native, status.Takeover);
        Assert.False(status.NeedsRecovery);
        Assert.Equal(DesktopMode.Native, status.Mode);

        // The canvas' own failure is reported, and it does not undo the give-back.
        Assert.True(status.HasError);
    }

    [Fact]
    public async Task SwitchingBetweenPreviewAndTakeover_KeepsTheOneCanvasAndTheOneAdoption()
    {
        var service = Service();

        await service.ApplyAsync(DesktopMode.Preview);
        var status = await service.ApplyAsync(DesktopMode.Takeover);

        // One set of items and one look at the user's own desktop: switching between the two showing
        // modes changes which layer answers the pointer, not what is on the desktop. The mount is
        // asked for again — the canvas service answers that it is already on — and nothing is taken
        // off, so no item is ever on twice and the user's desktop is not read a second time.
        Assert.Equal(1, _sync.SyncCalls);
        Assert.Equal(1, _sync.StartCalls);
        Assert.Equal(1, _takeover.TakeCalls);
        Assert.Equal(0, _canvas.DisableCalls);
        Assert.Equal(DesktopMode.Takeover, status.Mode);
        Assert.True(status.OwnsTheDesktop);
        Assert.True(_sync.IsWatching);
    }

    [Fact]
    public async Task TakingTheDesktopBack_StopsWatchingTheUsersFolders()
    {
        var service = Service();

        await service.ApplyAsync(DesktopMode.Preview);
        await service.ApplyAsync(DesktopMode.Native);

        Assert.Equal(1, _sync.StartCalls);
        Assert.Equal(1, _sync.StopCalls);
        Assert.False(_sync.IsWatching);
        Assert.Equal(1, _canvas.DisableCalls);
        Assert.Equal(DesktopMode.Native, service.Status.EffectiveMode);
    }

    [Fact]
    public async Task AnUnsupportedMode_IsRefusedAndNothingIsTouched()
    {
        var status = await Service().ApplyAsync((DesktopMode)99);

        Assert.Equal(0, _canvas.EnableCalls);
        Assert.Equal(0, _takeover.TakeCalls);
        Assert.Equal(0, _canvas.PersistCalls);
        Assert.Contains("not a desktop mode", status.Error);
    }

    [Fact]
    public async Task TwentyRoundsOfTakingOverAndGivingBack_LeaveTheDesktopNative()
    {
        var service = Service();

        for (var round = 0; round < 20; round++)
        {
            var taken = await service.ApplyAsync(DesktopMode.Takeover);
            Assert.True(taken.OwnsTheDesktop);

            var given = await service.RestoreNativeDesktopAsync();
            Assert.Equal(DesktopMode.Native, given.Mode);
            Assert.Equal(DesktopTakeoverState.Native, given.Takeover);
        }

        // Twenty rounds in and out, and the desktop is still the user's: nothing accumulated, nothing
        // was left half-done, and the last thing written down is the native mode.
        Assert.Equal(20, _takeover.TakeCalls);
        Assert.Equal(20, _takeover.EmergencyCalls);
        Assert.Equal(20, _canvas.EnableCalls);
        Assert.Equal(20, _canvas.DisableCalls);
        Assert.Equal(DesktopTakeoverState.Native, service.Status.Takeover);
        Assert.Equal(DesktopMode.Native, _canvas.Options.Mode);
        Assert.False(_sync.IsWatching);
    }

    private DesktopModeService Service() =>
        new(NullLogger<DesktopModeService>.Instance, _canvas, _takeover, _sync);

    private sealed class FakeCanvas : IDesktopCanvasService
    {
        private readonly List<string> _steps;
        private CanvasPrototypeState _state = CanvasPrototypeState.Disabled;

        internal FakeCanvas(List<string> steps) => _steps = steps;

        /// <summary>What the next mount reports; the default is a canvas that really came up.</summary>
        internal CanvasPrototypeStatus EnableResult { get; set; } = new(CanvasPrototypeState.Active);

        /// <summary>Makes the canvas fail to come off the desktop, as a lost layer would.</summary>
        internal bool ThrowOnDisable { get; set; }

        /// <summary>The document as the canvas service holds it, so a save can be seen.</summary>
        internal DesktopTakeoverOptions Options { get; } = new();

        internal int EnableCalls { get; private set; }

        internal int DisableCalls { get; private set; }

        internal int PersistCalls { get; private set; }

        public CanvasPrototypeStatus Status => new(_state);

        public event EventHandler<CanvasPrototypeStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public CanvasDiagnosticsSnapshot? Diagnostics => null;

        public Task<CanvasPrototypeStatus> EnableAsync(CancellationToken cancellationToken = default)
        {
            EnableCalls++;
            _steps.Add("canvas:enable");
            _state = EnableResult.State;
            return Task.FromResult(EnableResult);
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            DisableCalls++;
            _steps.Add("canvas:disable");

            if (ThrowOnDisable)
            {
                throw new InvalidOperationException("the canvas would not come off the desktop");
            }

            _state = CanvasPrototypeState.Disabled;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DesktopItem>> GetItemsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DesktopItem>>([]);

        public Task<bool> AddItemAsync(DesktopItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RemoveItemAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<DesktopAdoptionResult> AdoptAsync(
            DesktopAdoptionPlan plan,
            CancellationToken cancellationToken = default) => Task.FromResult(DesktopAdoptionResult.None);

        public Task<DockOptions> GetDockAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockOptions());

        public Task<bool> UpdateDockAsync(DockOptions dock, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<DesktopTakeoverOptions> GetTakeoverOptionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Options);

        public Task UpdateTakeoverOptionsAsync(
            DesktopTakeoverOptions options,
            CancellationToken cancellationToken = default)
        {
            PersistCalls++;
            _steps.Add($"canvas:persist {options.Mode}");
            return Task.CompletedTask;
        }

        public Task SuspendInteractionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTakeover : IDesktopTakeoverService
    {
        private readonly List<string> _steps;

        internal FakeTakeover(List<string> steps) => _steps = steps;

        internal DesktopTakeoverOutcome TakeResult { get; set; } = new(
            DesktopTakeoverState.Muralis, DesktopIconStrategy.ShellViewFlags, null, null);

        internal DesktopTakeoverOutcome ReleaseResult { get; set; } = DesktopTakeoverOutcome.AlreadyNative();

        internal DesktopTakeoverOutcome RestoreResult { get; set; } = DesktopTakeoverOutcome.AlreadyNative();

        internal int TakeCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal int EmergencyCalls { get; private set; }

        public string? Problem { get; set; }

        public DesktopTakeoverState State { get; set; } = DesktopTakeoverState.Native;

        public event EventHandler<DesktopTakeoverOutcome>? Changed
        {
            add { }
            remove { }
        }

        public Task<DesktopTakeoverOutcome> TakeAsync(CancellationToken cancellationToken = default)
        {
            TakeCalls++;
            _steps.Add("takeover:take");
            State = TakeResult.State;
            return Task.FromResult(TakeResult);
        }

        public Task<DesktopTakeoverOutcome> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            _steps.Add("takeover:release");
            State = ReleaseResult.State;
            return Task.FromResult(ReleaseResult);
        }

        public Task<DesktopTakeoverOutcome> RestoreNativeDesktopAsync(CancellationToken cancellationToken = default)
        {
            EmergencyCalls++;
            _steps.Add("takeover:restore");
            State = RestoreResult.State;
            return Task.FromResult(RestoreResult);
        }

        public Task<DesktopTakeoverOutcome> RecoverIfNeededAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DesktopTakeoverOutcome.AlreadyNative());

        public Task<NativeDesktopVisualState> ReadNativeStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(NativeDesktopVisualState.AssumedVisible);
    }

    private sealed class FakeSync : IDesktopItemSyncService
    {
        private readonly List<string> _steps;

        internal FakeSync(List<string> steps) => _steps = steps;

        /// <summary>Makes every read fail, as an unreadable layout document would.</summary>
        internal bool ThrowOnEverything { get; set; }

        internal int SyncCalls { get; private set; }

        internal int StartCalls { get; private set; }

        internal int StopCalls { get; private set; }

        public bool IsWatching { get; private set; }

        public Task<DesktopAdoptionPlan> PreviewAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnEverything)
            {
                throw new InvalidOperationException("the layout could not be read");
            }

            return Task.FromResult(DesktopAdoptionPlan.None);
        }

        public Task<DesktopAdoptionResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            _steps.Add("sync:sync");

            if (ThrowOnEverything)
            {
                throw new InvalidOperationException("the layout could not be read");
            }

            return Task.FromResult(DesktopAdoptionResult.None);
        }

        public void StartWatching()
        {
            StartCalls++;
            _steps.Add("sync:watch");
            IsWatching = true;
        }

        public void StopWatching()
        {
            StopCalls++;
            _steps.Add("sync:unwatch");
            IsWatching = false;

            if (ThrowOnEverything)
            {
                throw new InvalidOperationException("the watch could not be stopped");
            }
        }
    }
}
