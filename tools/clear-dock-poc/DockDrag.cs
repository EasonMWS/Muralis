using Muralis.Core.Dock;
using Muralis.Core.Motion;

namespace ClearDockPoc;

/// <summary>
/// The order the icons are shown in, and the one operation that changes it: a committed drag.
/// </summary>
/// <remarks>
/// <para>
/// In memory only, deliberately. The product persists pin order through its own settings service behind an
/// interface, and a renderer prototype that wrote the user's real configuration to prove it could reorder a row
/// of icons would be trading the user's data for a demonstration. What has to be proved here is that the renderer
/// can drive the reorder and land on an order, and that is provable without touching the file.
/// </para>
/// <para>
/// The order is stored as the list of which target sits in each display slot, so a commit is a single move and
/// every read is by display slot — which is exactly how the renderer draws.
/// </para>
/// </remarks>
internal sealed class DockOrder
{
    private readonly List<int> _slots;

    public DockOrder(int count)
    {
        _slots = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            _slots.Add(i);
        }
    }

    public int Count => _slots.Count;

    /// <summary>Which target is drawn in <paramref name="slot"/>.</summary>
    public int this[int slot] => _slots[slot];

    /// <summary>How many times a drag has actually changed the order.</summary>
    public int Commits { get; private set; }

    /// <summary>The order as it would be read by a person: the target behind each slot, left to right.</summary>
    public string Describe() => string.Join(',', _slots);

    /// <summary>
    /// Moves the target currently in <paramref name="fromSlot"/> to <paramref name="toSlot"/>, shifting the rest.
    /// </summary>
    /// <remarks>
    /// Nothing happens when the slot does not change, and that is the point: a drag that ends where it started is
    /// not a reorder, and counting it as one would make "exactly one commit per drag" meaningless.
    /// </remarks>
    public bool Move(int fromSlot, int toSlot)
    {
        if (fromSlot < 0 || fromSlot >= _slots.Count || toSlot < 0 || toSlot >= _slots.Count || fromSlot == toSlot)
        {
            return false;
        }

        var target = _slots[fromSlot];
        _slots.RemoveAt(fromSlot);
        _slots.Insert(toSlot, target);
        Commits++;
        return true;
    }
}

/// <summary>
/// What the pointer is doing to the dock: nothing, pressing, dragging, or having just dropped.
/// </summary>
internal enum DragPhase
{
    /// <summary>No button is down over the dock.</summary>
    Idle,

    /// <summary>The button went down on an icon but the pointer has not yet travelled far enough to be a drag.</summary>
    Pressed,

    /// <summary>The pointer has passed the travel threshold and is carrying an icon.</summary>
    Dragging,
}

/// <summary>
/// The dock's drag: a press, a threshold, a carried icon that follows the pointer one to one, a candidate slot,
/// and one commit on release.
/// </summary>
/// <remarks>
/// <para>
/// The transform ownership is stated rather than inherited from the XAML dock, because the two renderers compose
/// their state differently. The final position of an icon is
/// </para>
/// <list type="bullet">
/// <item><b>base</b> — where the slot rests, which the order decides;</item>
/// <item><b>plus the Nexus offset</b> — the engine's translation for the magnification wave;</item>
/// <item><b>plus the drag offset</b> — the carried icon's own displacement, which the engine never sees.</item>
/// </list>
/// <para>
/// The drag offset is deliberately kept out of <see cref="DockMotionEngine"/>. The engine answers "how large is
/// each icon and how far has the wave pushed it", which is a statement about the run; a drag is a statement about
/// one icon being held, and feeding it into the engine's maths would corrupt the wave for every other icon.
/// </para>
/// <para>
/// While a drag owns the pointer the wave is not drawn at all, which is the product's own rule: the dragged icon
/// would otherwise be scaled by the engine and by nothing else, and its neighbours would open a gap for a wave
/// that is not the reason they are moving. One owner at a time, as with the transform channels.
/// </para>
/// </remarks>
internal sealed class DockDrag
{
    /// <summary>
    /// How far the pointer must travel, in DIP, before a press becomes a drag.
    /// </summary>
    /// <remarks>
    /// The product's own threshold. A click without a drag must still launch, so the two gestures are separated
    /// by distance and not by time: a slow, small, deliberate press is a click, and a quick one that moves is a
    /// drag, which is what a hand expects.
    /// </remarks>
    private const double TravelThresholdDip = 4;

    private readonly DockOrder _order;
    private readonly DockMotionProfile _profile;
    private readonly double[] _previewCentres;

    public DockDrag(DockOrder order, DockMotionProfile profile, double[] previewCentres)
    {
        _order = order;
        _profile = profile;
        _previewCentres = previewCentres;
    }

    public DragPhase Phase { get; private set; } = DragPhase.Idle;

    /// <summary>The slot the carried icon came from, or -1.</summary>
    public int DraggedSlot { get; private set; } = -1;

    /// <summary>The slot the carried icon would take if it were released now, or -1.</summary>
    public int CandidateSlot { get; private set; } = -1;

    /// <summary>The carried icon's displacement from its resting centre, in DIP.</summary>
    public double OffsetDip { get; private set; }

    /// <summary>Whether the carried icon actually moved slots, which is what makes a release a reorder.</summary>
    public bool CandidateChanged { get; private set; }

    /// <summary>Whether a drag is in progress.</summary>
    public bool IsDragging => Phase == DragPhase.Dragging;

    /// <summary>
    /// Begins a press on <paramref name="slot"/> at <paramref name="pointerDip"/>.
    /// </summary>
    public void Press(int slot, double pointerDip)
    {
        if (slot < 0 || slot >= _order.Count)
        {
            Reset();
            return;
        }

        Phase = DragPhase.Pressed;
        DraggedSlot = slot;
        CandidateSlot = slot;
        CandidateChanged = false;
        PressDip = pointerDip;
        OffsetDip = 0;
    }

    /// <summary>Where the press landed, in DIP. The drag's displacement is measured from here, not from the icon.</summary>
    public double PressDip { get; private set; }

    /// <summary>
    /// Moves the pointer. Returns true when the phase changed, which is the only thing worth reporting.
    /// </summary>
    /// <remarks>
    /// The icon follows the pointer one to one: its displacement is the pointer's displacement from where the
    /// press landed, with no easing, no spring and no prediction. A dock that eases towards the hand is a dock
    /// that is behind the hand, and a dragged icon is the most visible case of it.
    /// </remarks>
    public bool Move(double pointerDip)
    {
        if (Phase == DragPhase.Idle)
        {
            return false;
        }

        OffsetDip = pointerDip - PressDip;

        var before = Phase;
        if (Phase == DragPhase.Pressed && Math.Abs(OffsetDip) >= TravelThresholdDip)
        {
            Phase = DragPhase.Dragging;
        }

        if (Phase == DragPhase.Dragging)
        {
            // The candidate comes from the product's own reorder maths, against the centres the other icons are
            // still resting at, so it is monotone in the pointer position and cannot flicker between two answers.
            var centres = new double[_order.Count];
            for (var slot = 0; slot < _order.Count; slot++)
            {
                centres[slot] = _previewCentres[slot];
            }

            var draggedCentre = _previewCentres[DraggedSlot] + OffsetDip;
            var candidate = DockReorder.TargetIndex(draggedCentre, centres, DraggedSlot);
            if (candidate != CandidateSlot)
            {
                CandidateSlot = candidate;
                CandidateChanged = true;
            }
        }

        return before != Phase;
    }

    /// <summary>
    /// Releases the pointer. Returns the committed move, or null when this was a click or a drag that landed
    /// where it started.
    /// </summary>
    /// <remarks>
    /// The commit happens here and only here. A drag that is reported as committed twice would reorder twice, and
    /// the product's own persistence layer would see two writes for one gesture, so the rule is that the order
    /// changes once per release and never while the pointer is moving.
    /// </remarks>
    public (int From, int To)? Release()
    {
        var wasDragging = Phase == DragPhase.Dragging;
        var from = DraggedSlot;
        var to = CandidateSlot;

        Reset();

        if (!wasDragging || from < 0 || to < 0 || from == to)
        {
            return null;
        }

        return _order.Move(from, to) ? (from, to) : null;
    }

    /// <summary>Abandons the gesture without committing, for a lost pointer or a teardown.</summary>
    public void Reset()
    {
        Phase = DragPhase.Idle;
        DraggedSlot = -1;
        CandidateSlot = -1;
        OffsetDip = 0;
        CandidateChanged = false;
    }

    /// <summary>
    /// The centre each icon should be drawn at, in DIP, for the current gesture.
    /// </summary>
    /// <remarks>
    /// While dragging, the gap opens at the candidate slot and every other icon slides along by one place — the
    /// product's own preview — and the carried icon is kept under the pointer regardless of where that is. The
    /// hand therefore never loses the icon it is carrying, and the drop position is visible before the release.
    /// </remarks>
    public void FillCentres(Span<double> centresDip)
    {
        if (!IsDragging)
        {
            for (var slot = 0; slot < _order.Count; slot++)
            {
                centresDip[slot] = _previewCentres[slot];
            }

            return;
        }

        var preview = DockReorder.PreviewCentres(_previewCentres, DraggedSlot, CandidateSlot);
        for (var slot = 0; slot < _order.Count; slot++)
        {
            centresDip[slot] = preview[slot];
        }

        centresDip[DraggedSlot] = _previewCentres[DraggedSlot] + OffsetDip;
    }
}
