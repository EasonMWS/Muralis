using Muralis.Core.Canvas;

namespace Muralis.Core.Dock;

/// <summary>
/// The edge dock: whether it is there at all, which edge it hugs, how it behaves when the pointer is
/// away, the numbers its geometry and magnification are built from, and what it holds.
/// </summary>
/// <remarks>
/// <para>
/// The dock is its own thing, not a corner of the canvas: membership and order live here, in
/// <see cref="Entries"/>, and the canvas is simply every item that is not in this list. The geometry
/// numbers are all in DIP and all relative to the display's own edge, so nothing here assumes a
/// monitor at (0, 0) or a scale of 1.
/// </para>
/// <para>
/// One dock is what this version shows, but nothing in the model says so: the document carries a
/// dock, and a second one would be another value of this type.
/// </para>
/// </remarks>
public sealed class DockOptions
{
    /// <summary>Whether the user has the dock switched on. A dock with no entries draws nothing either way.</summary>
    public bool Enabled { get; set; }

    public DockEdge Edge { get; set; } = DockEdge.Left;

    /// <summary>
    /// Whether the rail retracts when the pointer is away. Switched off, the rail simply stays out;
    /// there is still no work to do while nothing moves.
    /// </summary>
    public bool AutoHide { get; set; } = true;

    /// <summary>How deep, in DIP, the strip along the edge is that the pointer can summon the rail from.</summary>
    public double TriggerThicknessDip { get; set; } = 4;

    /// <summary>
    /// How much of the rail stays visible, in DIP, while it is away: a hint of where the dock is,
    /// not a handle. Zero hides it completely and leaves only the trigger strip.
    /// </summary>
    public double PeekSizeDip { get; set; } = 4;

    /// <summary>How long the pointer must want the dock before it comes out.</summary>
    public int ShowDelayMilliseconds { get; set; } = 120;

    /// <summary>How long the pointer must stay away before it goes back.</summary>
    public int HideDelayMilliseconds { get; set; } = 600;

    /// <summary>Edge length of one dock item in DIP.</summary>
    public double ItemSizeDip { get; set; } = 56;

    /// <summary>Gap in DIP between neighbouring items at their natural size.</summary>
    public double SpacingDip { get; set; } = 12;

    /// <summary>Gap in DIP between the rail and the display edge while it is out.</summary>
    public double EdgeMarginDip { get; set; } = 10;

    /// <summary>Padding in DIP between the rail's border and its items.</summary>
    public double PaddingDip { get; set; } = 8;

    /// <summary>How large an item grows directly under the pointer.</summary>
    public double MaxScale { get; set; } = 1.6;

    /// <summary>How far along the rail, in DIP, the pointer reaches before an item stops growing.</summary>
    public double InfluenceRadiusDip { get; set; } = 130;

    /// <summary>The shape of that growth falloff.</summary>
    public ProximityFalloff Falloff { get; set; } = ProximityFalloff.Smoothstep;

    /// <summary>The spring the rail and the items move with.</summary>
    public CanvasSpring Spring { get; set; } = new() { PeriodSeconds = 0.34, DampingRatio = 0.78 };

    /// <summary>What the dock holds, in the order it holds it. Every entry names an item of this layout.</summary>
    public List<DockEntry> Entries { get; set; } = [];

    /// <summary>Whether the item is in the dock.</summary>
    public bool IsDocked(string? itemId) => IndexOf(itemId) >= 0;

    /// <summary>Where the item sits in the dock, or -1 when it is not in it.</summary>
    public int IndexOf(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return -1;
        }

        for (var i = 0; i < Entries.Count; i++)
        {
            if (string.Equals(Entries[i].ItemId, itemId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The docked ids, for a caller that has to ask the question many times.</summary>
    public IReadOnlySet<string> DockedItemIds() =>
        Entries.Select(entry => entry.ItemId).ToHashSet(StringComparer.Ordinal);

    /// <summary>The rail's thickness in DIP: one item's row plus the padding on either side of it.</summary>
    public double RailThicknessDip => ItemSizeDip + (2 * PaddingDip);

    /// <summary>Checks the facts the geometry and the renderer rely on.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!Enum.IsDefined(Edge))
        {
            problems.Add("The dock edge is not one of the four display edges.");
        }

        if (TriggerThicknessDip is < 0 or > 200)
        {
            problems.Add("The dock trigger thickness must be 0-200 DIP.");
        }

        if (PeekSizeDip is < 0 or > 200)
        {
            problems.Add("The dock peek size must be 0-200 DIP.");
        }

        if (ShowDelayMilliseconds < 0 || HideDelayMilliseconds < 0)
        {
            problems.Add("Dock delays cannot be negative.");
        }

        if (ItemSizeDip is < 8 or > 512)
        {
            problems.Add("Dock item size must be 8-512 DIP.");
        }

        if (SpacingDip is < 0 or > 512)
        {
            problems.Add("Dock item spacing must be 0-512 DIP.");
        }

        if (EdgeMarginDip is < 0 or > 512 || PaddingDip is < 0 or > 512)
        {
            problems.Add("Dock margin and padding must be 0-512 DIP.");
        }

        if (MaxScale is < 1 or > 4)
        {
            problems.Add("The dock magnification must be between 1 and 4.");
        }

        if (InfluenceRadiusDip is < 1 or > 2000)
        {
            problems.Add("The dock influence radius must be 1-2000 DIP.");
        }

        if (!Enum.IsDefined(Falloff))
        {
            problems.Add("The dock falloff is not a known curve.");
        }

        if (Spring is null)
        {
            problems.Add("The dock spring is missing.");
        }
        else
        {
            if (Spring.PeriodSeconds is <= 0 or > 5)
            {
                problems.Add("The dock spring period must be 0-5 seconds.");
            }

            if (Spring.DampingRatio is <= 0 or > 2)
            {
                problems.Add("The dock spring damping ratio must be 0-2.");
            }
        }

        if (Entries is null)
        {
            problems.Add("The dock needs an entry list.");
            return problems;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (entry is null)
            {
                problems.Add("The dock contains a null entry.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.ItemId))
            {
                problems.Add("Every dock entry needs an item id.");
            }
            else if (!seen.Add(entry.ItemId))
            {
                problems.Add($"The item '{entry.ItemId}' is in the dock twice.");
            }
        }

        return problems;
    }

    public DockOptions Clone() => new()
    {
        Enabled = Enabled,
        Edge = Edge,
        AutoHide = AutoHide,
        TriggerThicknessDip = TriggerThicknessDip,
        PeekSizeDip = PeekSizeDip,
        ShowDelayMilliseconds = ShowDelayMilliseconds,
        HideDelayMilliseconds = HideDelayMilliseconds,
        ItemSizeDip = ItemSizeDip,
        SpacingDip = SpacingDip,
        EdgeMarginDip = EdgeMarginDip,
        PaddingDip = PaddingDip,
        MaxScale = MaxScale,
        InfluenceRadiusDip = InfluenceRadiusDip,
        Falloff = Falloff,
        Spring = Spring.Clone(),
        Entries = Entries.Select(entry => entry.Clone()).ToList(),
    };

    /// <summary>
    /// Takes on another dock's settings, entries included, without becoming another object. The dock's
    /// state machine holds the options it was built with — the same ones the layout holds — so the way
    /// to change them under it is to change this object, not to replace it.
    /// </summary>
    public void CopyFrom(DockOptions other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Enabled = other.Enabled;
        Edge = other.Edge;
        AutoHide = other.AutoHide;
        TriggerThicknessDip = other.TriggerThicknessDip;
        PeekSizeDip = other.PeekSizeDip;
        ShowDelayMilliseconds = other.ShowDelayMilliseconds;
        HideDelayMilliseconds = other.HideDelayMilliseconds;
        ItemSizeDip = other.ItemSizeDip;
        SpacingDip = other.SpacingDip;
        EdgeMarginDip = other.EdgeMarginDip;
        PaddingDip = other.PaddingDip;
        MaxScale = other.MaxScale;
        InfluenceRadiusDip = other.InfluenceRadiusDip;
        Falloff = other.Falloff;
        Spring = other.Spring.Clone();
        Entries = other.Entries.Select(entry => entry.Clone()).ToList();
    }
}
