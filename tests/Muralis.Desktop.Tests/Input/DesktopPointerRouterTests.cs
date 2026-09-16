using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Desktop.Input;
using Xunit;

namespace Muralis.Desktop.Tests.Input;

/// <summary>
/// The router's contract, driven through its seam: any pointer source, any clock, no Windows. A
/// report only wakes the router up — what it publishes is one reading of the pointer when the flush
/// timer fires — so a test sets the reading, sends reports, flushes and checks what subscribers saw.
/// </summary>
public sealed class DesktopPointerRouterTests
{
    [Fact]
    public void WithNoSubscribersAReportIsNotEvenRead()
    {
        var sampler = new FakeSampler();
        using var router = CreateRouter(sampler);

        Assert.False(router.HandlePointerReport());

        Assert.Equal(0, sampler.Reads);
        Assert.Equal(0, router.Stats.Dispatches);
    }

    [Fact]
    public void ComingOntoTheDesktopAnnouncesTheEntryAndTheMove()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.HandlePointerReport();
        Assert.Empty(log);

        router.HandleFlushTimer(1_006);

        Assert.Equal(["entered", "moved"], log);
        Assert.Equal(DesktopPointerContext.Desktop, router.Current.Context);
        Assert.Equal(100, router.Current.X);
        Assert.Equal(200, router.Current.Y);
    }

    [Fact]
    public void MovingInsideTheDesktopOnlyReportsTheMove()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);
        sampler.Reading = Desktop(140, 260);
        router.HandlePointerReport();
        router.HandleFlushTimer(1_016);

        Assert.Equal(["entered", "moved", "moved"], log);
    }

    [Fact]
    public void AReportThatChangedNothingPublishesNothing()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);
        router.HandlePointerReport();
        router.HandleFlushTimer(1_016);

        Assert.Equal(["entered", "moved"], log);
    }

    [Fact]
    public void AnOrdinaryApplicationWindowSilencesTheDesktop()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);
        sampler.Reading = Foreign(700, 400);
        router.HandlePointerReport();
        router.HandleFlushTimer(1_016);

        // Being over a browser must not look like a move on the desktop: no pointer event at all.
        Assert.Equal(["entered", "moved", "left"], log);
        Assert.Equal(DesktopPointerContext.Foreign, router.Current.Context);
    }

    [Fact]
    public void MovingAroundInsideAForeignWindowPublishesNothingMore()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);
        sampler.Reading = Foreign(700, 400);
        router.HandlePointerReport();
        router.HandleFlushTimer(1_016);
        sampler.Reading = Foreign(720, 430);
        router.HandlePointerReport();
        router.HandleFlushTimer(1_026);
        sampler.Reading = Foreign(740, 460);
        router.HandlePointerReport();
        router.HandleFlushTimer(1_036);

        Assert.Equal(["entered", "moved", "left"], log);
    }

    [Fact]
    public void ComingBackOntoTheDesktopAnnouncesTheEntryAgain()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);
        sampler.Reading = Foreign(700, 400);
        router.HandlePointerReport();
        router.HandleFlushTimer(1_016);
        sampler.Reading = Desktop(300, 200);
        router.HandlePointerReport();
        router.HandleFlushTimer(1_026);

        Assert.Equal(["entered", "moved", "left", "entered", "moved"], log);

        // The reading that came back is the one that counts as the move, even if the coordinates
        // match an earlier reading: the desktop was left in between.
        Assert.Equal(300, router.Current.X);
    }

    [Fact]
    public void OurOwnSurfaceCountsAsTheDesktop()
    {
        var sampler = new FakeSampler { Reading = new(50, 60, DesktopPointerContext.Surface, DesktopPointerButtons.None) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);

        Assert.Equal(["entered", "moved"], log);
        Assert.True(router.Current.IsOverDesktop);
    }

    [Fact]
    public void APointerThatCannotBeReadIsTreatedAsLeavingTheDesktop()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);
        sampler.Fail = true;
        router.HandlePointerReport();
        router.HandleFlushTimer(1_016);

        Assert.Equal(["entered", "moved", "left"], log);
        Assert.Equal(DesktopPointerState.Unknown, router.Current);
    }

    [Fact]
    public void BurstReportsInsideTheIntervalAreCoalescedIntoOne()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        Assert.True(router.HandlePointerReport());
        Assert.False(router.HandlePointerReport());
        Assert.False(router.HandlePointerReport());
        Assert.False(router.HandlePointerReport());
        Assert.Empty(log);

        // The burst collapses into one reading, and that reading is taken when the flush fires: the
        // pointer as it is now, not as the skipped reports said it was.
        sampler.Reading = Desktop(500, 600);
        router.HandleFlushTimer(1_006);

        Assert.Equal(["entered", "moved"], log);
        Assert.Equal(500, router.Current.X);
        Assert.Equal(600, router.Current.Y);
    }

    [Fact]
    public void TheReadingIsTakenWhenTheFlushFiresNotWhenTheReportArrives()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        Record(router);

        router.HandlePointerReport();

        // Nothing was read while the report was handled: the position is still the old one there,
        // and reading it would pin the pointer one move behind.
        Assert.Equal(0, sampler.Reads);

        // The move lands in the system between the report and the flush, which is exactly what the
        // delay is for.
        sampler.Reading = Desktop(300, 400);
        router.HandleFlushTimer(1_006);

        Assert.Equal(1, sampler.Reads);
        Assert.Equal(300, router.Current.X);
        Assert.Equal(400, router.Current.Y);
    }

    [Fact]
    public void OnlyOneFlushIsArmedPerBurst()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        Record(router);

        router.HandlePointerReport();
        Assert.False(router.HandlePointerReport());
        Assert.False(router.HandlePointerReport());
        Assert.False(router.HandlePointerReport());

        // One flush, one dispatch. The reports behind the first one ask for a verification look
        // afterwards (see TheTailOfACoalescedBurstIsReadOnceMore), and only once that is done is
        // the router free to arm again.
        router.HandleFlushTimer(1_006);
        Assert.Equal(1, router.Stats.Dispatches);
        router.HandleFlushTimer(1_016);
        Assert.Equal(2, router.Stats.Dispatches);

        Assert.True(router.HandlePointerReport());
        Assert.False(router.HandlePointerReport());
        router.HandleFlushTimer(1_026);
        Assert.Equal(3, router.Stats.Dispatches);
    }

    [Fact]
    public void TheTailOfACoalescedBurstIsReadOnceMore()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        // A burst: the first report arms the flush, the one behind it is its tail.
        router.HandlePointerReport();
        Assert.False(router.HandlePointerReport());

        // The flush reads before the tail's move has landed, so its reading is one move behind.
        router.HandleFlushTimer(1_006);
        Assert.Equal(100, router.Current.X);

        // The look the tail asked for catches up with the move the burst ended on.
        sampler.Reading = Desktop(300, 200);
        router.HandleFlushTimer(1_022);

        Assert.Equal(300, router.Current.X);
        Assert.Equal(["entered", "moved", "moved"], log);

        // One verification, not a chain: the tail is settled and the next burst can arm again.
        Assert.True(router.HandlePointerReport());
    }

    [Fact]
    public void AFlushThatArrivesWithoutSubscribersDoesNothing()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var moved = 0;
        void OnMoved(object? sender, DesktopPointerEventArgs e) => moved++;
        router.PointerMoved += OnMoved;
        router.HandlePointerReport();
        router.PointerMoved -= OnMoved;

        router.HandleFlushTimer(1_006);

        Assert.Equal(0, moved);
        Assert.Equal(0, router.Stats.Dispatches);
    }

    [Fact]
    public void SampleOnceReadsThePointerWithoutAReport()
    {
        var sampler = new FakeSampler { Reading = Desktop(320, 240) };
        using var router = CreateRouter(sampler);
        var log = Record(router);

        router.SampleOnce();

        Assert.Equal(["entered", "moved"], log);
        Assert.Equal(1, sampler.Reads);
    }

    [Fact]
    public void SampleOnceWithNoSubscribersDoesNotRead()
    {
        var sampler = new FakeSampler { Reading = Desktop(320, 240) };
        using var router = CreateRouter(sampler);

        router.SampleOnce();

        Assert.Equal(0, sampler.Reads);
    }

    [Fact]
    public void SampleOnceCountsAsADispatchForTheCoalescing()
    {
        var sampler = new FakeSampler { Reading = Desktop(320, 240) };
        using var router = CreateRouter(sampler);
        Record(router);

        router.SampleOnce();
        Assert.Equal(1, sampler.Reads);

        // A report right after the one-off read waits its turn: inside the interval the flush holds
        // the report back instead of reading the pointer a second time.
        Assert.True(router.HandlePointerReport());
        router.HandleFlushTimer(1_003);
        Assert.Equal(1, sampler.Reads);

        router.HandleFlushTimer(1_009);
        Assert.Equal(2, sampler.Reads);
    }

    [Fact]
    public void TheStatsCountWhatActuallyHappened()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        Record(router);

        router.HandlePointerReport();
        router.HandlePointerReport();
        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);

        var stats = router.Stats;
        Assert.Equal(3, stats.Reports);
        Assert.Equal(1, stats.Dispatches);
        Assert.Equal(DesktopPointerContext.Desktop, stats.Context);
    }

    [Fact]
    public void DetachWithoutAMountResetsTheReadingAndIsSafeToRepeat()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        Record(router);
        router.HandlePointerReport();

        router.Detach();
        router.Detach();

        Assert.False(router.IsAttached);
        Assert.Equal(DesktopPointerState.Unknown, router.Current);
        Assert.Equal(DesktopPointerContext.Foreign, router.Stats.Context);
    }

    [Fact]
    public void ASubscriberThatThrowsDoesNotStopTheOthers()
    {
        var sampler = new FakeSampler { Reading = Desktop(100, 200) };
        using var router = CreateRouter(sampler);
        var seen = 0;
        router.PointerMoved += (_, _) => throw new InvalidOperationException("The subscriber fell over.");
        router.PointerMoved += (_, _) => seen++;

        router.HandlePointerReport();
        router.HandleFlushTimer(1_006);

        Assert.Equal(1, seen);
    }

    private static (int X, int Y, DesktopPointerContext Context, DesktopPointerButtons Buttons) Desktop(int x, int y) =>
        (x, y, DesktopPointerContext.Desktop, DesktopPointerButtons.None);

    private static (int X, int Y, DesktopPointerContext Context, DesktopPointerButtons Buttons) Foreign(int x, int y) =>
        (x, y, DesktopPointerContext.Foreign, DesktopPointerButtons.None);

    private static DesktopPointerRouter CreateRouter(FakeSampler sampler) =>
        new(NullLogger<DesktopPointerRouter>.Instance, sampler, () => 1_000);

    /// <summary>Watches the three events as one ordered list of what a subscriber saw.</summary>
    private static List<string> Record(DesktopPointerRouter router)
    {
        var log = new List<string>();
        router.EnteredDesktopRegion += (_, _) => log.Add("entered");
        router.LeftDesktopRegion += (_, _) => log.Add("left");
        router.PointerMoved += (_, _) => log.Add("moved");
        return log;
    }

    private sealed class FakeSampler : IDesktopPointerSampler
    {
        internal (int X, int Y, DesktopPointerContext Context, DesktopPointerButtons Buttons) Reading { get; set; } =
            (0, 0, DesktopPointerContext.Foreign, DesktopPointerButtons.None);

        internal bool Fail { get; set; }

        internal int Reads { get; private set; }

        public bool TryRead(out int x, out int y, out DesktopPointerButtons buttons, out DesktopPointerContext context)
        {
            Reads++;
            (x, y, context, buttons) = Reading;
            return !Fail;
        }
    }
}
