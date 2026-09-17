using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class DesktopExperienceServiceTests
{
    [Fact]
    public async Task ApplyMuralis_HidesTheIconsAndPersistsTheMode()
    {
        var phase3 = new FakeDesktopModeService();
        var clean = new FakeCleanDesktop(true);
        var settings = new FakeSettingsService();
        var service = new DesktopExperienceService(phase3, clean, settings);

        var status = await service.ApplyAsync(DesktopExperienceMode.Muralis);

        Assert.Equal(DesktopExperienceMode.Muralis, status.Mode);
        Assert.True(status.IsMuralis);
        Assert.True(clean.IsNativeDesktopHidden);
        Assert.Equal(DesktopExperienceMode.Muralis, settings.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task ApplyMuralis_WhenUnavailable_FallsBackAndPersistsNative()
    {
        var phase3 = new FakeDesktopModeService();
        var settings = new FakeSettingsService();
        var service = new DesktopExperienceService(phase3, new FakeCleanDesktop(false), settings);

        var status = await service.ApplyAsync(DesktopExperienceMode.Muralis);

        Assert.Equal(DesktopExperienceMode.Native, status.Mode);
        Assert.True(status.HasError);
        Assert.Equal(DesktopMode.Native, phase3.LastApplied);
        Assert.Equal(DesktopExperienceMode.Native, settings.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task ApplyMuralis_WhenActivationFails_FallsBackAndPersistsNative()
    {
        var settings = new FakeSettingsService();
        var clean = new FakeCleanDesktop(true) { FailActivation = true };
        var service = new DesktopExperienceService(new FakeDesktopModeService(), clean, settings);

        var status = await service.ApplyAsync(DesktopExperienceMode.Muralis);

        Assert.Equal(DesktopExperienceMode.Native, status.Mode);
        Assert.Equal(DesktopExperienceMode.Native, settings.Current.DesktopExperience.Mode);
        Assert.True(status.HasError);
        Assert.False(clean.IsNativeDesktopHidden);
    }

    [Fact]
    public async Task ApplyMuralis_WhenTheDesktopStillOwesAGiveBack_PersistsNative()
    {
        // A desktop that cannot be handed back is not a desktop to hide icons on, so the mode the user
        // asked for is refused rather than applied on top of an owed recovery.
        var phase3 = new FakeDesktopModeService { FailNative = true };
        var clean = new FakeCleanDesktop(true);
        var settings = new FakeSettingsService();
        var service = new DesktopExperienceService(phase3, clean, settings);

        var status = await service.ApplyAsync(DesktopExperienceMode.Muralis);

        Assert.Equal(DesktopExperienceMode.Native, status.Mode);
        Assert.False(clean.IsNativeDesktopHidden);
        Assert.Equal(DesktopExperienceMode.Native, settings.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task LeavingMuralis_ReturnsTheIconsBeforeAnythingElse()
    {
        var sequence = new List<string>();
        var phase3 = new FakeDesktopModeService(sequence);
        var clean = new FakeCleanDesktop(true, sequence);
        var settings = new FakeSettingsService();
        settings.Current.DesktopExperience.Mode = DesktopExperienceMode.Muralis;
        var service = new DesktopExperienceService(phase3, clean, settings);

        await service.ApplyAsync(DesktopExperienceMode.Muralis);
        sequence.Clear();

        var status = await service.ApplyAsync(DesktopExperienceMode.Native);

        Assert.Equal(DesktopExperienceMode.Native, status.Mode);
        Assert.False(clean.IsNativeDesktopHidden);
        Assert.Equal("clean:deactivate", sequence[0]);
        Assert.Equal(DesktopExperienceMode.Native, settings.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task ApplyNative_LeavesExplorerAndCanvasNative()
    {
        var phase3 = new FakeDesktopModeService();
        var settings = new FakeSettingsService();
        var service = new DesktopExperienceService(phase3, new FakeCleanDesktop(true), settings);

        var status = await service.ApplyAsync(DesktopExperienceMode.Native);

        Assert.Equal(DesktopExperienceMode.Native, status.Mode);
        Assert.Equal(DesktopMode.Native, status.Phase3.EffectiveMode);
        Assert.False(status.Phase3.IsShowingCanvas);
    }

    [Fact]
    public async Task Restore_WithASavedMuralisMode_BringsItBackWithoutRewritingTheFile()
    {
        var clean = new FakeCleanDesktop(true);
        var settings = new FakeSettingsService();
        settings.Current.DesktopExperience.Mode = DesktopExperienceMode.Muralis;
        var service = new DesktopExperienceService(new FakeDesktopModeService(), clean, settings);

        var status = await service.RestoreAsync();

        Assert.Equal(DesktopExperienceMode.Muralis, status.Mode);
        Assert.True(clean.IsNativeDesktopHidden);
        Assert.Equal(0, settings.UpdateCount);
    }

    [Fact]
    public async Task Restore_WhenAPhase3DesktopStillOwesAGiveBack_HandsItBackAndForgetsIt()
    {
        // An older file can still name a withdrawn takeover; landing in it is never allowed, and what is
        // remembered afterwards is the native desktop that is really in place.
        var phase3 = new FakeDesktopModeService();
        phase3.Seed(TakenOver());
        var settings = new FakeSettingsService();
        settings.Current.DesktopExperience.Mode = DesktopExperienceMode.Muralis;
        var service = new DesktopExperienceService(phase3, new FakeCleanDesktop(true), settings);

        var status = await service.RestoreAsync();

        Assert.Equal(DesktopExperienceMode.Native, status.Mode);
        Assert.Equal(DesktopMode.Native, phase3.LastApplied);
        Assert.Equal(DesktopExperienceMode.Native, settings.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task Restore_WhenTheFilePredatesTheTwoModes_UpgradesTheSchema()
    {
        var settings = new FakeSettingsService();
        settings.Current.SchemaVersion = 1;
        var service = new DesktopExperienceService(new FakeDesktopModeService(), new FakeCleanDesktop(true), settings);

        await service.RestoreAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.Current.SchemaVersion);
    }

    [Fact]
    public async Task Phase3MovingOnItsOwn_DoesNotRewriteTheUsersChoice()
    {
        // The retired layer handing the desktop back is not a mode change the user made, so the saved
        // preference has to survive it — otherwise Muralis Mode would never last a quit.
        var phase3 = new FakeDesktopModeService();
        var settings = new FakeSettingsService();
        settings.Current.DesktopExperience.Mode = DesktopExperienceMode.Muralis;
        var service = new DesktopExperienceService(phase3, new FakeCleanDesktop(true), settings);

        await service.ApplyAsync(DesktopExperienceMode.Muralis);
        var updatesAfterApplying = settings.UpdateCount;

        phase3.Seed(DesktopModeStatus.Native);

        Assert.Equal(updatesAfterApplying, settings.UpdateCount);
        Assert.Equal(DesktopExperienceMode.Muralis, settings.Current.DesktopExperience.Mode);
    }

    private static DesktopModeStatus TakenOver() => new(
        DesktopMode.Takeover,
        DesktopTakeoverState.Muralis,
        CanvasPrototypeState.Active,
        null);

    private sealed class FakeDesktopModeService(List<string>? sequence = null) : IDesktopModeService
    {
        public DesktopModeStatus Status { get; private set; } = DesktopModeStatus.Native;

        public DesktopModeStatus RestoreResult { get; init; } = DesktopModeStatus.Native;

        public DesktopMode LastApplied { get; private set; } = DesktopMode.Native;

        public bool FailNative { get; init; }

        public int RestoreCount { get; private set; }

        public event EventHandler<DesktopModeStatus>? Changed;

        /// <summary>Puts the frozen service in a state the app has to react to.</summary>
        public void Seed(DesktopModeStatus status)
        {
            Status = status;
            Changed?.Invoke(this, Status);
        }

        public Task<DesktopModeStatus> RestoreAsync(CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            Status = RestoreResult;
            return Task.FromResult(Status);
        }

        public Task<DesktopModeStatus> ApplyAsync(DesktopMode mode, CancellationToken cancellationToken = default)
        {
            sequence?.Add($"phase3:{mode}");
            LastApplied = mode;
            Status = mode switch
            {
                DesktopMode.Native when FailNative => new DesktopModeStatus(
                    DesktopMode.Native,
                    DesktopTakeoverState.RecoveryRequired,
                    CanvasPrototypeState.Disabled,
                    "The native desktop could not be given back."),
                DesktopMode.Takeover => TakenOver(),
                DesktopMode.Preview => new DesktopModeStatus(
                    DesktopMode.Preview,
                    DesktopTakeoverState.Native,
                    CanvasPrototypeState.Active,
                    null),
                _ => DesktopModeStatus.Native,
            };
            Changed?.Invoke(this, Status);
            return Task.FromResult(Status);
        }

        public Task<DesktopModeStatus> RestoreNativeDesktopAsync(CancellationToken cancellationToken = default) =>
            ApplyAsync(DesktopMode.Native, cancellationToken);
    }

    private sealed class FakeCleanDesktop(bool available, List<string>? sequence = null) : ICleanDesktopPresentation
    {
        public bool IsAvailable { get; } = available;

        public bool IsNativeDesktopHidden { get; private set; }

        public bool FailActivation { get; init; }

        public Task<CleanDesktopPresentationResult> ActivateAsync(CancellationToken cancellationToken = default)
        {
            sequence?.Add("clean:activate");
            IsNativeDesktopHidden = IsAvailable && !FailActivation;
            return Task.FromResult(IsNativeDesktopHidden
                ? CleanDesktopPresentationResult.Active
                : new CleanDesktopPresentationResult(false, "Unavailable or failed"));
        }

        public Task<CleanDesktopPresentationResult> DeactivateAsync(CancellationToken cancellationToken = default)
        {
            sequence?.Add("clean:deactivate");
            IsNativeDesktopHidden = false;
            return Task.FromResult(CleanDesktopPresentationResult.Inactive);
        }

        public Task<CleanDesktopPresentationResult> SynchronizeStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CleanDesktopPresentationResult(IsNativeDesktopHidden));

        public Task<CleanDesktopPresentationResult> RecoverIfNeededAsync(CancellationToken cancellationToken = default) =>
            DeactivateAsync(cancellationToken);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public int UpdateCount { get; private set; }

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Update(Action<AppSettings> mutate)
        {
            UpdateCount++;
            mutate(Current);
            SettingsChanged?.Invoke(this, Current);
        }
    }
}
