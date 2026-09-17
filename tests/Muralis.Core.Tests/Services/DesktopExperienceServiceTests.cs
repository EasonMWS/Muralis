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
    public async Task ApplyCleanDesktop_WhenPresentationUnavailable_FallsBackAndPersistsNative()
    {
        var phase3 = new FakeDesktopModeService();
        var settings = new FakeSettingsService();
        var service = new DesktopExperienceService(phase3, new FakeCleanDesktop(false), settings);

        var status = await service.ApplyAsync(DesktopExperienceMode.CleanDesktop);

        Assert.Equal(DesktopExperienceMode.Native, status.Mode);
        Assert.True(status.HasError);
        Assert.Equal(DesktopMode.Native, phase3.LastApplied);
        Assert.Equal(DesktopExperienceMode.Native, settings.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task ApplyFullTakeover_DelegatesToFrozenPhase3Service()
    {
        var phase3 = new FakeDesktopModeService();
        var settings = new FakeSettingsService();
        var service = new DesktopExperienceService(phase3, new FakeCleanDesktop(false), settings);

        var status = await service.ApplyAsync(DesktopExperienceMode.FullTakeoverExperimental);

        Assert.Equal(DesktopMode.Takeover, phase3.LastApplied);
        Assert.Equal(DesktopExperienceMode.FullTakeoverExperimental, status.Mode);
        Assert.True(status.Phase3.OwnsTheDesktop);
        Assert.Equal(DesktopExperienceMode.FullTakeoverExperimental, settings.Current.DesktopExperience.Mode);
    }

    [Fact]
    public async Task Restore_OldTakeover_MigratesWithoutDroppingCompatibility()
    {
        var phase3 = new FakeDesktopModeService
        {
            RestoreResult = TakenOver(),
        };
        var settings = new FakeSettingsService();
        settings.Current.SchemaVersion = 1;
        var service = new DesktopExperienceService(phase3, new FakeCleanDesktop(false), settings);

        var status = await service.RestoreAsync();

        Assert.Equal(1, phase3.RestoreCount);
        Assert.Equal(DesktopExperienceMode.FullTakeoverExperimental, status.Mode);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.Current.SchemaVersion);
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
    public async Task CleanDesktopAndFullTakeover_AreMutuallyExclusive()
    {
        var sequence = new List<string>();
        var phase3 = new FakeDesktopModeService(sequence);
        var clean = new FakeCleanDesktop(true, sequence);
        var service = new DesktopExperienceService(phase3, clean, new FakeSettingsService());

        Assert.Equal(DesktopExperienceMode.CleanDesktop, (await service.ApplyAsync(DesktopExperienceMode.CleanDesktop)).Mode);
        Assert.Equal(DesktopExperienceMode.FullTakeoverExperimental, (await service.ApplyAsync(DesktopExperienceMode.FullTakeoverExperimental)).Mode);

        Assert.True(sequence.IndexOf("clean:deactivate") < sequence.LastIndexOf("phase3:Takeover"));
        Assert.False(clean.IsNativeDesktopHidden);
    }

    [Fact]
    public async Task CleanDesktopActivationFailure_FallsBackAndPersistsNative()
    {
        var settings = new FakeSettingsService();
        var clean = new FakeCleanDesktop(true) { FailActivation = true };
        var service = new DesktopExperienceService(new FakeDesktopModeService(), clean, settings);

        var status = await service.ApplyAsync(DesktopExperienceMode.CleanDesktop);

        Assert.Equal(DesktopExperienceMode.Native, status.Mode);
        Assert.Equal(DesktopExperienceMode.Native, settings.Current.DesktopExperience.Mode);
        Assert.True(status.HasError);
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

        public int RestoreCount { get; private set; }

        public event EventHandler<DesktopModeStatus>? Changed;

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

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Update(Action<AppSettings> mutate)
        {
            mutate(Current);
            SettingsChanged?.Invoke(this, Current);
        }
    }
}
