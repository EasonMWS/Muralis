using System.Text.Json;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Helpers;
using Muralis.Desktop.Interop;
using Muralis.Desktop.Shell;
using Muralis.Desktop.Takeover;

namespace Muralis.Desktop.CleanDesktop;

/// <summary>
/// Alternative presentation lifecycle for Clean Desktop. It changes only Explorer's reversible
/// no-icons flag (with the icon-list window as a fallback), never creates or reparents desktop windows.
/// </summary>
/// <remarks>
/// The dock it puts in place of the icons belongs to the dock's own service, not to this one, because
/// the dock is also shown on a fully native desktop. What this class keeps is the order of events: the
/// dock is required to be up, and only then are the icons hidden.
/// </remarks>
public sealed class CleanDesktopPresentation : ICleanDesktopPresentation, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IDesktopShelfService _shelf;
    private readonly IDockExperienceService _dock;
    private readonly ShellEventSource _shellEvents;
    private readonly ILogger<CleanDesktopPresentation> _logger;
    private readonly DesktopComThread _comThread;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _markerPath;
    private CleanDesktopMarker? _marker;
    private bool _active;
    private bool _disposed;

    public CleanDesktopPresentation(
        IDesktopShelfService shelf,
        IDockExperienceService dock,
        ShellEventSource shellEvents,
        ILogger<CleanDesktopPresentation> logger)
        : this(shelf, dock, shellEvents, logger, AppPaths.CleanDesktopRecoveryFile)
    {
    }

    internal CleanDesktopPresentation(
        IDesktopShelfService shelf,
        IDockExperienceService dock,
        ShellEventSource shellEvents,
        ILogger<CleanDesktopPresentation> logger,
        string markerPath)
    {
        _shelf = shelf;
        _dock = dock;
        _shellEvents = shellEvents;
        _logger = logger;
        _markerPath = markerPath;
        _comThread = new DesktopComThread(logger);
        _shellEvents.ShellRestarted += OnShellRestarted;
    }

    public bool IsAvailable => OperatingSystem.IsWindows() && _comThread.IsAvailable && _dock.IsAvailable;

    public bool IsNativeDesktopHidden => _active;

    public async Task<CleanDesktopPresentationResult> ActivateAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active)
            {
                return CleanDesktopPresentationResult.Active;
            }

            if (!IsAvailable)
            {
                return new CleanDesktopPresentationResult(false, "Windows desktop icon visibility is unavailable.");
            }

            // The real Shelf must be ready before Explorer's presentation is removed.
            var shelf = await _shelf.StartAsync(cancellationToken).ConfigureAwait(false);
            if (shelf.UnreadableFolders.Count == DesktopContentFolderCount())
            {
                return new CleanDesktopPresentationResult(false, "The Windows desktop folders could not be read.");
            }

            if (!await _dock.EnsureVisibleAsync(cancellationToken).ConfigureAwait(false))
            {
                return new CleanDesktopPresentationResult(false, "The dock could not be shown.");
            }

            var prepared = await _comThread.RunAsync(CaptureState, cancellationToken).ConfigureAwait(false);
            if (prepared is null)
            {
                await _dock.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                return new CleanDesktopPresentationResult(false, "Explorer's desktop view could not be found.");
            }

            _marker = prepared;
            if (!TryWriteMarker(prepared))
            {
                _marker = null;
                await _dock.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                return new CleanDesktopPresentationResult(false, "The Clean Desktop recovery marker could not be written.");
            }

            var hidden = await _comThread.RunAsync(HideIcons, cancellationToken).ConfigureAwait(false);
            if (!hidden)
            {
                await RestoreNativeCoreAsync(prepared, CancellationToken.None).ConfigureAwait(false);
                await _dock.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                TryClearMarker();
                _marker = null;
                return new CleanDesktopPresentationResult(false, "Explorer's desktop icons could not be hidden safely.");
            }

            _active = true;
            _logger.LogInformation("Clean Desktop hid Explorer desktop icons after the real Shelf became ready");
            return CleanDesktopPresentationResult.Active;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clean Desktop activation failed; restoring native desktop icons");
            await RestoreNativeCoreAsync(_marker, CancellationToken.None).ConfigureAwait(false);
            await _dock.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            TryClearMarker();
            _marker = null;
            _active = false;
            return new CleanDesktopPresentationResult(false, ex.Message);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<CleanDesktopPresentationResult> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_active && _marker is null && !File.Exists(_markerPath))
            {
                return CleanDesktopPresentationResult.Inactive;
            }

            var marker = _marker ?? TryReadMarker();
            var restored = await RestoreNativeCoreAsync(marker, cancellationToken).ConfigureAwait(false);
            if (!restored)
            {
                return new CleanDesktopPresentationResult(true, "Explorer's native desktop icons could not be restored.");
            }

            _active = false;
            _marker = null;
            await _dock.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            TryClearMarker();
            _logger.LogInformation("Clean Desktop restored Explorer desktop icons");
            return CleanDesktopPresentationResult.Inactive;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<CleanDesktopPresentationResult> SynchronizeStateAsync(CancellationToken cancellationToken = default)
    {
        if (!_active)
        {
            return CleanDesktopPresentationResult.Inactive;
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await _comThread.RunAsync(HideIcons, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Explorer desktop view was reacquired and Clean Desktop visibility was synchronized");
                    return CleanDesktopPresentationResult.Active;
                }
            }
            catch (Exception ex) when (attempt < 5)
            {
                _logger.LogDebug(ex, "Explorer desktop view is not ready on synchronization attempt {Attempt}", attempt + 1);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }

        // Fail open: if the new Explorer cannot be synchronized, abandon Clean Desktop.
        var restored = await RestoreNativeCoreAsync(_marker, CancellationToken.None).ConfigureAwait(false);
        _active = false;
        _marker = null;
        await _dock.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        if (restored)
        {
            TryClearMarker();
        }

        return new CleanDesktopPresentationResult(false, "Explorer restarted, but its desktop view could not be synchronized; Clean Desktop was disabled.");
    }

    public async Task<CleanDesktopPresentationResult> RecoverIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_markerPath))
        {
            return CleanDesktopPresentationResult.Inactive;
        }

        var marker = TryReadMarker();
        _logger.LogWarning("A previous Clean Desktop session did not close normally; restoring Explorer icons before mode restore");
        var restored = await RestoreNativeCoreAsync(marker, cancellationToken).ConfigureAwait(false);
        _active = false;
        _marker = null;
        await _dock.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        if (restored)
        {
            TryClearMarker();
            return CleanDesktopPresentationResult.Inactive;
        }

        return new CleanDesktopPresentationResult(true, "Native desktop icon recovery could not be verified.");
    }

    private int DesktopContentFolderCount() => Muralis.Core.Desktop.DesktopContentScanner.DefaultFolders().Count;

    private CleanDesktopMarker? CaptureState()
    {
        using var view = DesktopShellView.Open(_logger);
        if (view is null)
        {
            return null;
        }

        var state = view.VisualState();
        if (!state.Observed)
        {
            return null;
        }

        return new CleanDesktopMarker(
            true,
            state.OriginalFolderFlags,
            state.OriginalIconsVisible,
            state.HadIconWindow,
            DateTimeOffset.UtcNow,
            Environment.ProcessId);
    }

    private bool HideIcons()
    {
        using var view = DesktopShellView.Open(_logger);
        if (view is null)
        {
            return false;
        }

        if (view.CanReadFlags
            && view.SetFolderFlags(ShellViewInterfaces.FolderFlagNoIcons, ShellViewInterfaces.FolderFlagNoIcons) == 0
            && !view.IconsAreDrawn())
        {
            return true;
        }

        return view.HideIconList();
    }

    private async Task<bool> RestoreNativeCoreAsync(CleanDesktopMarker? marker, CancellationToken cancellationToken)
    {
        try
        {
            return await _comThread.RunAsync(() => RestoreIcons(marker), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explorer desktop icon restoration failed");
            return false;
        }
    }

    private bool RestoreIcons(CleanDesktopMarker? marker)
    {
        using var view = DesktopShellView.Open(_logger);
        if (view is null)
        {
            return false;
        }

        var originalNoIcons = marker is null
            ? 0u
            : marker.OriginalFolderFlags & ShellViewInterfaces.FolderFlagNoIcons;
        var flagsRestored = !view.CanReadFlags
            || view.SetFolderFlags(ShellViewInterfaces.FolderFlagNoIcons, originalNoIcons) == 0;

        var shouldShow = marker?.OriginalIconsVisible ?? true;
        var windowRestored = true;
        if (shouldShow && view.IconList != nint.Zero)
        {
            windowRestored = view.ShowIconList();
        }

        return flagsRestored && windowRestored && (shouldShow ? view.IconsAreDrawn() : !view.IconsAreDrawn());
    }

    private bool TryWriteMarker(CleanDesktopMarker marker)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
            var temporary = _markerPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(marker, JsonOptions));
            File.Move(temporary, _markerPath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The Clean Desktop recovery marker could not be written");
            return false;
        }
    }

    private CleanDesktopMarker? TryReadMarker()
    {
        try
        {
            return JsonSerializer.Deserialize<CleanDesktopMarker>(File.ReadAllText(_markerPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "The Clean Desktop recovery marker could not be read; using fail-open defaults");
            return null;
        }
    }

    private void TryClearMarker()
    {
        try
        {
            File.Delete(_markerPath);
            File.Delete(_markerPath + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Clean Desktop recovery marker could not be cleared");
        }
    }

    private void OnShellRestarted(object? sender, EventArgs args)
    {
        if (_active)
        {
            _ = SynchronizeAfterExplorerRestartAsync();
        }
    }

    private async Task SynchronizeAfterExplorerRestartAsync()
    {
        try
        {
            await SynchronizeStateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clean Desktop failed while handling an Explorer restart");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shellEvents.ShellRestarted -= OnShellRestarted;
        if (_active || File.Exists(_markerPath))
        {
            DeactivateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        _comThread.Dispose();
        _mutex.Dispose();
    }

    private sealed record CleanDesktopMarker(
        bool NativeIconsWereHidden,
        uint OriginalFolderFlags,
        bool OriginalIconsVisible,
        bool HadIconWindow,
        DateTimeOffset Timestamp,
        int ProcessId);
}
