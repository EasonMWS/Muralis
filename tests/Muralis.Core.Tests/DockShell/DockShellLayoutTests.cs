using Muralis.Core.DockShell;
using Xunit;

namespace Muralis.Core.Tests.DockShell;

public sealed class DockShellLayoutTests
{
    [Fact]
    public void Calculate_KeepsPinnedAndUtilitiesIntrinsic_WhenShelfOverflows()
    {
        var shortShelf = DockShellLayout.Calculate(4, 3, 3);
        var longShelf = DockShellLayout.Calculate(4, 20, 3);

        Assert.Equal(shortShelf.PinnedApps.ViewportWidth, longShelf.PinnedApps.ViewportWidth);
        Assert.Equal(shortShelf.Utilities.ViewportWidth, longShelf.Utilities.ViewportWidth);
        Assert.False(longShelf.PinnedApps.IsScrollable);
        Assert.False(longShelf.Utilities.IsScrollable);
        Assert.True(longShelf.DesktopShelf.IsScrollable);
        Assert.True(longShelf.DesktopShelf.ContentWidth > longShelf.DesktopShelf.ViewportWidth);
    }

    [Fact]
    public void Calculate_BoundsOnlyShelfViewport_WhenSpaceShrinks()
    {
        var options = new DockShellLayoutOptions();
        var spacious = DockShellLayout.Calculate(4, 10, 3, 1200, options);
        var narrow = DockShellLayout.Calculate(4, 10, 3, 760, options);

        Assert.Equal(spacious.PinnedApps, narrow.PinnedApps);
        Assert.Equal(spacious.Utilities, narrow.Utilities);
        Assert.True(narrow.DesktopShelf.ViewportWidth < spacious.DesktopShelf.ViewportWidth);
        Assert.Equal(options.ShelfMinimumWidth, narrow.DesktopShelf.ViewportWidth);
    }

    [Fact]
    public void Calculate_IsContentSized_ForSmallShelf()
    {
        var result = DockShellLayout.Calculate(1, 4, 1);

        Assert.False(result.DesktopShelf.IsScrollable);
        Assert.True(result.TotalWidth < 800);
    }
}
