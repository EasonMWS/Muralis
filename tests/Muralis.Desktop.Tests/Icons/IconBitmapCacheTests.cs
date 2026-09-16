using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Desktop.Icons;
using Xunit;

namespace Muralis.Desktop.Tests.Icons;

/// <summary>
/// Covers the icon store's promises: an icon is read from the shell once however often the canvas
/// asks, a request is answered off the requesting thread, and the store stays inside its byte
/// budget by letting the longest-unused icons go first. The reader is injected, so none of this
/// needs the shell.
/// </summary>
public sealed class IconBitmapCacheTests
{
    private const long ByteBudget = 32L * 1024 * 1024;

    [Fact]
    public void TryGet_BeforeAnyRequest_GivesNothing()
    {
        using var cache = new IconBitmapCache(NullLogger.Instance, (_, _) => Icon(16));

        Assert.Null(cache.TryGet(@"C:\nowhere\thing.exe", 96));
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public void Request_StoresTheIconAndSaysWhenItHasArrived()
    {
        using var cache = new IconBitmapCache(NullLogger.Instance, (_, _) => Icon(32));
        using var arrived = new ManualResetEventSlim();
        cache.BitmapArrived += arrived.Set;

        cache.Request(@"C:\apps\thing.exe", 96);

        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "the icon never arrived");
        var bitmap = cache.TryGet(@"C:\apps\thing.exe", 96);
        Assert.NotNull(bitmap);
        Assert.Equal(32, bitmap!.Width);
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public void Request_ForAnIconAlreadyHeld_DoesNotReadItAgain()
    {
        var reads = 0;
        using var cache = new IconBitmapCache(NullLogger.Instance, (_, _) =>
        {
            Interlocked.Increment(ref reads);
            return Icon(32);
        });
        using var arrived = new ManualResetEventSlim();
        cache.BitmapArrived += arrived.Set;

        cache.Request(@"C:\apps\thing.exe", 96);
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "the icon never arrived");

        // Asking again — as a second item with the same target would — is answered from the store.
        cache.Request(@"C:\apps\thing.exe", 96);
        Thread.Sleep(100);

        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public void Request_ForAnotherSize_IsItsOwnIcon()
    {
        var reads = 0;
        using var cache = new IconBitmapCache(NullLogger.Instance, (_, _) =>
        {
            Interlocked.Increment(ref reads);
            return Icon(32);
        });
        using var arrived = new ManualResetEventSlim();
        cache.BitmapArrived += arrived.Set;

        cache.Request(@"C:\apps\thing.exe", 48);
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "the first size never arrived");

        arrived.Reset();
        cache.Request(@"C:\apps\thing.exe", 96);
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "the second size never arrived");

        Assert.Equal(2, Volatile.Read(ref reads));
        Assert.Equal(2, cache.EntryCount);
        Assert.NotNull(cache.TryGet(@"C:\apps\thing.exe", 48));
        Assert.NotNull(cache.TryGet(@"C:\apps\thing.exe", 96));
    }

    [Fact]
    public void Request_WithNoTargetOrNoSize_IsNeverRead()
    {
        var reads = 0;
        using var cache = new IconBitmapCache(NullLogger.Instance, (_, _) =>
        {
            Interlocked.Increment(ref reads);
            return Icon(32);
        });

        cache.Request("   ", 96);
        cache.Request(@"C:\apps\thing.exe", 0);
        Thread.Sleep(100);

        Assert.Equal(0, Volatile.Read(ref reads));
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public void Request_WhenTheShellHasNothing_StoresNothing()
    {
        var reads = 0;
        using var cache = new IconBitmapCache(NullLogger.Instance, (_, _) =>
        {
            Interlocked.Increment(ref reads);
            return null;
        });

        cache.Request(@"C:\gone\thing.exe", 96);
        WaitFor(() => Volatile.Read(ref reads) == 1);

        Assert.Null(cache.TryGet(@"C:\gone\thing.exe", 96));
        Assert.Equal(0, cache.EntryCount);

        // And a second ask is not dropped by a stale "already queued" mark: the shell is asked again,
        // because the first answer was nothing.
        cache.Request(@"C:\gone\thing.exe", 96);
        WaitFor(() => Volatile.Read(ref reads) == 2);
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public void Store_PastTheBudget_LetsTheLongestUnusedIconGoFirst()
    {
        // Each icon is a megabyte, so forty of them cannot all stay inside the budget.
        using var cache = new IconBitmapCache(NullLogger.Instance, (_, _) => Icon(512));
        using var arrived = new ManualResetEventSlim();
        cache.BitmapArrived += arrived.Set;

        for (var i = 0; i < 40; i++)
        {
            arrived.Reset();
            cache.Request($@"C:\apps\thing-{i}.exe", 96);
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), $"icon {i} never arrived");
        }

        Assert.True(cache.EntryCount < 40, "the budget let every icon stay");
        Assert.True(cache.ByteCount <= ByteBudget, "the store holds more bytes than its budget allows");
        Assert.NotNull(cache.TryGet(@"C:\apps\thing-39.exe", 96));
        Assert.Null(cache.TryGet(@"C:\apps\thing-0.exe", 96));
    }

    [Fact]
    public void Dispose_StopsTheReaderTakingNewWork()
    {
        var reads = 0;
        var cache = new IconBitmapCache(NullLogger.Instance, (_, _) =>
        {
            Interlocked.Increment(ref reads);
            return Icon(32);
        });

        cache.Dispose();
        cache.Request(@"C:\apps\thing.exe", 96);
        Thread.Sleep(100);

        Assert.Equal(0, Volatile.Read(ref reads));
    }

    private static IconBitmap Icon(int size)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, (byte)255);
        return new IconBitmap(size, size, pixels);
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the icon reader did not get there in time");
            Thread.Sleep(10);
        }
    }
}
