using Muralis.Core.Dock;
using Xunit;

namespace Muralis.Core.Tests.Dock;

/// <summary>
/// The dock's five states and the two delays between them. The clock is passed in, so every case is
/// a sequence of advances rather than a sleep.
/// </summary>
public sealed class DockAutoHideTests
{
    private static DockOptions Dock(int showDelay = 120, int hideDelay = 600, bool autoHide = true) => new()
    {
        AutoHide = autoHide,
        ShowDelayMilliseconds = showDelay,
        HideDelayMilliseconds = hideDelay,
    };

    private static DockAutoHide Machine(int showDelay = 120, int hideDelay = 600, bool autoHide = true) =>
        new(Dock(showDelay, hideDelay, autoHide));

    [Fact]
    public void StartsHidden()
    {
        Assert.Equal(DockState.Hidden, Machine().State);
        Assert.False(Machine().IsOut);
    }

    [Fact]
    public void WantingTheDock_RevealsItOnlyAfterTheShowDelay()
    {
        var machine = Machine();

        Assert.False(machine.Advance(0, wantsReveal: true, dragging: false, revealSettled: false));
        Assert.False(machine.Advance(119, wantsReveal: true, dragging: false, revealSettled: false));
        Assert.Equal(DockState.Hidden, machine.State);

        Assert.True(machine.Advance(120, wantsReveal: true, dragging: false, revealSettled: false));
        Assert.Equal(DockState.Revealing, machine.State);
        Assert.True(machine.IsOut);
        Assert.False(machine.IsAtRest);

        // The rail is only at rest once it has finished moving.
        Assert.True(machine.Advance(121, wantsReveal: true, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Visible, machine.State);
        Assert.True(machine.IsAtRest);
    }

    [Fact]
    public void AQuickPassThroughTheTrigger_DoesNotRevealTheDock()
    {
        var machine = Machine();

        machine.Advance(0, wantsReveal: true, dragging: false, revealSettled: false);
        Assert.False(machine.Advance(80, wantsReveal: true, dragging: false, revealSettled: false));

        // The pointer left before the show delay elapsed: the pending reveal is forgotten, so
        // crossing the edge on the way somewhere else costs nothing.
        machine.Advance(100, wantsReveal: false, dragging: false, revealSettled: false);
        machine.Advance(400, wantsReveal: false, dragging: false, revealSettled: false);
        Assert.Equal(DockState.Hidden, machine.State);
    }

    [Fact]
    public void ZeroShowDelay_RevealsAtOnce()
    {
        var machine = Machine(showDelay: 0);

        Assert.True(machine.Advance(0, wantsReveal: true, dragging: false, revealSettled: false));
        Assert.Equal(DockState.Revealing, machine.State);
    }

    [Fact]
    public void LeavingTheDock_HidesItAfterTheHideDelay()
    {
        var machine = Visible();

        Assert.False(machine.Advance(200, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.False(machine.Advance(799, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Visible, machine.State);

        Assert.True(machine.Advance(800, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Hiding, machine.State);

        Assert.True(machine.Advance(900, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Hidden, machine.State);
        Assert.False(machine.IsOut);
    }

    [Fact]
    public void ReturningBeforeTheHideDelay_CancelsTheHide()
    {
        var machine = Visible();
        machine.Advance(200, wantsReveal: false, dragging: false, revealSettled: true);

        Assert.False(machine.Advance(700, wantsReveal: true, dragging: false, revealSettled: true));
        Assert.Null(machine.PendingChangeDelayMilliseconds(700));

        // The pointer may leave again now; the clock restarts from this leave.
        Assert.False(machine.Advance(700, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.False(machine.Advance(1299, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.True(machine.Advance(1300, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Hiding, machine.State);
    }

    [Fact]
    public void ThePointerLeavingMidReveal_TurnsTheRailAround()
    {
        var machine = Machine();
        machine.Advance(0, wantsReveal: true, dragging: false, revealSettled: false);
        machine.Advance(120, wantsReveal: true, dragging: false, revealSettled: false);

        // The pointer left while the rail was still coming out: the hide delay starts from the leave,
        // so the rail turns around rather than being dragged all the way out first.
        Assert.False(machine.Advance(200, wantsReveal: false, dragging: false, revealSettled: false));
        Assert.True(machine.Advance(800, wantsReveal: false, dragging: false, revealSettled: false));
        Assert.Equal(DockState.Hiding, machine.State);

        Assert.True(machine.Advance(900, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Hidden, machine.State);
    }

    [Fact]
    public void AHalfRetractedRail_ComesBackOutWhenThePointerReturns()
    {
        var machine = Visible();
        machine.Advance(700, wantsReveal: false, dragging: false, revealSettled: true);

        Assert.True(machine.Advance(1300, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Hiding, machine.State);

        // The pointer came back while the rail was still on its way out.
        Assert.True(machine.Advance(1400, wantsReveal: true, dragging: false, revealSettled: false));
        Assert.Equal(DockState.Revealing, machine.State);
        Assert.True(machine.IsOut);
    }

    [Fact]
    public void Dragging_KeepsTheDockOutAndNeverStartsAHide()
    {
        var machine = Visible();

        // A drag holds the rail out even though the pointer is nowhere near the trigger strip.
        Assert.True(machine.Advance(5000, wantsReveal: false, dragging: true, revealSettled: true));
        Assert.Equal(DockState.Dragging, machine.State);
        Assert.True(machine.IsOut);
        Assert.Null(machine.PendingChangeDelayMilliseconds(5000));

        // Releasing away from the dock starts the hide delay from that moment.
        Assert.True(machine.Advance(6000, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Visible, machine.State);
        Assert.False(machine.Advance(6599, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.True(machine.Advance(6600, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Hiding, machine.State);
    }

    [Fact]
    public void ADragThatStartsWhileHidden_RevealsTheDockSoSomethingCanBeDroppedOnIt()
    {
        var machine = Machine();

        Assert.True(machine.Advance(0, wantsReveal: false, dragging: true, revealSettled: false));
        Assert.Equal(DockState.Dragging, machine.State);
        Assert.True(machine.IsOut);
    }

    [Fact]
    public void ReleasingOnTheDock_LeavesItOut()
    {
        var machine = Machine();
        machine.Advance(0, wantsReveal: false, dragging: true, revealSettled: false);

        // The drop happened over the rail: the pointer is on it, so nothing is scheduled.
        Assert.True(machine.Advance(100, wantsReveal: true, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Visible, machine.State);
        Assert.False(machine.Advance(200, wantsReveal: true, dragging: false, revealSettled: true));
        Assert.Null(machine.PendingChangeDelayMilliseconds(200));
    }

    [Fact]
    public void ADockThatNeverHides_IsAlwaysOut()
    {
        var machine = Machine(autoHide: false);

        Assert.True(machine.Advance(0, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Visible, machine.State);
        Assert.False(machine.Advance(100000, wantsReveal: false, dragging: false, revealSettled: true));
        Assert.Equal(DockState.Visible, machine.State);
        Assert.Null(machine.PendingChangeDelayMilliseconds(100000));
    }

    [Fact]
    public void PendingChangeDelay_ReportsTheTimeUntilItWouldFlip()
    {
        var machine = Machine();

        machine.Advance(0, wantsReveal: true, dragging: false, revealSettled: false);
        Assert.Equal(20, machine.PendingChangeDelayMilliseconds(100));

        machine.Advance(120, wantsReveal: true, dragging: false, revealSettled: true);
        machine.Advance(200, wantsReveal: false, dragging: false, revealSettled: true);
        Assert.Equal(400, machine.PendingChangeDelayMilliseconds(400));

        Assert.Null(Visible().PendingChangeDelayMilliseconds(0));
    }

    private static DockAutoHide Visible()
    {
        var machine = Machine();
        machine.Advance(0, wantsReveal: true, dragging: false, revealSettled: false);
        machine.Advance(120, wantsReveal: true, dragging: false, revealSettled: false);
        machine.Advance(121, wantsReveal: true, dragging: false, revealSettled: true);
        Assert.Equal(DockState.Visible, machine.State);
        Assert.Null(machine.PendingChangeDelayMilliseconds(121));
        return machine;
    }
}
