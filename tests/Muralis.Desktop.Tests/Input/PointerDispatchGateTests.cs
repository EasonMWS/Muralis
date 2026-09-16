using Muralis.Desktop.Input;
using Xunit;

namespace Muralis.Desktop.Tests.Input;

public sealed class PointerDispatchGateTests
{
    [Fact]
    public void TheFirstReportAlwaysDispatches()
    {
        var gate = new PointerDispatchGate(6);

        Assert.True(gate.TryDispatch(1_000));
    }

    [Fact]
    public void ReportsInsideTheIntervalAreHeldBack()
    {
        var gate = new PointerDispatchGate(6);
        Assert.True(gate.TryDispatch(1_000));

        Assert.False(gate.TryDispatch(1_001));
        Assert.False(gate.TryDispatch(1_005));
        Assert.True(gate.TryDispatch(1_006));
        Assert.False(gate.TryDispatch(1_007));
    }

    [Fact]
    public void TheDelayToTheNextDispatchCountsDownToTheInterval()
    {
        var gate = new PointerDispatchGate(6);
        Assert.True(gate.TryDispatch(1_000));

        Assert.Equal(5, gate.DelayToNextDispatch(1_001));
        Assert.Equal(2, gate.DelayToNextDispatch(1_004));
    }

    [Fact]
    public void TheDelayNeverDropsBelowOneMillisecond()
    {
        var gate = new PointerDispatchGate(6);
        Assert.True(gate.TryDispatch(1_000));

        // A late flush must still arm a real timer instead of a zero one.
        Assert.Equal(1, gate.DelayToNextDispatch(1_010));
        Assert.Equal(1, gate.DelayToNextDispatch(1_100));
    }

    [Fact]
    public void AnExplicitDispatchCountsAsOne()
    {
        var gate = new PointerDispatchGate(6);
        gate.MarkDispatched(1_000);

        Assert.False(gate.TryDispatch(1_005));
        Assert.True(gate.TryDispatch(1_006));
    }
}
