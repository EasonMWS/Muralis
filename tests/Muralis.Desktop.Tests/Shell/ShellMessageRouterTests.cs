using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Desktop.Interop;
using Muralis.Desktop.Shell;
using Xunit;

namespace Muralis.Desktop.Tests.Shell;

public sealed class ShellMessageRouterTests
{
    private const uint TaskbarCreated = 0xC123;

    [Fact]
    public void Handle_TaskbarCreated_RaisesShellRestarted()
    {
        using var router = CreateRouter();
        var raised = 0;
        router.ShellRestarted += (_, _) => raised++;

        var consumed = router.Handle(TaskbarCreated);

        Assert.True(consumed);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Handle_DisplayChange_RaisesDisplaysChanged()
    {
        using var router = CreateRouter();
        var shellRestarts = 0;
        var displayChanges = 0;
        router.ShellRestarted += (_, _) => shellRestarts++;
        router.DisplaysChanged += (_, _) => displayChanges++;

        var consumed = router.Handle(NativeMethods.WmDisplayChange);

        Assert.True(consumed);
        Assert.Equal(0, shellRestarts);
        Assert.Equal(1, displayChanges);
    }

    [Fact]
    public void Handle_UnrelatedMessage_IsNotConsumedAndRaisesNothing()
    {
        using var router = CreateRouter();
        var raised = 0;
        router.ShellRestarted += (_, _) => raised++;
        router.DisplaysChanged += (_, _) => raised++;

        var consumed = router.Handle(0x0113);

        Assert.False(consumed);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Handle_AfterDispose_DoesNotDispatch()
    {
        var router = CreateRouter();
        var raised = 0;
        router.ShellRestarted += (_, _) => raised++;
        router.DisplaysChanged += (_, _) => raised++;

        router.Dispose();

        Assert.False(router.Handle(TaskbarCreated));
        Assert.False(router.Handle(NativeMethods.WmDisplayChange));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var router = CreateRouter();

        router.Dispose();
        router.Dispose();

        Assert.False(router.Handle(TaskbarCreated));
    }

    [Fact]
    public void Handle_RepeatedShellRestarts_RaisesOncePerMessage()
    {
        using var router = CreateRouter();
        var raised = 0;
        router.ShellRestarted += (_, _) => raised++;

        router.Handle(TaskbarCreated);
        router.Handle(TaskbarCreated);
        router.Handle(TaskbarCreated);

        Assert.Equal(3, raised);
    }

    [Fact]
    public void Handle_WithMultipleSubscribers_InvokesEachSubscriberOnce()
    {
        using var router = CreateRouter();
        var first = 0;
        var second = 0;
        router.ShellRestarted += (_, _) => first++;
        router.ShellRestarted += (_, _) => second++;

        router.Handle(TaskbarCreated);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void Handle_FailingSubscriber_DoesNotBlockTheOthers()
    {
        using var router = CreateRouter();
        var survived = 0;
        router.ShellRestarted += (_, _) => throw new InvalidOperationException("subscriber failed");
        router.ShellRestarted += (_, _) => survived++;

        var consumed = router.Handle(TaskbarCreated);

        Assert.True(consumed);
        Assert.Equal(1, survived);
    }

    [Fact]
    public void Handle_ZeroMessage_IsNotConsumed()
    {
        // A failed TaskbarCreated registration yields id 0; it must not be mistaken for a real
        // shell message (WM_NULL carries no meaning here).
        using var router = CreateRouter(taskbarCreatedMessage: 0);
        var raised = 0;
        router.ShellRestarted += (_, _) => raised++;

        Assert.False(router.Handle(NativeMethods.WmNull));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void UnsubscribedHandler_IsNotInvoked()
    {
        using var router = CreateRouter();
        var raised = 0;
        EventHandler handler = (_, _) => raised++;

        router.ShellRestarted += handler;
        router.ShellRestarted -= handler;
        router.Handle(TaskbarCreated);

        Assert.Equal(0, raised);
    }

    private static ShellMessageRouter CreateRouter(uint taskbarCreatedMessage = TaskbarCreated) =>
        new(taskbarCreatedMessage, NullLogger.Instance);
}
