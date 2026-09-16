using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop.Takeover;
using Muralis.Desktop.Takeover;
using Xunit;

namespace Muralis.Desktop.Tests.Takeover;

/// <summary>
/// The one part of the takeover that pure code cannot prove: whether the shell's view really accepts the
/// declared calls and really stops drawing the desktop icons. Every interface the takeover uses is
/// declared by vtable slot, so a wrong slot is not a compile error — it is a call to the wrong method on
/// a live shell object. This check runs the whole transaction against the real desktop and insists the
/// flags come back identical.
/// </summary>
/// <remarks>
/// Set <c>MURALIS_TAKEOVER_LIVE=1</c> to run. It hides the desktop icons for about a second and puts
/// them back; every step is written to <c>takeover-live.log</c> beside the test binaries.
/// </remarks>
public sealed class DesktopTakeoverLiveTests
{
    [Fact]
    public async Task TakeOverAndGiveBack_LeaveTheDesktopExactlyAsItWasFound()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_TAKEOVER_LIVE") is null)
        {
            return;
        }

        var markerPath = Path.Combine(Path.GetTempPath(), $"muralis-takeover-live-{Guid.NewGuid():N}.json");

        using var service = new DesktopTakeoverService(
            NullLoggerFactory.Instance,
            NullLogger<DesktopTakeoverService>.Instance,
            new DesktopTakeoverRecordStore(NullLogger<DesktopTakeoverRecordStore>.Instance, markerPath));

        var before = await service.ReadNativeStateAsync();
        Probe($"before: visible={before.OriginalIconsVisible} flags=0x{before.OriginalFolderFlags:X8} observed={before.Observed} window={before.HadIconWindow}");

        Assert.True(before.Observed, "the shell answered nothing about the desktop; the live check needs a real one");

        try
        {
            var taken = await service.TakeAsync();
            var during = await service.ReadNativeStateAsync();
            Probe($"take: state={taken.State} strategy={taken.Strategy} error={taken.Error}");
            Probe($"during: visible={during.OriginalIconsVisible} flags=0x{during.OriginalFolderFlags:X8}");

            if (!before.OriginalIconsVisible)
            {
                // A user who keeps their own icons hidden has nothing to hide, and the takeover has to
                // say so rather than claim it did something.
                Assert.Equal(DesktopIconStrategy.None, taken.Strategy);
                Assert.Equal(DesktopTakeoverState.Muralis, taken.State);
                return;
            }

            Assert.Equal(DesktopTakeoverState.Muralis, taken.State);
            Assert.NotEqual(DesktopIconStrategy.None, taken.Strategy);
            Assert.False(during.OriginalIconsVisible, "the desktop icons were still drawn after the takeover");
            Assert.True(File.Exists(markerPath), "the takeover left no marker behind, so a crash could not be recovered");
        }
        finally
        {
            var given = await service.RestoreNativeDesktopAsync();
            var after = await service.ReadNativeStateAsync();
            Probe($"give back: state={given.State} error={given.Error}");
            Probe($"after: visible={after.OriginalIconsVisible} flags=0x{after.OriginalFolderFlags:X8}");

            Assert.Equal(DesktopTakeoverState.Native, given.State);
            Assert.Equal(before.OriginalIconsVisible, after.OriginalIconsVisible);
            Assert.Equal(before.OriginalFolderFlags, after.OriginalFolderFlags);
            Assert.False(File.Exists(markerPath), "the marker was left behind after a verified give-back");

            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
    }

    /// <summary>
    /// The give-back must be safe to ask for when nothing was taken over, because that is exactly what
    /// the emergency path in the tray does and it has to be usable at any moment.
    /// </summary>
    [Fact]
    public async Task GivingBackADesktopThatWasNeverTakenOver_ChangesNothing()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_TAKEOVER_LIVE") is null)
        {
            return;
        }

        var markerPath = Path.Combine(Path.GetTempPath(), $"muralis-takeover-live-{Guid.NewGuid():N}.json");

        using var service = new DesktopTakeoverService(
            NullLoggerFactory.Instance,
            NullLogger<DesktopTakeoverService>.Instance,
            new DesktopTakeoverRecordStore(NullLogger<DesktopTakeoverRecordStore>.Instance, markerPath));

        var before = await service.ReadNativeStateAsync();
        var given = await service.ReleaseAsync();
        var after = await service.ReadNativeStateAsync();

        Probe($"untouched give back: state={given.State} error={given.Error}");
        Probe($"untouched after: visible={after.OriginalIconsVisible} flags=0x{after.OriginalFolderFlags:X8}");

        Assert.Equal(DesktopTakeoverState.Native, given.State);
        Assert.Equal(before.OriginalIconsVisible, after.OriginalIconsVisible);
        Assert.Equal(before.OriginalFolderFlags, after.OriginalFolderFlags);
    }

    /// <summary>What a fresh process does with a marker a crashed run left behind.</summary>
    [Fact]
    public async Task ARecoveryAfterACrash_Marker_PutsTheDesktopBack()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_TAKEOVER_LIVE") is null)
        {
            return;
        }

        var markerPath = Path.Combine(Path.GetTempPath(), $"muralis-takeover-live-{Guid.NewGuid():N}.json");

        using var service = new DesktopTakeoverService(
            NullLoggerFactory.Instance,
            NullLogger<DesktopTakeoverService>.Instance,
            new DesktopTakeoverRecordStore(NullLogger<DesktopTakeoverRecordStore>.Instance, markerPath));

        var before = await service.ReadNativeStateAsync();
        if (!before.OriginalIconsVisible)
        {
            return;
        }

        // Hide them the way a run that is about to be killed would, then throw that run away: the
        // recovery below must work from the marker alone, because that is all a crash leaves behind.
        var killed = await service.TakeAsync();
        Assert.Equal(DesktopTakeoverState.Muralis, killed.State);

        service.Dispose();

        var store = new DesktopTakeoverRecordStore(NullLogger<DesktopTakeoverRecordStore>.Instance, markerPath);
        var leftBehind = store.Load().Record;
        Assert.NotNull(leftBehind);
        Assert.True(leftBehind!.TakeoverWasActive, "the killed run left a marker that does not claim a takeover");

        using var fresh = new DesktopTakeoverService(
            NullLoggerFactory.Instance,
            NullLogger<DesktopTakeoverService>.Instance,
            store);

        var recovered = await fresh.RecoverIfNeededAsync();
        var after = await fresh.ReadNativeStateAsync();

        Probe($"recovered: state={recovered.State} error={recovered.Error}");
        Probe($"recovered after: visible={after.OriginalIconsVisible} flags=0x{after.OriginalFolderFlags:X8}");

        Assert.Equal(DesktopTakeoverState.Native, recovered.State);
        Assert.Equal(before.OriginalIconsVisible, after.OriginalIconsVisible);
        Assert.Equal(before.OriginalFolderFlags, after.OriginalFolderFlags);
        Assert.False(File.Exists(markerPath));
    }

    /// <summary>A marker that cannot be read must be reported and left alone, never acted on.</summary>
    [Fact]
    public async Task AnUnreadableMarker_IsReportedAndTheDesktopIsNotTouched()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_TAKEOVER_LIVE") is null)
        {
            return;
        }

        var markerPath = Path.Combine(Path.GetTempPath(), $"muralis-takeover-live-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(markerPath, "{ this is not the marker that was written ");

        using var service = new DesktopTakeoverService(
            NullLoggerFactory.Instance,
            NullLogger<DesktopTakeoverService>.Instance,
            new DesktopTakeoverRecordStore(NullLogger<DesktopTakeoverRecordStore>.Instance, markerPath));

        var before = await service.ReadNativeStateAsync();
        var outcome = await service.RecoverIfNeededAsync();
        var after = await service.ReadNativeStateAsync();

        Probe($"unreadable marker: state={outcome.State} error={outcome.Error}");

        Assert.NotNull(outcome.Error);
        Assert.Equal(before.OriginalIconsVisible, after.OriginalIconsVisible);
        Assert.Equal(before.OriginalFolderFlags, after.OriginalFolderFlags);
        Assert.True(File.Exists(markerPath + ".bad"), "the unreadable marker was not set aside");
    }

    /// <summary>
    /// The two halves of the unreadable-marker rule: an ordinary give-back refuses to guess, and the
    /// emergency one assumes the icons were shown — while leaving every setting it has no record of
    /// exactly where the user left it.
    /// </summary>
    [Fact]
    public async Task AnUnreadableMarker_IsRefusedByAnOrdinaryGiveBackAndAssumedByTheEmergencyOne()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_TAKEOVER_LIVE") is null)
        {
            return;
        }

        var markerPath = Path.Combine(Path.GetTempPath(), $"muralis-takeover-live-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(markerPath, "{ this is not the marker that was written ");

        using var service = new DesktopTakeoverService(
            NullLoggerFactory.Instance,
            NullLogger<DesktopTakeoverService>.Instance,
            new DesktopTakeoverRecordStore(NullLogger<DesktopTakeoverRecordStore>.Instance, markerPath));

        var before = await service.ReadNativeStateAsync();
        if (!before.Observed || !before.OriginalIconsVisible)
        {
            // The emergency give-back assumes the icons were shown. On a desktop that keeps them
            // hidden, running it would be a change the check itself made, so the check stays out.
            return;
        }

        var refused = await service.ReleaseAsync();
        Probe($"unreadable ordinary give back: state={refused.State} error={refused.Error}");
        Assert.Equal(DesktopTakeoverState.RecoveryRequired, refused.State);
        Assert.NotNull(refused.Error);

        var emergency = await service.RestoreNativeDesktopAsync();
        var after = await service.ReadNativeStateAsync();
        Probe($"unreadable emergency give back: state={emergency.State} error={emergency.Error}");
        Probe($"unreadable after: visible={after.OriginalIconsVisible} flags=0x{after.OriginalFolderFlags:X8}");

        Assert.Equal(DesktopTakeoverState.Native, emergency.State);
        Assert.True(after.OriginalIconsVisible);
        Assert.Equal(before.OriginalFolderFlags, after.OriginalFolderFlags);
    }

    private static void Probe(string step) =>
        File.AppendAllText(
            Path.Combine(AppContext.BaseDirectory, "takeover-live.log"),
            $"{DateTime.Now:HH:mm:ss.fff} {step}{Environment.NewLine}");
}
