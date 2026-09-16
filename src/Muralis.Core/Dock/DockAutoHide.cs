namespace Muralis.Core.Dock;

/// <summary>
/// What the dock is doing. <see cref="Revealing"/> and <see cref="Hiding"/> are the rail moving;
/// <see cref="Visible"/> and <see cref="Hidden"/> are it at rest; <see cref="Dragging"/> is the one
/// state in which nothing may take the dock away, because a hand is holding something over it.
/// </summary>
public enum DockState
{
    Hidden,
    Revealing,
    Visible,
    Hiding,
    Dragging,
}

/// <summary>
/// The dock's auto-hide decision, with no clock of its own: the caller advances it with the current
/// time whenever anything relevant happened — the pointer moved, a drag started or ended, the rail
/// finished moving — and arms a one-shot timer for whatever this asks for next. It never polls.
/// </summary>
/// <remarks>
/// The two delays are what keep the dock from reacting to a pointer that is only passing by: the
/// rail comes out after <see cref="DockOptions.ShowDelayMilliseconds"/> of wanting it and goes back
/// after <see cref="DockOptions.HideDelayMilliseconds"/> of not wanting it, so crossing an edge on
/// the way somewhere else does neither. A rail that is already out stays out while the pointer is on
/// it, which is what makes clicking an item — or simply resting on one after a launch — leave the
/// dock where it is.
/// </remarks>
public sealed class DockAutoHide
{
    private readonly DockOptions _options;
    private double? _deadlineMilliseconds;

    public DockAutoHide(DockOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public DockState State { get; private set; } = DockState.Hidden;

    /// <summary>Whether the rail should be out. This is what the reveal animation springs towards.</summary>
    public bool IsOut => State is DockState.Revealing or DockState.Visible or DockState.Dragging;

    /// <summary>Where the rail is heading: 1 fully out, 0 away behind its edge.</summary>
    public double RevealTarget => State is DockState.Hidden or DockState.Hiding ? 0.0 : 1.0;

    /// <summary>Whether the rail has nothing left to do: the two resting states.</summary>
    public bool IsAtRest => State is DockState.Hidden or DockState.Visible;

    /// <summary>
    /// How long after the last advance the state would change on its own, or null when it would not.
    /// The caller arms a one-shot timer for it instead of polling for it.
    /// </summary>
    public double? PendingChangeDelayMilliseconds(double nowMilliseconds) =>
        _deadlineMilliseconds is { } deadline ? Math.Max(0.0, deadline - nowMilliseconds) : null;

    /// <summary>
    /// Advances the state.
    /// <paramref name="wantsReveal"/> is true while the pointer is in the trigger strip or over the
    /// rail itself, <paramref name="dragging"/> while the pointer is holding something, and
    /// <paramref name="revealSettled"/> when the rail has finished moving towards wherever this last
    /// asked it to go. Returns true when the state changed.
    /// </summary>
    public bool Advance(double nowMilliseconds, bool wantsReveal, bool dragging, bool revealSettled)
    {
        if (!_options.AutoHide)
        {
            // A dock that never hides is simply out; nothing here has anything to decide.
            return MoveTo(DockState.Visible);
        }

        if (dragging)
        {
            return MoveTo(DockState.Dragging);
        }

        if (State == DockState.Dragging)
        {
            // The hand let go. Whatever it was carrying has been dropped on the dock or off it, and
            // nothing holds the rail any more: the ordinary delays take over from this moment, so a
            // release away from the dock starts its hide delay here rather than at the next event.
            var settled = MoveTo(DockState.Visible);
            return AdvanceWhileVisible(nowMilliseconds, wantsReveal) || settled;
        }

        return State switch
        {
            DockState.Hidden => AdvanceWhileHidden(nowMilliseconds, wantsReveal),
            DockState.Revealing => AdvanceWhileRevealing(nowMilliseconds, wantsReveal, revealSettled),
            DockState.Hiding => AdvanceWhileHiding(wantsReveal, revealSettled),
            _ => AdvanceWhileVisible(nowMilliseconds, wantsReveal),
        };
    }

    private bool AdvanceWhileHidden(double now, bool wantsReveal)
    {
        if (!wantsReveal)
        {
            _deadlineMilliseconds = null;
            return false;
        }

        _deadlineMilliseconds ??= now + _options.ShowDelayMilliseconds;
        if (now < _deadlineMilliseconds)
        {
            return false;
        }

        return MoveTo(DockState.Revealing);
    }

    private bool AdvanceWhileRevealing(double now, bool wantsReveal, bool revealSettled)
    {
        if (wantsReveal)
        {
            // Re-entering the dock cancels a pending retract; the rail never blinks away from under
            // a pointer that came back.
            _deadlineMilliseconds = null;
            return revealSettled && MoveTo(DockState.Visible);
        }

        // The pointer left while the rail was still coming out. The hide delay is counted from the
        // leave, not from the end of the reveal, so the rail turns around where it is instead of
        // finishing a journey nobody asked for and then waiting to be taken back.
        _deadlineMilliseconds ??= now + _options.HideDelayMilliseconds;
        return now >= _deadlineMilliseconds && MoveTo(DockState.Hiding);
    }

    private bool AdvanceWhileVisible(double now, bool wantsReveal)
    {
        if (wantsReveal)
        {
            _deadlineMilliseconds = null;
            return false;
        }

        _deadlineMilliseconds ??= now + _options.HideDelayMilliseconds;
        if (now < _deadlineMilliseconds)
        {
            return false;
        }

        return MoveTo(DockState.Hiding);
    }

    private bool AdvanceWhileHiding(bool wantsReveal, bool revealSettled)
    {
        if (wantsReveal)
        {
            return MoveTo(DockState.Revealing);
        }

        return revealSettled && MoveTo(DockState.Hidden);
    }

    private bool MoveTo(DockState state)
    {
        if (State == state)
        {
            return false;
        }

        State = state;
        _deadlineMilliseconds = null;
        return true;
    }
}
