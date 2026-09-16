using Muralis.Core.Canvas;
using Xunit;

namespace Muralis.Core.Tests.Canvas;

public sealed class CanvasDockAutoHideTests
{
    private static CanvasDockAutoHide Machine(int showDelay = 150, int hideDelay = 600) =>
        new(new CanvasDockOptions { ShowDelayMilliseconds = showDelay, HideDelayMilliseconds = hideDelay });

    [Fact]
    public void StartsCollapsed()
    {
        Assert.Equal(CanvasDockPhase.Collapsed, Machine().Phase);
    }

    [Fact]
    public void WantingTheDock_ShowsItOnlyAfterTheShowDelay()
    {
        var machine = Machine();

        Assert.False(machine.Advance(0, wantsExpanded: true, interactionLocked: false));
        Assert.False(machine.Advance(149, wantsExpanded: true, interactionLocked: false));
        Assert.Equal(CanvasDockPhase.Collapsed, machine.Phase);

        Assert.True(machine.Advance(150, wantsExpanded: true, interactionLocked: false));
        Assert.Equal(CanvasDockPhase.Shown, machine.Phase);
    }

    [Fact]
    public void AQuickPassThroughTheTrigger_DoesNotShowTheDock()
    {
        var machine = Machine();

        machine.Advance(0, wantsExpanded: true, interactionLocked: false);
        Assert.False(machine.Advance(80, wantsExpanded: true, interactionLocked: false));

        // Pointer left before the show delay elapsed: the pending show is forgotten.
        machine.Advance(100, wantsExpanded: false, interactionLocked: false);
        Assert.False(machine.Advance(400, wantsExpanded: true, interactionLocked: false));
        Assert.Equal(CanvasDockPhase.Collapsed, machine.Phase);
    }

    [Fact]
    public void ZeroShowDelay_ShowsImmediately()
    {
        var machine = Machine(showDelay: 0);

        Assert.True(machine.Advance(0, wantsExpanded: true, interactionLocked: false));
        Assert.Equal(CanvasDockPhase.Shown, machine.Phase);
    }

    [Fact]
    public void LeavingTheDock_RetractsItAfterTheHideDelay()
    {
        var machine = Machine();
        machine.Advance(0, wantsExpanded: true, interactionLocked: false);
        machine.Advance(150, wantsExpanded: true, interactionLocked: false);

        Assert.False(machine.Advance(200, wantsExpanded: false, interactionLocked: false));
        Assert.False(machine.Advance(799, wantsExpanded: false, interactionLocked: false));
        Assert.Equal(CanvasDockPhase.Shown, machine.Phase);

        Assert.True(machine.Advance(800, wantsExpanded: false, interactionLocked: false));
        Assert.Equal(CanvasDockPhase.Collapsed, machine.Phase);
    }

    [Fact]
    public void ReturningBeforeTheHideDelay_CancelsTheRetract()
    {
        var machine = Machine();
        machine.Advance(0, wantsExpanded: true, interactionLocked: false);
        machine.Advance(150, wantsExpanded: true, interactionLocked: false);

        machine.Advance(200, wantsExpanded: false, interactionLocked: false);
        Assert.False(machine.Advance(700, wantsExpanded: true, interactionLocked: false));
        Assert.Null(machine.PendingChangeDelayMilliseconds(700));

        // The pointer may leave again now; the clock restarts from this leave.
        Assert.False(machine.Advance(700, wantsExpanded: false, interactionLocked: false));
        Assert.False(machine.Advance(1299, wantsExpanded: false, interactionLocked: false));
        Assert.True(machine.Advance(1300, wantsExpanded: false, interactionLocked: false));
        Assert.Equal(CanvasDockPhase.Collapsed, machine.Phase);
    }

    [Fact]
    public void AnInteractionLock_KeepsTheDockOut()
    {
        var machine = Machine();
        machine.Advance(0, wantsExpanded: true, interactionLocked: false);
        machine.Advance(150, wantsExpanded: true, interactionLocked: false);

        // A drag holds the rail open even though the pointer is not in the trigger band.
        Assert.False(machine.Advance(5000, wantsExpanded: false, interactionLocked: true));
        Assert.Null(machine.PendingChangeDelayMilliseconds(5000));
        Assert.Equal(CanvasDockPhase.Shown, machine.Phase);

        // Releasing outside the trigger starts the hide delay from that moment.
        Assert.False(machine.Advance(6000, wantsExpanded: false, interactionLocked: false));
        Assert.True(machine.Advance(6600, wantsExpanded: false, interactionLocked: false));
    }

    [Fact]
    public void AnInteractionLock_AlsoSummonsTheDock()
    {
        var machine = Machine();

        // A drag that starts while collapsed still shows the rail, so items can be dropped on it.
        Assert.False(machine.Advance(0, wantsExpanded: false, interactionLocked: true));
        Assert.True(machine.Advance(150, wantsExpanded: false, interactionLocked: true));
        Assert.Equal(CanvasDockPhase.Shown, machine.Phase);
    }

    [Fact]
    public void PendingChangeDelay_ReportsTheTimeUntilItWouldFlip()
    {
        var machine = Machine();

        machine.Advance(0, wantsExpanded: true, interactionLocked: false);
        Assert.Equal(50, machine.PendingChangeDelayMilliseconds(100));

        machine.Advance(150, wantsExpanded: true, interactionLocked: false);
        machine.Advance(200, wantsExpanded: false, interactionLocked: false);
        Assert.Equal(400, machine.PendingChangeDelayMilliseconds(400));

        machine.Advance(800, wantsExpanded: false, interactionLocked: false);
        Assert.Null(machine.PendingChangeDelayMilliseconds(900));
    }
}
