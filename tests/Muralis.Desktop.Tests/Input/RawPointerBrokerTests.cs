using System.Collections.Concurrent;
using Muralis.Desktop.Input;
using Xunit;

namespace Muralis.Desktop.Tests.Input;

public sealed class RawPointerBrokerTests
{
    [Fact]
    public void FirstConsumerStartsOneRegistrationAndAdditionalConsumersShareIt()
    {
        var source = new FakeRegistration();
        using var broker = new RawPointerBroker(() => source);

        using var dock = broker.Subscribe((_, _) => { });
        using var wallpaper = broker.Subscribe((_, _) => { });

        Assert.Equal(1, source.OpenCalls);
        Assert.Equal(2, broker.ConsumerCount);
        Assert.True(broker.IsRegistered);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DockAndWallpaperReceiveTheSameRegistrationRegardlessOfStartOrder(bool dockFirst)
    {
        var source = new FakeRegistration();
        using var broker = new RawPointerBroker(() => source);
        var dockMoves = 0;
        var wallpaperMoves = 0;

        using var first = dockFirst
            ? broker.Subscribe((_, _) => dockMoves++)
            : broker.Subscribe((_, _) => wallpaperMoves++);
        using var second = dockFirst
            ? broker.Subscribe((_, _) => wallpaperMoves++)
            : broker.Subscribe((_, _) => dockMoves++);

        source.Emit(120, 240);

        Assert.Equal(1, source.OpenCalls);
        Assert.Equal(1, dockMoves);
        Assert.Equal(1, wallpaperMoves);
    }

    [Fact]
    public void RemovingEitherConsumerLeavesTheOtherAliveAndLastConsumerReleasesRegistration()
    {
        var source = new FakeRegistration();
        using var broker = new RawPointerBroker(() => source);
        var dockMoves = 0;
        var wallpaperMoves = 0;
        var dock = broker.Subscribe((_, _) => dockMoves++);
        var wallpaper = broker.Subscribe((_, _) => wallpaperMoves++);

        wallpaper.Dispose();
        source.Emit(1, 2);

        Assert.Equal(1, dockMoves);
        Assert.Equal(0, wallpaperMoves);
        Assert.False(source.Disposed);

        dock.Dispose();
        Assert.True(source.Disposed);
        Assert.False(broker.IsRegistered);
    }

    [Fact]
    public void ANewLifecycleAfterAllConsumersLeaveCreatesOneFreshOwner()
    {
        var sources = new List<FakeRegistration>();
        using var broker = new RawPointerBroker(() =>
        {
            var source = new FakeRegistration();
            sources.Add(source);
            return source;
        });

        broker.Subscribe((_, _) => { }).Dispose();
        broker.Subscribe((_, _) => { }).Dispose();

        Assert.Equal(2, sources.Count);
        Assert.All(sources, source =>
        {
            Assert.Equal(1, source.OpenCalls);
            Assert.True(source.Disposed);
        });
    }

    [Fact]
    public void ConcurrentConsumerLifecycleStillCreatesAndReleasesOneOwner()
    {
        var source = new FakeRegistration();
        using var broker = new RawPointerBroker(() => source);
        var leases = new ConcurrentBag<IDisposable>();

        Parallel.For(0, 64, _ => leases.Add(broker.Subscribe((_, _) => { })));

        Assert.Equal(1, source.OpenCalls);
        Assert.Equal(64, broker.ConsumerCount);

        Parallel.ForEach(leases, lease => lease.Dispose());

        Assert.Equal(0, broker.ConsumerCount);
        Assert.True(source.Disposed);
    }

    private sealed class FakeRegistration : IRawPointerRegistration
    {
        public event Action<int, int>? Moved;

        public bool IsRegistered { get; private set; }

        public long Reports { get; private set; }

        public long Messages => Reports;

        public int OpenCalls { get; private set; }

        public bool Disposed { get; private set; }

        public bool Open()
        {
            OpenCalls++;
            IsRegistered = true;
            return true;
        }

        public void RequestDump()
        {
        }

        public void Emit(int x, int y)
        {
            Reports++;
            Moved?.Invoke(x, y);
        }

        public void Dispose()
        {
            IsRegistered = false;
            Disposed = true;
        }
    }
}
