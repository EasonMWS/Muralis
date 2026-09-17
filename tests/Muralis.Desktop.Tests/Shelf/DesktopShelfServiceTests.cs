using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.DockShell;
using Muralis.Desktop.Shelf;
using Xunit;

namespace Muralis.Desktop.Tests.Shelf;

public sealed class DesktopShelfServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muralis-shelf-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Refresh_MergesDeduplicatesAndSortsUserAndPublicDesktop()
    {
        var user = Path.Combine(_root, "User");
        var common = Path.Combine(_root, "Public");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(common);
        Directory.CreateDirectory(Path.Combine(user, "Projects"));
        await File.WriteAllTextAsync(Path.Combine(user, "Launch.lnk"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(user, "notes.final.txt"), "user");
        await File.WriteAllTextAsync(Path.Combine(common, "notes.final.txt"), "public duplicate");
        await File.WriteAllTextAsync(Path.Combine(common, "共享 🌌.txt"), "public");

        var scanner = new DesktopContentScanner(
            NullLogger<DesktopContentScanner>.Instance,
            [user, common]);
        using var service = new DesktopShelfService(scanner, NullLogger<DesktopShelfService>.Instance);

        var snapshot = await service.RefreshAsync();

        Assert.Equal(4, snapshot.Items.Count);
        Assert.Equal(3, snapshot.UserCount);
        Assert.Equal(1, snapshot.PublicCount);
        Assert.Equal(DockShellItemType.Folder, snapshot.Items[0].ItemType);
        Assert.Equal(DockShellItemType.Shortcut, snapshot.Items[1].ItemType);
        Assert.Equal(1, snapshot.Items.Count(item => item.DisplayName == "notes.final"));
        Assert.Equal(4, snapshot.Items.Select(item => item.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Watcher_CoalescesChangesAndRefreshesSnapshot()
    {
        var user = Path.Combine(_root, "User");
        var common = Path.Combine(_root, "Public");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(common);
        var scanner = new DesktopContentScanner(
            NullLogger<DesktopContentScanner>.Instance,
            [user, common]);
        using var service = new DesktopShelfService(scanner, NullLogger<DesktopShelfService>.Instance);
        await service.StartAsync();

        var changed = new TaskCompletionSource<DesktopShelfSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, snapshot) =>
        {
            if (snapshot.Items.Count >= 2)
            {
                changed.TrySetResult(snapshot);
            }
        };

        await File.WriteAllTextAsync(Path.Combine(user, "one.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(user, "two.txt"), "2");

        var refreshed = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(refreshed.Items, item => item.DisplayName == "one");
        Assert.Contains(refreshed.Items, item => item.DisplayName == "two");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
