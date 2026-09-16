namespace Muralis.Core.Canvas;

/// <summary>Whether the dock rail is out or away. The animation between the two is the renderer's business.</summary>
public enum CanvasDockPhase
{
    Collapsed,
    Shown,
}

/// <summary>
/// The dock's auto-hide decision, with no clock of its own: the caller advances it with the
/// current time whenever anything relevant happened (pointer moved, drag started, animation
/// settled). It never polls, and it owns the two delays from <see cref="CanvasDockOptions"/>:
/// the rail only appears after <see cref="CanvasDockOptions.ShowDelayMilliseconds"/> of wanting
/// it, and only retracts after <see cref="CanvasDockOptions.HideDelayMilliseconds"/> of not
/// wanting it, so passing by an edge does neither.
/// </summary>
public sealed class CanvasDockAutoHide
{
    private readonly CanvasDockOptions _options;
    private double? _deadlineMilliseconds;

    public CanvasDockAutoHide(CanvasDockOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public CanvasDockPhase Phase { get; private set; } = CanvasDockPhase.Collapsed;

    /// <summary>
    /// How long after the last advance the phase would change on its own, or null when it would
    /// not. The caller arms a one-shot timer for it instead of polling.
    /// </summary>
    public double? PendingChangeDelayMilliseconds(double nowMilliseconds) =>
        _deadlineMilliseconds is { } deadline ? Math.Max(0.0, deadline - nowMilliseconds) : null;

    /// <summary>
    /// Advances the state. <paramref name="wantsExpanded"/> is true while the pointer is in the
    /// trigger band or over the rail's own area; <paramref name="interactionLocked"/> is true
    /// while the pointer is captured (a drag) so the rail cannot retract under it.
    /// Returns true when the phase changed.
    /// </summary>
    public bool Advance(double nowMilliseconds, bool wantsExpanded, bool interactionLocked)
    {
        var wants = wantsExpanded || interactionLocked;

        if (Phase == CanvasDockPhase.Collapsed)
        {
            if (!wants)
            {
                _deadlineMilliseconds = null;
                return false;
            }

            _deadlineMilliseconds ??= nowMilliseconds + _options.ShowDelayMilliseconds;
            if (nowMilliseconds < _deadlineMilliseconds)
            {
                return false;
            }

            _deadlineMilliseconds = null;
            Phase = CanvasDockPhase.Shown;
            return true;
        }

        if (wants)
        {
            // Re-entering cancels a pending retract; the rail never blinks away under a moving pointer.
            _deadlineMilliseconds = null;
            return false;
        }

        _deadlineMilliseconds ??= nowMilliseconds + _options.HideDelayMilliseconds;
        if (nowMilliseconds < _deadlineMilliseconds)
        {
            return false;
        }

        _deadlineMilliseconds = null;
        Phase = CanvasDockPhase.Collapsed;
        return true;
    }
}
