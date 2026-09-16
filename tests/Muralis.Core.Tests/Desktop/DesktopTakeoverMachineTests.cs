using Muralis.Core.Desktop.Takeover;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The takeover's rules, proved without a desktop. What matters here is that no sequence of moves can
/// leave the machine claiming the desktop is fine while the icons may still be hidden: every failure
/// lands in a state that says which of the two is true.
/// </summary>
public sealed class DesktopTakeoverMachineTests
{
    [Fact]
    public void AFreshMachineOwnsNothing()
    {
        var machine = new DesktopTakeoverMachine();

        Assert.Equal(DesktopTakeoverState.Native, machine.State);
        Assert.Null(machine.Problem);
        Assert.False(machine.OwnsTheDesktop);
        Assert.False(machine.NeedsRecovery);
        Assert.False(machine.IsBusy);
    }

    [Fact]
    public void ATakeoverRunsThroughEnablingToOwned()
    {
        var machine = new DesktopTakeoverMachine();

        Assert.True(machine.BeginEnable());
        Assert.Equal(DesktopTakeoverState.Enabling, machine.State);
        Assert.True(machine.MayHideIcons);
        Assert.True(machine.IsBusy);

        Assert.True(machine.MarkEnabled());
        Assert.Equal(DesktopTakeoverState.Muralis, machine.State);
        Assert.True(machine.OwnsTheDesktop);
        Assert.False(machine.MayHideIcons);
    }

    [Fact]
    public void IconsMayOnlyBeHiddenOnTheWayIn()
    {
        // Nowhere else in the machine's life is hiding the icons a thing that may happen, which is what
        // keeps a give-back from being interleaved with a fresh hide.
        var machine = new DesktopTakeoverMachine();

        Assert.False(machine.MayHideIcons);
        machine.BeginEnable();
        Assert.True(machine.MayHideIcons);
        machine.MarkEnabled();
        Assert.False(machine.MayHideIcons);
        machine.BeginDisable();
        Assert.False(machine.MayHideIcons);
    }

    [Fact]
    public void ASecondTakeoverIsRefusedWhileOneIsRunningOrDone()
    {
        var machine = new DesktopTakeoverMachine();

        Assert.True(machine.BeginEnable());
        Assert.False(machine.BeginEnable());
        Assert.Equal(DesktopTakeoverState.Enabling, machine.State);

        machine.MarkEnabled();
        Assert.False(machine.BeginEnable());
        Assert.Equal(DesktopTakeoverState.Muralis, machine.State);
    }

    [Fact]
    public void AFailedTakeoverThatWasGivenBackIsNativeAgain()
    {
        var machine = new DesktopTakeoverMachine();
        machine.BeginEnable();

        Assert.True(machine.FailEnable("the shell refused", desktopGivenBack: true));

        Assert.Equal(DesktopTakeoverState.Native, machine.State);
        Assert.Equal("the shell refused", machine.Problem);
        Assert.False(machine.NeedsRecovery);
    }

    [Fact]
    public void AFailedTakeoverThatLeftTheIconsHiddenOwesARecovery()
    {
        var machine = new DesktopTakeoverMachine();
        machine.BeginEnable();

        Assert.True(machine.FailEnable("the rollback did not verify", desktopGivenBack: false));

        Assert.Equal(DesktopTakeoverState.RecoveryRequired, machine.State);
        Assert.Equal("the rollback did not verify", machine.Problem);
        Assert.True(machine.NeedsRecovery);
        Assert.False(machine.OwnsTheDesktop);
    }

    [Fact]
    public void NothingMayBeTakenOverWhileAGiveBackIsOwed()
    {
        // The invariant the whole state machine exists for: a desktop that may still have its icons
        // hidden is never built on again, so a second takeover cannot hide them twice over.
        var machine = new DesktopTakeoverMachine();
        machine.BeginEnable();
        machine.FailEnable("the rollback did not verify", desktopGivenBack: false);

        Assert.False(machine.BeginEnable());
        Assert.Equal(DesktopTakeoverState.RecoveryRequired, machine.State);
    }

    [Fact]
    public void AGiveBackMayBeAskedForFromEveryStateItIsSafeFrom()
    {
        // "Restore the native desktop" is always safe to ask for, including on a desktop that was never
        // taken over, because giving back what the user already has is a no-op and not a failure.
        foreach (var state in new[]
                 {
                     DesktopTakeoverState.Native,
                     DesktopTakeoverState.Muralis,
                     DesktopTakeoverState.RecoveryRequired,
                 })
        {
            var machine = new DesktopTakeoverMachine();
            switch (state)
            {
                case DesktopTakeoverState.Muralis:
                    machine.BeginEnable();
                    machine.MarkEnabled();
                    break;
                case DesktopTakeoverState.RecoveryRequired:
                    machine.RequireRecovery("a crash left the marker behind");
                    break;
            }

            Assert.Equal(state, machine.State);
            Assert.True(machine.BeginDisable());
            Assert.Equal(DesktopTakeoverState.Disabling, machine.State);
        }
    }

    [Fact]
    public void ASecondChangeIsRefusedWhileOneIsRunning()
    {
        var machine = new DesktopTakeoverMachine();
        machine.BeginEnable();
        machine.MarkEnabled();
        machine.BeginDisable();

        Assert.False(machine.BeginDisable());
        Assert.Equal(DesktopTakeoverState.Disabling, machine.State);
    }

    [Fact]
    public void AGiveBackThatDidNotVerifyOwesARecoveryAndKeepsItsReason()
    {
        var machine = new DesktopTakeoverMachine();
        machine.BeginEnable();
        machine.MarkEnabled();
        machine.BeginDisable();

        Assert.True(machine.FailDisable("the icons are not back the way they were"));

        Assert.Equal(DesktopTakeoverState.RecoveryRequired, machine.State);
        Assert.Equal("the icons are not back the way they were", machine.Problem);
    }

    [Fact]
    public void AVerifiedGiveBackClearsTheProblem()
    {
        var machine = new DesktopTakeoverMachine();
        machine.BeginEnable();
        machine.FailEnable("the shell refused", desktopGivenBack: false);
        machine.BeginDisable();

        Assert.True(machine.MarkDisabled());

        Assert.Equal(DesktopTakeoverState.Native, machine.State);
        Assert.Null(machine.Problem);
        Assert.False(machine.NeedsRecovery);
    }

    [Fact]
    public void TheEmergencyRestoreSetsAsideWhateverTheMachineWasDoing()
    {
        // The one path that may not be refused: being asked for a restore means the desktop may be
        // stuck, so the state it was in is left behind rather than argued with.
        var machine = new DesktopTakeoverMachine();
        machine.BeginEnable();
        machine.MarkEnabled();

        machine.RequireRecovery("an explicit restore of the native desktop was asked for");

        Assert.True(machine.NeedsRecovery);
        Assert.Equal("an explicit restore of the native desktop was asked for", machine.Problem);
        Assert.True(machine.BeginDisable());
    }

    [Fact]
    public void ACancelledTakeoverGoesBackToNativeWithoutClaimingARestore()
    {
        var machine = new DesktopTakeoverMachine();
        machine.BeginEnable();

        machine.ResetToNative();

        Assert.Equal(DesktopTakeoverState.Native, machine.State);
        Assert.Null(machine.Problem);
    }
}
