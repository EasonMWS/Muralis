using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>
/// The product's desktop experience: the native Windows desktop, or Muralis Mode. It owns the mode's
/// lifecycle and nothing else — hiding and restoring Explorer's icons belongs to the presentation
/// layer, and the dock and the Shelf belong to the dock's own service.
/// </summary>
/// <remarks>
/// <para>
/// The frozen Phase 3 service is kept for one job: a desktop that still owes Explorer its icons back
/// is handed back to Windows. It is never a destination, because the withdrawn takeover is not
/// re-entered and an older settings file naming it reads as Native.
/// </para>
/// <para>
/// Every failure fails open. A mode that could not be established is reported as Native, which is what
/// really is in place, so nobody is left with hidden icons and nothing to click.
/// </para>
/// </remarks>
public sealed class DesktopExperienceService : IDesktopExperienceService, IDisposable
{
    private readonly IDesktopModeService _phase3;
    private readonly ICleanDesktopPresentation _presentation;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public DesktopExperienceService(
        IDesktopModeService phase3,
        ICleanDesktopPresentation cleanDesktop,
        ISettingsService settings)
    {
        _phase3 = phase3 ?? throw new ArgumentNullException(nameof(phase3));
        _presentation = cleanDesktop ?? throw new ArgumentNullException(nameof(cleanDesktop));
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
            UpgradeTheSavedShape();

            // A desktop that still owes Explorer its icons back is given back before any mode is
            // applied, and what is remembered afterwards is Native: the withdrawn takeover is never
            // restored, whatever an older file asked for.
            var legacy = _phase3.Status;
            if (legacy.NeedsRecovery || legacy.EffectiveMode != DesktopMode.Native)
            {
                var given = await _phase3.ApplyAsync(DesktopMode.Native, cancellationToken).ConfigureAwait(false);
                return PublishAndPersist(DesktopExperienceMode.Native, given, given.Error);
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
        // Leaving Muralis Mode is one move, and it starts with the icons: the desktop is the user's
        // again before anything else is touched, so a later step that fails has nothing to answer for.
        if (Status.Mode == DesktopExperienceMode.Muralis && mode != DesktopExperienceMode.Muralis)
        {
            var stopped = await _presentation.DeactivateAsync(cancellationToken).ConfigureAwait(false);
            if (stopped.IsActive || stopped.Error is not null)
            {
                return Publish(Status.Mode, _phase3.Status, stopped.Error ?? "Muralis Mode could not be stopped.");
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
            case DesktopExperienceMode.Muralis:
            {
                if (!_presentation.IsAvailable)
                {
                    var unavailable = await EnsureNativeAsync(cancellationToken).ConfigureAwait(false);
                    return PublishAndPersist(
                        DesktopExperienceMode.Native,
                        unavailable,
                        "Muralis Mode is unavailable because Explorer's desktop icons cannot be hidden safely on this system.");
                }

                // The dock has to be up before the icons go, and the icons have to be the user's before
                // the dock is asked for: the presentation enforces the first, and this is the second.
                var native = await EnsureNativeAsync(cancellationToken).ConfigureAwait(false);
                if (native.EffectiveMode != DesktopMode.Native || native.NeedsRecovery)
                {
                    return PublishAndPersist(DesktopExperienceMode.Native, native, native.Error, persist: true);
                }

                var activated = await _presentation.ActivateAsync(cancellationToken).ConfigureAwait(false);
                if (!activated.IsActive)
                {
                    return PublishAndPersist(
                        DesktopExperienceMode.Native,
                        native,
                        activated.Error ?? "Muralis Mode could not be started.",
                        persist: true);
                }

                return persist
                    ? PublishAndPersist(mode, native, activated.Error)
                    : Publish(mode, native, activated.Error);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private Task<DesktopModeStatus> EnsureNativeAsync(CancellationToken cancellationToken) =>
        _phase3.ApplyAsync(DesktopMode.Native, cancellationToken);

    /// <summary>
    /// A file written by an older version is brought up to the current shape once, so the withdrawn
    /// mode names stop existing on disk rather than being translated every time they are read.
    /// </summary>
    private void UpgradeTheSavedShape()
    {
        if (_settings.Current.SchemaVersion == AppSettings.CurrentSchemaVersion)
        {
            return;
        }

        _settings.Update(settings => settings.SchemaVersion = AppSettings.CurrentSchemaVersion);
    }

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
        Status = new DesktopExperienceStatus(mode, _presentation.IsAvailable, phase3, error);
        Changed?.Invoke(this, Status);
        return Status;
    }

    /// <summary>
    /// Keeps the reported desktop honest when the frozen Phase 3 service moves it on its own — which,
    /// now that its interface is gone, is the desktop being handed back. The mode the user chose is
    /// left alone: what the retired layer does is not a choice the product offers.
    /// </summary>
    private void OnPhase3Changed(object? sender, DesktopModeStatus phase3) =>
        Publish(Status.Mode, phase3, phase3.Error);

    public void Dispose() => _phase3.Changed -= OnPhase3Changed;
}
