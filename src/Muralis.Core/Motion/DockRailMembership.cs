namespace Muralis.Core.Motion;

/// <summary>
/// Decides which icons share the dock's rail, using identity and a baseline the motion cannot have written.
/// </summary>
/// <remarks>
/// <para>
/// The engine writes Scale/Translation onto the same elements the dock measures to discover them, so any
/// membership test that reads a live transformed position is reading the motion's own output back as its input.
/// That is not a theoretical hazard. Measured on the running dock with the profiler on, the dock holds one row of
/// forty-five icons, and the wave disturbs it in two ways at once: it lifts the icons it is working on by a
/// graded amount, and the expansion that goes with the pointer arriving shifts the entire row. A four-unit band
/// around the current pose is narrower than either, so it rejected icons that had never left the rail. They then
/// stopped being driven, and because the dock releases only the icons it is currently driving, they were never
/// released either: participants fell 45, 42, 39, 37, 33 and stayed there for the rest of the session while the
/// dock still held all forty-five icons.
/// </para>
/// <para>
/// So the rail is a set held across passes, not a test re-applied to the current pose. It is established from a
/// pass where every sample is at rest, and then held: an icon the wave has lifted is still on the rail, and the
/// expansion moving the row does not put anybody off it. The set is started again only when the set of keys
/// changes.
/// </para>
/// <para>
/// The caller keys each icon by its position in the dock's own icon list, so a key is a slot rather than a
/// durable identity: an icon swapped for another at the same position, with the same count, does not start the
/// set again and inherits the slot's membership. That is a deliberate simplification, and its failure mode is
/// over-admission — an extra icon gets magnified — never the silent, permanent loss this type exists to prevent.
/// </para>
/// <para>
/// Deliberately free of any UI type: it holds opaque integer keys and numbers, so the policy can be tested
/// exhaustively without a window, a dispatcher or a composition pass.
/// </para>
/// </remarks>
public sealed class DockRailMembership
{
    private readonly HashSet<int> _members = [];
    private readonly List<int> _order = [];
    private readonly HashSet<int> _all = [];
    private int _iconCount = -1;
    private bool _hasReference;

    /// <summary>The tolerance, in the dock's own units, that decides whether a top sits on the reference row.</summary>
    public const double ToleranceDip = 4;

    /// <summary>How many icons are currently on the rail.</summary>
    public int Count => _order.Count;

    /// <summary>The reference top membership is decided against, or NaN while no honest one exists.</summary>
    public double ReferenceTop { get; private set; } = double.NaN;

    /// <summary>Whether a trustworthy reference top has been taken yet.</summary>
    public bool HasReference => _hasReference;

    /// <summary>
    /// How many of the icons offered on the most recent pass were admitted by that pass.
    /// </summary>
    /// <remarks>
    /// <see cref="Observe"/> returns the accumulated members, which is what the caller draws with, so it can only
    /// ever grow. This is the per-pass figure, and it is the one that says whether the rule is still finding the
    /// whole rail: an icon the wave has lifted is admitted here just as an icon at rest is.
    /// </remarks>
    public int AdmittedInLastPass { get; private set; }

    /// <summary>
    /// Offers one measurement pass and returns the current members, in the order they were offered.
    /// </summary>
    /// <param name="topsByIdentity">Each sized icon's measured top, keyed by its position in the dock's icon list.</param>
    /// <param name="allAtRest">
    /// Whether the caller believes nothing is lifted. The reference is taken only when this is true, because only
    /// then are the measured tops free of the motion's own output. The caller derives it from the engine's samples
    /// being at rest, which is a good proxy rather than a proof: a freshly built engine's samples are all at rest
    /// by construction, so a caller that replaced its engine without releasing the elements could report rest over
    /// posed icons. That would move the reference with the majority row rather than break membership, since the
    /// set is held rather than re-derived.
    /// </param>
    public IReadOnlyList<int> Observe(IReadOnlyDictionary<int, double> topsByIdentity, bool allAtRest)
    {
        ArgumentNullException.ThrowIfNull(topsByIdentity);

        if (topsByIdentity.Count == 0)
        {
            return [];
        }

        // A changed key set is a changed dock, so a previous decision describes nothing. The count check is only a
        // cheap guard ahead of the set comparison, which is what actually decides this.
        if (_iconCount != topsByIdentity.Count || !_all.SetEquals(topsByIdentity.Keys))
        {
            _members.Clear();
            _order.Clear();
            _all.Clear();
            foreach (var identity in topsByIdentity.Keys)
            {
                _all.Add(identity);
            }

            _iconCount = topsByIdentity.Count;
            _hasReference = false;
            ReferenceTop = double.NaN;
        }

        // An all-at-rest pass is the only one whose measured tops the motion cannot have written, so it is the
        // only one a reference may be taken from. Taking it again on a later such pass can only widen membership,
        // because an admitted icon is never removed for measuring far from a new reference.
        if (allAtRest)
        {
            TakeReference(topsByIdentity);
        }

        AdmittedInLastPass = 0;
        foreach (var pair in topsByIdentity)
        {
            // Without a reference there is no honest basis for dropping anything, and a dock that magnifies one
            // icon too many is still a working dock, so the whole sized set is kept until one exists.
            if (_hasReference && Math.Abs(pair.Value - ReferenceTop) > ToleranceDip)
            {
                continue;
            }

            AdmittedInLastPass++;
            if (_members.Add(pair.Key))
            {
                _order.Add(pair.Key);
            }
        }

        return _order;
    }

    /// <summary>Whether the given identity is currently a member of the rail.</summary>
    public bool Contains(int identity) => _members.Contains(identity);

    private void TakeReference(IReadOnlyDictionary<int, double> topsByIdentity)
    {
        var ordered = new double[topsByIdentity.Count];
        var n = 0;
        foreach (var top in topsByIdentity.Values)
        {
            ordered[n++] = top;
        }

        Array.Sort(ordered);
        ReferenceTop = ordered[ordered.Length / 2];
        _hasReference = true;
    }
}
