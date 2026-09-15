using Muralis.Core.Models;
using Muralis.Desktop.Surfaces;
using Xunit;

namespace Muralis.Desktop.Tests.Surfaces;

public sealed class MonitorGeometryTests
{
    [Fact]
    public void From_CopiesBoundsAndWorkAreaFromRuntimeInfo()
    {
        var runtime = new MonitorRuntimeInfo(
            DevicePath: @"\\?\DISPLAY#SAMPLE",
            DeviceName: @"\\.\DISPLAY1",
            FriendlyName: "Display 1",
            IsPrimary: true,
            OrderIndex: 0,
            Bounds: new PixelRect(0, 0, 2560, 1440),
            WorkArea: new PixelRect(0, 0, 2560, 1400),
            Dpi: 192,
            ScaleFactor: 1.5,
            Orientation: MonitorOrientation.Landscape,
            Mirroring: MirroringInfo.None);

        var geometry = MonitorGeometry.From(runtime);

        Assert.Equal(runtime.Bounds, geometry.Bounds);
        Assert.Equal(runtime.WorkArea, geometry.WorkArea);
    }
}
