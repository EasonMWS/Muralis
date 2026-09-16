using Muralis.Desktop.Input;
using Xunit;

namespace Muralis.Desktop.Tests.Input;

public sealed class DesktopGestureRecognizerTests
{
    /// <summary>
    /// The recognizer most tests use: a 4x4 pixel drag rectangle (so a press may wander two pixels)
    /// and a 4x4 pixel, 500 ms double-click rule.
    /// </summary>
    private static DesktopGestureRecognizer Recognizer() => new(4, 4, 4, 4, 500);

    [Fact]
    public void APressThatStaysPutIsAClick()
    {
        var gestures = Recognizer();

        gestures.Press(100, 100);

        Assert.True(gestures.IsPressed);
        Assert.False(gestures.IsDragging);
        Assert.Equal(DesktopGesture.Click, gestures.Release(101, 101, 1_000));
        Assert.False(gestures.IsPressed);
    }

    [Fact]
    public void MovementInsideTheDragRectangleIsStillAClick()
    {
        var gestures = Recognizer();
        gestures.Press(100, 100);

        Assert.False(gestures.Move(102, 102));
        Assert.False(gestures.IsDragging);
        Assert.Equal(DesktopGesture.Click, gestures.Release(102, 102, 1_000));
    }

    [Fact]
    public void MovementBeyondTheDragRectangleIsADragFromThenOn()
    {
        var gestures = Recognizer();
        gestures.Press(100, 100);

        Assert.True(gestures.Move(103, 100));
        Assert.True(gestures.IsDragging);
        Assert.Equal(DesktopGesture.DragEnd, gestures.Release(310, 240, 1_400));
    }

    [Fact]
    public void ACompletedDragIsNeverAClick()
    {
        var gestures = Recognizer();
        gestures.Press(100, 100);
        Assert.True(gestures.Move(140, 100));

        // Back on the press point and released instantly: still the end of a drag, not a click.
        Assert.Equal(DesktopGesture.DragEnd, gestures.Release(100, 100, 1_020));
    }

    [Fact]
    public void TheSecondClickInsideTheTimeAndPlaceIsADoubleClick()
    {
        var gestures = Recognizer();

        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(100, 100, 1_000));

        gestures.Press(101, 102);
        Assert.Equal(DesktopGesture.DoubleClick, gestures.Release(101, 102, 1_300));
    }

    [Fact]
    public void TheSecondClickTooLateInTimeIsAnotherClick()
    {
        var gestures = Recognizer();

        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(100, 100, 1_000));

        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(100, 100, 1_501));
    }

    [Fact]
    public void TheSecondClickTooFarAwayIsAnotherClick()
    {
        var gestures = Recognizer();

        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(100, 100, 1_000));

        gestures.Press(104, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(104, 100, 1_100));
    }

    [Fact]
    public void AThirdClickAfterADoubleClickStartsOver()
    {
        var gestures = Recognizer();

        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(100, 100, 1_000));
        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.DoubleClick, gestures.Release(100, 100, 1_200));

        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(100, 100, 1_300));
    }

    [Fact]
    public void ADragEndsTheClickChain()
    {
        var gestures = Recognizer();

        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(100, 100, 1_000));

        gestures.Press(100, 100);
        Assert.True(gestures.Move(200, 100));
        Assert.Equal(DesktopGesture.DragEnd, gestures.Release(200, 100, 1_100));

        // The click before the drag is not half of a double-click any more.
        gestures.Press(100, 100);
        Assert.Equal(DesktopGesture.Click, gestures.Release(100, 100, 1_150));
    }

    [Fact]
    public void AReleaseWithoutAPressIsNoGesture()
    {
        Assert.Equal(DesktopGesture.None, Recognizer().Release(100, 100, 1_000));
    }

    [Fact]
    public void CancellingEndsThePressWithoutAGesture()
    {
        var gestures = Recognizer();
        gestures.Press(100, 100);
        gestures.Move(300, 300);

        gestures.Cancel();

        Assert.False(gestures.IsPressed);
        Assert.False(gestures.IsDragging);
        Assert.Equal(DesktopGesture.None, gestures.Release(300, 300, 1_100));
    }

    [Fact]
    public void MovementWithoutAPressIsNoDrag()
    {
        Assert.False(Recognizer().Move(500, 500));
    }

    [Fact]
    public void TheSystemRecognizerTreatsALongMoveAsADrag()
    {
        // Whatever the user's settings are, a move of a hundred pixels is a drag: the metric is at
        // most SM_CXDRAG wide, which is a small number of pixels on every sensible system.
        var gestures = DesktopGestureRecognizer.ForThisSystem();

        gestures.Press(100, 100);

        Assert.True(gestures.Move(200, 100));
    }
}
