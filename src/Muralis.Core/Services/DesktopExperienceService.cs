using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>
/// Coordinates product modes without changing the Phase 3 takeover implementation. Clean Desktop stays
/// fails safely back to Native unless its independent presentation reports that its complete lifecycle is available.
/// </summary>
public sealed class DesktopExperienceService : IDesktopExperienceService, IDisposable
{
    private readonly IDesktopModeService _phase3;
    private readonly ICleanDesktopPresentation _cleanDesktop;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public DesktopExperienceService(
        IDesktopModeService phase3,
        ICleanDesktopPresentation cleanDesktop,
        ISettingsService settings)
    {
        _phase3 = phase3 ?? throw new ArgumentNullException(nameof(phase3));
        _cleanDesktop = cleanDesktop ?? throw new ArgumentNullException(nameof(cleanDesktop));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Status = new DesktopExperienceStatus(
            DesktopExperienceMode.Native,
            cleanDesktop.IsAvailable,
            phase3.Status);
        _phase3.Changed += OnPhase3Changed;
    }

    public DesktopExperienceStatus Status { get; private set; }

    public event EventHandler<DesktopExperienceStatus>? Changed;

    public async Task<DesktopExperienceStatus> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Settings written before Phase 4A stored the Phase 3 choice in the canvas document. Restore
            // it once so an existing takeover remains compatible, then migrate to the product-level mode.
            if (_settings.Current.SchemaVersion < AppSettings.CurrentSchemaVersion)
            {
                var legacy = await _phase3.RestoreAsync(cancellationToken).ConfigureAwait(false);
                if (legacy.EffectiveMode == DesktopMode.Takeover && legacy.OwnsTheDesktop)
                {
                    return PublishAndPersist(DesktopExperienceMode.FullTakeoverExperimental, legacy, legacy.Error);
                }

                // Preview was a Phase 3 development state, not a product-level experience. A migration
                // ends it explicitly so the new safe Native default describes what is actually visible.
                if (legacy.EffectiveMode != DesktopMode.Native || legacy.IsShowingCanvas)
                {
                    legacy = await _phase3.ApplyAsync(DesktopMode.Native, cancellationToken).ConfigureAwait(false);
                }

                return PublishAndPersist(DesktopExperienceMode.Native, legacy, legacy.Error);
            }

            return await ApplyCoreAsync(_settings.Current.DesktopExperience.Mode, persist: false, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<DesktopExperienceStatus> ApplyAsync(
        DesktopExperienceMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(mode, persist: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<DesktopExperienceStatus> ApplyCoreAsync(
        DesktopExperienceMode mode,
        bool persist,
        CancellationToken cancellationToken)
    {
        if (mode == DesktopExperienceMode.CleanDesktop && !_cleanDesktop.IsAvailable)
        {
            var native = await EnsureNativeAsync(cancellationToken).ConfigureAwait(false);
            const string unavailable = "Clean Desktop is unavailable because a safe Explorer icon-visibility lifecycle could not be established.";
            return PublishAndPersist(DesktopExperienceMode.Native, native, unavailable, persist: true);
        }

        if (Status.Mode == DesktopExperienceMode.CleanDesktop && mode != DesktopExperienceMode.CleanDesktop)
        {
            var stopped = await _cleanDesktop.DeactivateAsync(cancellationToken).ConfigureAwait(false);
            if (stopped.IsActive || stopped.Error is not null)
            {
                return Publish(Status.Mode, _phase3.Status, stopped.Error ?? "Clean Desktop could not be deactivated.");
            }
        }

        switch (mode)
        {
            case DesktopExperienceMode.Native:
            {
                var native = await EnsureNativeAsync(cancellationToken).ConfigureAwait(false);
                return persist
                    ? PublishAndPersist(mode, native, native.Error)
                    : Publish(mode, native, native.Error);
            }
            case DesktopExperienceMode.CleanDesktop:
            {
                var native = await EnsureNativeAsync(cancellationToken).ConfigureAwait(false);
                if (native.EffectiveMode != DesktopMode.Native || native.NeedsRecovery)
                {
                    return PublishAndPersist(DesktopExperienceMode.Native, native, native.Error, persist: true);
                }

                var activated = await _cleanDesktop.ActivateAsync(cancellationToken).ConfigureAwait(false);
                if (!activated.IsActive)
                {
                    return PublishAndPersist(
                        DesktopExperienceMode.Native,
                        native,
                        activated.Error ?? "Clean Desktop could not be activated.",
                        persist: true);
                }

                return persist
                    ? PublishAndPersist(mode, native, activated.Error)
                    : Publish(mode, native, activated.Error);
            }
            case DesktopExperienceMode.FullTakeoverExperimental:
            {
                var takeover = await _phase3.ApplyAsync(DesktopMode.Takeover, cancellationToken).ConfigureAwait(false);
                if (takeover.EffectiveMode != DesktopMode.Takeover || !takeover.OwnsTheDesktop)
                {
                    var problem = takeover.Error ?? "The experimental takeover could not hide Explorer's desktop icons.";
                    var native = await EnsureNativeAsync(cancellationToken).ConfigureAwait(false);
                    return PublishAndPersist(DesktopExperienceMode.Native, native, problem, persist: true);
                }

                return persist
                    ? PublishAndPersist(mode, takeover, takeover.Error)
                    : Publish(mode, takeover, takeover.Error);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private Task<DesktopModeStatus> EnsureNativeAsync(CancellationToken cancellationToken) =>
        _phase3.ApplyAsync(DesktopMode.Native, cancellationToken);

    private DesktopExperienceStatus PublishAndPersist(
        DesktopExperienceMode mode,
        DesktopModeStatus phase3,
        string? error,
        bool persist = true)
    {
        if (persist)
        {
            var current = _settings.Current;
            if (current.SchemaVersion != AppSettings.CurrentSchemaVersion
                || current.DesktopExperience.Mode != mode)
            {
                _settings.Update(settings =>
                {
                    settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
                    settings.DesktopExperience.Mode = mode;
                });
            }
        }

        return Publish(mode, phase3, error);
    }

    private DesktopExperienceStatus Publish(
        DesktopExperienceMode mode,
        DesktopModeStatus phase3,
        string? error)
    {
        Status = new DesktopExperienceStatus(mode, _cleanDesktop.IsAvailable, phase3, error);
        Changed?.Invoke(this, Status);
        return Status;
    }

    /// <summary>
    /// Keeps the compatibility surface honest when the existing Phase 3 diagnostics page or tray
    /// changes the legacy service directly. A successful takeover is remembered as experimental;
    /// every other legacy state has a safe Native product-level restart policy.
    /// </summary>
    private void OnPhase3Changed(object? sender, DesktopModeStatus phase3)
    {
        var mode = phase3.EffectiveMode == DesktopMode.Takeover && phase3.OwnsTheDesktop
            ? DesktopExperienceMode.FullTakeoverExperimental
            : DesktopExperienceMode.Native;
        var error = phase3.EffectiveMode == DesktopMode.Preview
            ? "The legacy Phase 3 preview is active; it will restart as Native."
            : phase3.Error;

        PublishAndPersist(mode, phase3, error);
    }

    public void Dispose() => _phase3.Changed -= OnPhase3Changed;
}
