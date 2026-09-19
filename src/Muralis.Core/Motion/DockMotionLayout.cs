namespace Muralis.Core.Motion;

/// <summary>
/// The dock as the motion engine sees it: one entry per icon, in dock order, each with the centre it
/// rests at and the width it occupies there.
/// </summary>
/// <remarks>
/// A snapshot rather than a live query. The pointer path is not allowed to walk a visual tree, measure
/// anything or convert between coordinate spaces, so the dock builds one of these whenever the layout
/// has actually changed and the pointer path only ever reads it.
/// </remarks>
public sealed class DockMotionLayout
{
    private readonly double[] _centres;
    private readonly double[] _widths;

    public DockMotionLayout(IReadOnlyList<double> centres, IReadOnlyList<double> widths)
    {
        ArgumentNullException.ThrowIfNull(centres);
        ArgumentNullException.ThrowIfNull(widths);

        if (centres.Count != widths.Count)
        {
            throw new ArgumentException("Every resting centre needs a width.", nameof(widths));
        }

        _centres = [.. centres];
        _widths = [.. widths];
    }

    /// <summary>An empty dock. Nothing to magnify, and every operation is a no-op.</summary>
    public static DockMotionLayout Empty { get; } = new([], []);

    public int Count => _centres.Length;

    public bool IsEmpty => _centres.Length == 0;

    /// <summary>The centre each icon rests at, along the dock, in DIP.</summary>
    public ReadOnlySpan<double> Centres => _centres;

    /// <summary>The width each icon occupies at rest, in DIP.</summary>
    public ReadOnlySpan<double> Widths => _widths;

    public double CentreAt(int index) => _centres[index];

    /// <summary>
    /// The centre from which the engine measures. Centres that are not strictly increasing mean the dock
    /// is mid-layout, and a run that is still settling is not something to animate: the engine answers
    /// with <see cref="IsSane"/> false and the dock leaves the icons alone for a frame.
    /// </summary>
    public bool IsSane
    {
        get
        {
            for (var i = 1; i < _centres.Length; i++)
            {
                if (_centres[i] <= _centres[i - 1])
                {
                    return false;
                }
            }

            for (var i = 0; i < _widths.Length; i++)
            {
                if (_widths[i] <= 0 || double.IsNaN(_widths[i]) || double.IsInfinity(_widths[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
