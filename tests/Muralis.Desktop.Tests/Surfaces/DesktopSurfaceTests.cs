using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Models;
using Muralis.Desktop.Surfaces;
using Xunit;

namespace Muralis.Desktop.Tests.Surfaces;

public sealed class DesktopSurfaceTests
{
    private static readonly MonitorRef Display = new("primary");
    private static readonly PixelRect DisplayBounds = new(0, 0, 2560, 1440);

    /// <summary>A handle USER32 is guaranteed to reject, so the surface never touches a real window.</summary>
    private static readonly nint FakeWindow = unchecked((nint)0x7FFFFFFF00000001);

    [Fact]
    public async Task AttachAsync_MountsContentAndRecordsTheTarget()
    {
        var content = new FakeContent();
        var surface = CreateSurface(content);

        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);

        Assert.Equal(SurfaceState.Mounted, surface.State);
        Assert.Equal(1, content.Mounts);
        Assert.Equal(DisplayBounds, surface.Bounds);
        Assert.Equal(0, surface.RemountAttempts);
    }

    [Fact]
    public async Task AttachAsync_RejectsTargetsThatAreNotWindows()
    {
        var surface = CreateSurface(new FakeContent());

        await Assert.ThrowsAsync<ArgumentException>(
            () => surface.AttachAsync(new AlienTarget(), CancellationToken.None));

        Assert.Equal(SurfaceState.Detached, surface.State);
    }

    [Fact]
    public async Task AttachAsync_WhenMountFails_LeavesTheSurfaceDetachedAndUnmounts()
    {
        var content = new FakeContent { FailMount = true };
        var surface = CreateSurface(content);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => surface.AttachAsync(new FakeTarget(), CancellationToken.None));

        Assert.Equal(SurfaceState.Detached, surface.State);
        Assert.Equal(nint.Zero, surface.WindowHandle);
        Assert.Equal(1, content.Unmounts);

        // A failed mount must not poison the surface: the shell retries with a fresh window.
        content.FailMount = false;
        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);
        Assert.Equal(SurfaceState.Mounted, surface.State);
    }

    [Fact]
    public async Task Detach_ReleasesTheMountAndToleratesRepeatedCalls()
    {
        var content = new FakeContent();
        var surface = CreateSurface(content);
        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);

        surface.Detach();

        Assert.Equal(SurfaceState.Detached, surface.State);
        Assert.Equal(nint.Zero, surface.WindowHandle);
        Assert.Equal(1, content.Unmounts);

        surface.Detach();

        Assert.Equal(SurfaceState.Detached, surface.State);
        Assert.Equal(2, content.Unmounts);
    }

    [Fact]
    public async Task MoveTo_UpdatesBoundsAndNotifiesTheContent()
    {
        var content = new FakeContent();
        var surface = CreateSurface(content);
        await surface.AttachAsync(new FakeTarget(scale: 1.75), CancellationToken.None);

        var moved = new MonitorGeometry(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040));
        surface.MoveTo(Display, moved);

        Assert.Equal(moved.Bounds, surface.Bounds);
        Assert.Equal(moved, content.LastGeometry);
        Assert.Equal(1.75, content.LastScale);
    }

    [Fact]
    public async Task MarkOrphaned_ReleasesTheMountButKeepsTheSurfaceRemountable()
    {
        var content = new FakeContent();
        var surface = CreateSurface(content);
        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);

        Assert.True(surface.MarkOrphaned());

        Assert.Equal(SurfaceState.Orphaned, surface.State);
        Assert.Equal(nint.Zero, surface.WindowHandle);
        Assert.Equal(1, content.Unmounts);

        // The re-mount keeps the same content: the intent to show was never given up.
        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);

        Assert.Equal(SurfaceState.Mounted, surface.State);
        Assert.Equal(2, content.Mounts);
        Assert.Equal(0, surface.RemountAttempts);
    }

    [Fact]
    public async Task MarkOrphaned_WithoutALiveMount_ReturnsFalse()
    {
        var surface = CreateSurface(new FakeContent());

        Assert.False(surface.MarkOrphaned());
        Assert.Equal(SurfaceState.Detached, surface.State);

        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);
        surface.Detach();

        Assert.False(surface.MarkOrphaned());
        Assert.Equal(SurfaceState.Detached, surface.State);
    }

    [Fact]
    public async Task OnWindowDestroyed_ReportsTheLossOnlyOncePerMount()
    {
        var losses = 0;
        var surface = CreateSurface(new FakeContent(), _ => losses++);
        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);

        surface.OnWindowDestroyed();
        surface.OnWindowDestroyed();

        Assert.Equal(1, losses);
        Assert.Equal(SurfaceState.Orphaned, surface.State);
    }

    [Fact]
    public async Task OnWindowDestroyed_AfterADeliberateDetach_IsIgnored()
    {
        var losses = 0;
        var surface = CreateSurface(new FakeContent(), _ => losses++);
        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);
        surface.Detach();

        surface.OnWindowDestroyed();

        Assert.Equal(0, losses);
        Assert.Equal(SurfaceState.Detached, surface.State);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheMountAndTheContent()
    {
        var content = new FakeContent();
        var surface = CreateSurface(content);
        await surface.AttachAsync(new FakeTarget(), CancellationToken.None);

        await surface.DisposeAsync();

        Assert.Equal(SurfaceState.Detached, surface.State);
        Assert.Equal(1, content.Unmounts);
        Assert.Equal(1, content.Disposals);
    }

    private static DesktopSurface CreateSurface(ISurfaceContent content, Action<DesktopSurface>? onWindowLost = null)
    {
        var host = new Win32SurfaceHost(NullLogger.Instance, onWindowLost ?? (_ => { }));
        return (DesktopSurface)host
            .CreateAsync(new SurfaceRequest(content, Display), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private sealed class FakeTarget(nint window = 0, double scale = 1.0) : IWin32SurfaceTarget
    {
        public nint WindowHandle { get; } = window == nint.Zero ? FakeWindow : window;

        public PixelRect PixelBounds { get; } = DisplayBounds;

        public double ScaleFactor { get; } = scale;

        public SurfaceLayer Layer => SurfaceLayer.WallpaperLayer;
    }

    private sealed class AlienTarget : ISurfaceTarget
    {
        public PixelRect PixelBounds => DisplayBounds;

        public double ScaleFactor => 1.0;

        public SurfaceLayer Layer => SurfaceLayer.WallpaperLayer;
    }

    private sealed class FakeContent : ISurfaceContent
    {
        public bool FailMount { get; set; }

        public int Mounts { get; private set; }

        public int Unmounts { get; private set; }

        public int Disposals { get; private set; }

        public MonitorGeometry? LastGeometry { get; private set; }

        public double LastScale { get; private set; }

        public SurfaceKind Kind => SurfaceKind.Backdrop;

        public SurfaceInteraction Interaction => SurfaceInteraction.None;

        public SurfaceActivation Activation => SurfaceActivation.Never;

        public Task MountAsync(ISurfaceTarget target, CancellationToken cancellationToken)
        {
            Mounts++;
            return FailMount
                ? Task.FromException(new InvalidOperationException("The fake content refused to mount."))
                : Task.CompletedTask;
        }

        public Task UnmountAsync()
        {
            Unmounts++;
            return Task.CompletedTask;
        }

        public void OnGeometryChanged(MonitorGeometry geometry, double scale)
        {
            LastGeometry = geometry;
            LastScale = scale;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
