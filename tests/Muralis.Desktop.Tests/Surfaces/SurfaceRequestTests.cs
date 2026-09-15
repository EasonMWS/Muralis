using Muralis.Core.Models;
using Muralis.Desktop.Surfaces;
using Xunit;

namespace Muralis.Desktop.Tests.Surfaces;

public sealed class SurfaceRequestTests
{
    [Fact]
    public void Validate_AcceptsBackdropOnStableDisplay()
    {
        var request = new SurfaceRequest(new BackdropContent(), new MonitorRef("edid:left"));

        Assert.Empty(request.Validate());
    }

    [Fact]
    public void Validate_RequiresStableDisplayId()
    {
        var request = new SurfaceRequest(new BackdropContent(), default);

        Assert.NotEmpty(request.Validate());
    }

    [Fact]
    public void Validate_RejectsBackdropThatTakesInput()
    {
        var request = new SurfaceRequest(
            new BackdropContent(interaction: SurfaceInteraction.Pointer), new MonitorRef("edid:left"));

        Assert.NotEmpty(request.Validate());
    }

    [Fact]
    public void Validate_RejectsBackdropThatActivates()
    {
        var request = new SurfaceRequest(
            new BackdropContent(activation: SurfaceActivation.OnClick), new MonitorRef("edid:left"));

        Assert.NotEmpty(request.Validate());
    }

    private sealed class BackdropContent(
        SurfaceInteraction interaction = SurfaceInteraction.None,
        SurfaceActivation activation = SurfaceActivation.Never) : ISurfaceContent
    {
        public SurfaceKind Kind => SurfaceKind.Backdrop;

        public SurfaceInteraction Interaction => interaction;

        public SurfaceActivation Activation => activation;

        public Task MountAsync(ISurfaceTarget target, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UnmountAsync() => Task.CompletedTask;

        public void OnGeometryChanged(MonitorGeometry geometry, double scale)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
