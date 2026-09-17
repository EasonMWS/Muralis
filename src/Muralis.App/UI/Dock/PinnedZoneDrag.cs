using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Muralis.App.UI.Controls;
using Muralis.App.UI.Motion;
using Muralis.Core.Diagnostics;
using Muralis.Core.Dock;

namespace Muralis.App.UI.Dock;

/// <summary>
/// Turns a press on a pinned icon into a reorder, without reordering anything until the pointer comes
/// up. While the pointer is down the icon is translated by the raw pointer delta, one for one, and the
/// only other thing that happens is a line drawn where the item would land.
/// </summary>
/// <remarks>
/// <para>
/// The carried icon is deliberately not animated while it is being carried: an icon that eased toward
/// the pointer would sit behind the hand, and on a high refresh display that lag is the first thing
/// the user notices. Nothing is written during the drag either — the drop is the single write.
/// </para>
/// <para>
/// The line rather than a moving gap: the neighbours would have to be translated out of the way and
/// then translated back over the layout change that follows the drop, and there is no moment between
/// the two where they can be cleared without a frame drawn at double offset. A line says the same
/// thing and cannot collide with the item the pointer is holding.
/// </para>
/// </remarks>
internal sealed class PinnedZoneDrag
{
    /// <summary>How far the pointer has to travel before a press counts as a drag rather than a click.</summary>
    private const double TravelThreshold = 4;

    /// <summary>The dock's own spacing, used only when the run is too short to measure a pitch from.</summary>
    private const double SlotGap = 4;

    private readonly FrameworkElement _zone;
    private readonly Border _indicator;
    private readonly Func<PinnedAppViewItem, int, Task> _commit;

    private double[] _centres = [];
    private DockIcon? _carried;
    private PinnedAppViewItem? _item;
    private uint _pointerId;
    private double _originX;
    private double _width;
    private int _from;
    private int _target;
    private bool _pressed;
    private bool _travelled;
    private bool _awaitingProjection;

    public PinnedZoneDrag(FrameworkElement zone, Border indicator, Func<PinnedAppViewItem, int, Task> commit)
    {
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _indicator = indicator ?? throw new ArgumentNullException(nameof(indicator));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
    }

    /// <summary>
    /// Whether the pointer has carried an icon far enough for the release to mean a drop. The click
    /// that would otherwise launch the application is suppressed while this is true.
    /// </summary>
    public bool Travelled => _travelled;

    /// <summary>
    /// Takes the pointer for <paramref name="icon"/>. Everything the drag needs is measured here,
    /// while every translation is still zero, so the resting centres are the real ones.
    /// </summary>
    public void Press(IReadOnlyList<DockIcon> icons, DockIcon icon, PointerRoutedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentNullException.ThrowIfNull(args);

        if (_pressed || icons.Count < 2 || icon.Tag is not PinnedAppViewItem item)
        {
            return;
        }

        var from = IndexOf(icons, icon);
        if (from < 0 || !icon.CapturePointer(args.Pointer))
        {
            return;
        }

        _centres = new double[icons.Count];
        for (var i = 0; i < icons.Count; i++)
        {
            _centres[i] = CentreOf(icons[i]);
        }

        _carried = icon;
        _item = item;
        _pointerId = args.Pointer.PointerId;
        _originX = PointerX(args);
        _width = icon.ActualWidth;
        _from = from;
        _target = from;
        _pressed = true;
    }

    /// <summary>
    /// Follows the pointer. Returns whether this drag consumed the move, which it only does once the
    /// press has become a drag.
    /// </summary>
    public bool Move(PointerRoutedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!_pressed || args.Pointer.PointerId != _pointerId)
        {
            return false;
        }

        var delta = PointerX(args) - _originX;
        if (!_travelled)
        {
            if (Math.Abs(delta) < TravelThreshold)
            {
                return false;
            }

            Begin();
        }

        TransformMotion.SetTranslateX(_carried!, delta);

        var target = DockReorder.TargetIndex(_centres[_from] + delta, _centres, _from);
        if (target != _target)
        {
            _target = target;
            PlaceIndicator();
        }

        return true;
    }

    /// <summary>
    /// Ends the drag: the carried icon settles into the place it was shown going to, and only then is
    /// the new order handed to the list that saves it.
    /// </summary>
    public async Task ReleaseAsync()
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        using (DropProfile.Measure("release.capture"))
        {
            _carried!.ReleasePointerCaptures();
        }

        if (!_travelled)
        {
            // A click, not a drag. The launch is the tap handler's business.
            Finish();
            return;
        }

        DropProfile.Begin("release");

        try
        {
            var carried = _carried;
            var from = _from;
            var target = _target;

            using (DropProfile.Measure("release.settle"))
            {
                await TransformMotion
                    .AnimateTranslateXAsync(carried, LandingOffset(), MotionDurations.Standard, new CubicEase { EasingMode = EasingMode.EaseOut })
                    .ConfigureAwait(true);
            }

            if (target != from)
            {
                _awaitingProjection = true;
                using (DropProfile.Measure("release.commit"))
                {
                    await _commit(_item!, target).ConfigureAwait(true);
                }
            }

            // The projection normally clears the translation from inside the same callback that reorders
            // the items, which is what stops the icon being drawn at its old place for a frame. This is
            // the fallback for a drop that was not followed by one, so a carried icon can never be left
            // floating off its own icon.
            if (_awaitingProjection)
            {
                Finish();
            }
        }
        finally
        {
            // Closed after the commit rather than after the drag ended, so the record covers the whole
            // transaction the user was waiting for.
            DropProfile.End();
        }
    }

    /// <summary>Abandons the drag and puts the icon back, without saving anything.</summary>
    public void Cancel()
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        _carried?.ReleasePointerCaptures();
        try
        {
            Finish();
        }
        finally
        {
            DropProfile.End();
        }
    }

    /// <summary>
    /// Called when the pinned list has just been redrawn. A drop that is waiting for its own reorder
    /// clears the translation here, in the same callback as the reorder and before the layout pass,
    /// so the icon is never drawn at both offsets at once.
    /// </summary>
    public void OnProjected()
    {
        if (_awaitingProjection)
        {
            Finish();
        }
    }

    private void Begin()
    {
        _travelled = true;

        // The pointer owns the icon now, so the icon's own hover preview has to stop: two things
        // animating the same element from different rules is how a drag starts to look elastic.
        _carried!.UsePointerMotion = false;
        HoverMotion.Exit(_carried.MotionTarget);
        PlaceIndicator();
        _indicator.Opacity = 1;
    }

    private void Finish()
    {
        using (DropProfile.Measure("release.finish"))
        {
            _awaitingProjection = false;
            _travelled = false;
            _pressed = false;
            _indicator.Opacity = 0;

            var carried = _carried;
            _carried = null;
            _item = null;
            _pointerId = 0;
            _centres = [];

            if (carried is null)
            {
                return;
            }

            TransformMotion.SetTranslateX(carried, 0);

            // The icon's own handlers describe hover from here on, and they will not run again until
            // the pointer moves, so the state it is in now has to be the right one.
            carried.ResumePointerMotion();
        }
    }

    /// <summary>
    /// Where the icon has to be drawn for the run to look as though it has already been reordered:
    /// the place the target index names, measured from where the icon rests today.
    /// </summary>
    private double LandingOffset() => _centres[0] + (_target * Pitch()) - _centres[_from];

    private void PlaceIndicator() => TransformMotion.SetTranslateX(
        _indicator,
        _centres[0] + (_target * Pitch()) - (_width / 2));

    /// <summary>
    /// The distance between two items. Measured from the run rather than assumed, so a dock whose
    /// labels make one icon wider than the rest still lands close; the drop itself is exact either
    /// way, because the icon ends up wherever the new layout puts it.
    /// </summary>
    private double Pitch()
    {
        if (_centres.Length > 1)
        {
            var span = _centres[^1] - _centres[0];
            if (span > 0)
            {
                return span / (_centres.Length - 1);
            }
        }

        return _width + SlotGap;
    }

    private double CentreOf(DockIcon icon)
    {
        var origin = icon.TransformToVisual(_zone).TransformPoint(new Windows.Foundation.Point(0, 0));
        return origin.X + (icon.ActualWidth / 2);
    }

    private double PointerX(PointerRoutedEventArgs args) => args.GetCurrentPoint(_zone).Position.X;

    private static int IndexOf(IReadOnlyList<DockIcon> icons, DockIcon icon)
    {
        for (var i = 0; i < icons.Count; i++)
        {
            if (ReferenceEquals(icons[i], icon))
            {
                return i;
            }
        }

        return -1;
    }
}
