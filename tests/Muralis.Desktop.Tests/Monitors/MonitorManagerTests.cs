using Muralis.Core.Models;
using Muralis.Desktop.Monitors;
using Xunit;

namespace Muralis.Desktop.Tests.Monitors;

public sealed class MonitorManagerTests
{
    [Fact]
    public void Monitors_IsEmptyBeforeAnyUpdate()
    {
        Assert.Empty(new MonitorManager().Monitors);
    }

    [Fact]
    public void Update_PublishesSnapshotAndRaisesChanged()
    {
        var manager = new MonitorManager();
        MonitorsChanged? observed = null;
        manager.Changed += (_, changed) => observed = changed;

        var monitors = new[] { Display("edid:left", isPrimary: true), Display("edid:right", isPrimary: false) };
        manager.Update(monitors);

        Assert.Equal(monitors, manager.Monitors);
        Assert.NotNull(observed);
        Assert.Equal(monitors, observed!.Monitors);
    }

    [Fact]
    public void Update_WithIdenticalSnapshot_DoesNotRaise()
    {
        var manager = new MonitorManager();
        manager.Update([Display("edid:left", isPrimary: true)]);

        var raised = 0;
        manager.Changed += (_, _) => raised++;
        manager.Update([Display("edid:left", isPrimary: true)]);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Update_WithChangedFacts_RaisesChanged()
    {
        var manager = new MonitorManager();
        manager.Update([Display("edid:left", isPrimary: true)]);

        var raised = 0;
        manager.Changed += (_, _) => raised++;
        manager.Update([Display("edid:left", isPrimary: false)]);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Primary_ReturnsThePrimaryDisplay()
    {
        var manager = new MonitorManager();
        var primary = Display("edid:left", isPrimary: true);
        manager.Update([Display("edid:right", isPrimary: false), primary]);

        Assert.Equal(primary, manager.Primary);
    }

    [Fact]
    public void Primary_IsNullWithoutPrimaryDisplay()
    {
        var manager = new MonitorManager();
        manager.Update([Display("edid:left", isPrimary: false)]);

        Assert.Null(manager.Primary);
    }

    [Fact]
    public void Resolve_MatchesByStableId()
    {
        var manager = new MonitorManager();
        var left = Display("edid:left", isPrimary: true);
        var right = Display("edid:right", isPrimary: false);
        manager.Update([left, right]);

        Assert.Equal(left, manager.Resolve(new MonitorRef("edid:left")));
        Assert.Equal(right, manager.Resolve(MonitorRef.From(right)));
        Assert.Null(manager.Resolve(new MonitorRef("edid:gone")));
    }

    private static Monitor Display(string stableId, bool isPrimary) =>
        new(
            new MonitorIdentity(stableId, null, IdentityConfidence.SignatureFallback, stableId),
            new MonitorRuntimeInfo(
                DevicePath: @"\\?\DISPLAY#" + stableId,
                DeviceName: @"\\.\DISPLAY1",
                FriendlyName: stableId,
                IsPrimary: isPrimary,
                OrderIndex: 0,
                Bounds: new PixelRect(0, 0, 1920, 1080),
                WorkArea: new PixelRect(0, 0, 1920, 1040),
                Dpi: 96,
                ScaleFactor: 1.0,
                Orientation: MonitorOrientation.Landscape,
                Mirroring: MirroringInfo.None));
}
