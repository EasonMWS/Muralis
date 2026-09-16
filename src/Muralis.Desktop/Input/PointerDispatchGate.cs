namespace Muralis.Desktop.Input;

/// <summary>
/// Caps how often pointer reports turn into work, without ever losing the last one: a report that
/// arrives inside the interval is not processed, and the caller is told when the next one may be.
/// Because the position is read at processing time and not taken from the report, skipping the
/// reports in between cannot lose anything but the reports themselves.
/// </summary>
internal sealed class PointerDispatchGate(int minimumIntervalMilliseconds)
{
    private long _lastDispatch = long.MinValue / 2;

    /// <summary>Whether a report at <paramref name="nowMilliseconds"/> may be processed; records it when it may.</summary>
    internal bool TryDispatch(long nowMilliseconds)
    {
        if (nowMilliseconds - _lastDispatch < minimumIntervalMilliseconds)
        {
            return false;
        }

        _lastDispatch = nowMilliseconds;
        return true;
    }

    /// <summary>When a report that was just skipped may be processed; at least 1 ms.</summary>
    internal int DelayToNextDispatch(long nowMilliseconds) =>
        (int)Math.Max(1, minimumIntervalMilliseconds - (nowMilliseconds - _lastDispatch));

    /// <summary>Records a dispatch that did not go through <see cref="TryDispatch"/>.</summary>
    internal void MarkDispatched(long nowMilliseconds) => _lastDispatch = nowMilliseconds;
}
