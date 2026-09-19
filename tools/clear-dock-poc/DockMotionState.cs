using Muralis.Core.Motion;

namespace ClearDockPoc;

/// <summary>
/// The dock's live state: where each icon is drawn, and which one the pointer is over.
/// </summary>
/// <remarks>
/// <para>
/// The magnification is not reimplemented here. <see cref="DockMotionEngine"/> is the product's own engine and
/// this class only converts screen coordinates into the dock's space, hands the engine a pointer position, and
/// reads back the scale, sideways translation and lift it produces. If the numbers here ever disagree with the
/// shipping dock, the engine is the thing that is right.
/// </para>
/// <para>
/// A prototype input adapter, deliberately: it polls the cursor rather than subscribing to the process-wide raw
/// input broker. A real renderer would consume the broker, which is already renderer-independent, and this
/// poll exists so the proof can be finished without moving input ownership. It is single-monitor and assumes
/// one pixel per DIP.
/// </para>
/// </remarks>
internal sealed class DockMotionState
{
    private readonly DockMotionEngine _engine;
    private readonly DockMotionProfile _profile = DockMotionProfile.Default;
    private readonly DockMotionLayout _layout;
    private readonly double[] _centres;

    public DockMotionState(int iconCount, int cellWidth, int iconBox, int padding)
    {
        _engine = new DockMotionEngine(iconCount);

        var centres = new double[iconCount];
        var widths = new double[iconCount];
        for (var i = 0; i < iconCount; i++)
        {
            centres[i] = padding + (i * cellWidth) + (cellWidth / 2.0);
            widths[i] = iconBox;
        }

        _centres = centres;
        _layout = new DockMotionLayout(centres, widths);
    }

    /// <summary>The largest scale the engine produced on the last update.</summary>
    public double PeakScale => _engine.PeakScale;

    /// <summary>Whether the engine is moving anything.</summary>
    public bool IsActive => _engine.IsActive;

    /// <summary>Where each icon rests, in dock space.</summary>
    public IReadOnlyList<double> Centres => _centres;

    /// <summary>The engine's answer for one icon.</summary>
    public DockMotionSample Sample(int index) => _engine.Samples[index];

    /// <summary>
    /// Moves the wave to <paramref name="pointerAlongDip"/>, or puts every icon at rest when it is null.
    /// </summary>
    public void Update(double? pointerAlongDip) => _engine.Apply(_layout, pointerAlongDip, _profile);

    /// <summary>
    /// The icon under a point in dock space, or -1. Judged against the positions the icons are <em>currently</em>
    /// drawn at, so a magnified icon is hit where it looks rather than where it rests.
    /// </summary>
    public int HitTest(double x, double y, int iconBox, int surfaceHeight, int platePaddingY, int topOffset)
    {
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < _centres.Length; i++)
        {
            var sample = _engine.Samples[i];
            var scale = sample.Scale;
            var half = (iconBox * scale) / 2.0;
            var centreX = _centres[i] + sample.TranslateX;
            var bottom = surfaceHeight - platePaddingY + topOffset - sample.Lift;
            var top = bottom - (iconBox * scale);

            if (x >= centreX - half && x <= centreX + half && y >= top && y <= bottom)
            {
                // The largest icon wins when two overlap, which is what the eye sees.
                var distance = Math.Abs(x - centreX);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
        }

        return best;
    }
}
