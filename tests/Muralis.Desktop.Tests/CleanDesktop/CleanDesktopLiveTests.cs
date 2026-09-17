using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.DockShell;
using Muralis.Desktop.CleanDesktop;
using Muralis.Desktop.Shelf;
using Muralis.Desktop.Shell;
using Muralis.Desktop.Takeover;
using Xunit;

namespace Muralis.Desktop.Tests.CleanDesktop;

// Deliberately synchronous: ShellEventSource owns a native window that must be destroyed on the
// same test thread that created it. The awaited production work itself runs on dedicated workers.
#pragma warning disable xUnit1031

/// <summary>
/// Opt-in integration check against the real Explorer desktop. It never touches desktop files and
/// restores the original icon state in a finally block. Set MURALIS_CLEAN_DESKTOP_LIVE=1 to run.
/// </summary>
public sealed class CleanDesktopLiveTests
{
    [Fact]
    public async Task RealDesktopWatcher_TracksSafeTemporaryCreateRenameAndDelete()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_CLEAN_DESKTOP_LIVE") is null)
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var stem = $"muralis-phase4b-{Guid.NewGuid():N}";
        var first = Path.Combine(desktop, stem + ".tmp");
        var renamed = Path.Combine(desktop, stem + "-renamed.tmp");
        var scanner = new DesktopContentScanner(NullLogger<DesktopContentScanner>.Instance);
        using var shelf = new DesktopShelfService(scanner, NullLogger<DesktopShelfService>.Instance);
        await shelf.StartAsync();

        try
        {
            var created = WaitForSnapshotAsync(shelf, snapshot => snapshot.Items.Any(item =>
                string.Equals(item.Path, first, StringComparison.OrdinalIgnoreCase)));
            await File.WriteAllTextAsync(first, "Muralis Phase 4B live watcher check");
            await created;

            var moved = WaitForSnapshotAsync(shelf, snapshot => snapshot.Items.Any(item =>
                string.Equals(item.Path, renamed, StringComparison.OrdinalIgnoreCase)));
            File.Move(first, renamed);
            await moved;

            var removed = WaitForSnapshotAsync(shelf, snapshot => snapshot.Items.All(item =>
                !string.Equals(item.Path, renamed, StringComparison.OrdinalIgnoreCase)));
            File.Delete(renamed);
            await removed;
        }
        finally
        {
            if (File.Exists(first))
            {
                File.Delete(first);
            }

            if (File.Exists(renamed))
            {
                File.Delete(renamed);
            }
        }
    }

    [Fact]
    public void ActivateAndDeactivate_HideThenRestoreTheRealExplorerView()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_CLEAN_DESKTOP_LIVE") is null)
        {
            return;
        }

        var marker = Path.Combine(Path.GetTempPath(), $"muralis-clean-live-{Guid.NewGuid():N}.json");
        var scanner = new DesktopContentScanner(NullLogger<DesktopContentScanner>.Instance);
        using var shelf = new DesktopShelfService(scanner, NullLogger<DesktopShelfService>.Instance);
        using var events = new ShellEventSource(NullLogger<ShellEventSource>.Instance);
        using var presentation = new CleanDesktopPresentation(
            shelf,
            new FakeDock(),
            events,
            NullLogger<CleanDesktopPresentation>.Instance,
            marker);

        var before = ReadIconsVisible();
        try
        {
            var active = presentation.ActivateAsync().GetAwaiter().GetResult();
            Assert.True(active.IsActive, active.Error);
            Assert.False(ReadIconsVisible());
            Assert.True(File.Exists(marker));

            var synchronized = presentation.SynchronizeStateAsync().GetAwaiter().GetResult();
            Assert.True(synchronized.IsActive, synchronized.Error);
            Assert.False(ReadIconsVisible());
        }
        finally
        {
            var inactive = presentation.DeactivateAsync().GetAwaiter().GetResult();
            Assert.False(inactive.IsActive, inactive.Error);
            Assert.Equal(before, ReadIconsVisible());
            Assert.False(File.Exists(marker));
        }
    }

    [Fact]
    public void RecoveryMarker_RestoresIconsAndReturnsToSafeState()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_CLEAN_DESKTOP_LIVE") is null)
        {
            return;
        }

        var marker = Path.Combine(Path.GetTempPath(), $"muralis-clean-recovery-live-{Guid.NewGuid():N}.json");
        var scanner = new DesktopContentScanner(NullLogger<DesktopContentScanner>.Instance);
        using var shelf = new DesktopShelfService(scanner, NullLogger<DesktopShelfService>.Instance);
        using var events = new ShellEventSource(NullLogger<ShellEventSource>.Instance);
        using var presentation = new CleanDesktopPresentation(
            shelf,
            new FakeDock(),
            events,
            NullLogger<CleanDesktopPresentation>.Instance,
            marker);

        var before = ReadIconsVisible();
        try
        {
            Assert.True(presentation.ActivateAsync().GetAwaiter().GetResult().IsActive);
            Assert.True(File.Exists(marker));

            var recovered = presentation.RecoverIfNeededAsync().GetAwaiter().GetResult();

            Assert.False(recovered.IsActive, recovered.Error);
            Assert.False(presentation.IsNativeDesktopHidden);
            Assert.Equal(before, ReadIconsVisible());
            Assert.False(File.Exists(marker));
        }
        finally
        {
            presentation.DeactivateAsync().GetAwaiter().GetResult();
        }
    }

    private static bool ReadIconsVisible()
    {
        using var thread = new DesktopComThread(NullLogger.Instance);
        return thread.RunAsync(() =>
        {
            using var view = DesktopShellView.Open(NullLogger.Instance);
            Assert.NotNull(view);
            return view!.IconsAreDrawn();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<DesktopShelfSnapshot> WaitForSnapshotAsync(
        DesktopShelfService shelf,
        Func<DesktopShelfSnapshot, bool> predicate)
    {
        var completion = new TaskCompletionSource<DesktopShelfSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DesktopShelfSnapshot>? handler = null;
        handler = (_, snapshot) =>
        {
            if (predicate(snapshot))
            {
                completion.TrySetResult(snapshot);
            }
        };
        shelf.Changed += handler;
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            shelf.Changed -= handler;
        }
    }

    private sealed class FakeDock : Muralis.Core.Abstractions.IDockExperienceService
    {
        public bool IsAvailable => true;

        public bool IsVisible { get; private set; }

        public event EventHandler<bool>? VisibilityChanged;

        public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) => Show(false);

        public Task<bool> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => Show(visible);

        public Task<bool> EnsureVisibleAsync(CancellationToken cancellationToken = default) => Show(true);

        public Task<bool> ReleaseAsync(CancellationToken cancellationToken = default) => Show(false);

        public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default) => Show(false);

        private Task<bool> Show(bool visible)
        {
            if (IsVisible != visible)
            {
                IsVisible = visible;
                VisibilityChanged?.Invoke(this, visible);
            }

            return Task.FromResult(true);
        }
    }
}
#pragma warning restore xUnit1031
