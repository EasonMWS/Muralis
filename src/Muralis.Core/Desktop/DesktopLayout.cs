using Muralis.Core.Canvas;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Dock;

namespace Muralis.Core.Desktop;

/// <summary>
/// The desktop layout document: the items the user put on the desktop, the dock they arranged, the
/// takeover they asked for, and the parameters that shape hover magnification and motion. It is
/// persisted on its own, away from application settings, so a hand-edit or a reset never touches
/// <c>settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shape is two lists and their parameters: <see cref="Items"/> holds every item the user
/// imported, exactly once, and <see cref="Dock"/> holds the ids of the ones that live on the dock,
/// in dock order. An item therefore appears on the canvas or in the dock depending on nothing but
/// where it is named, and there is no second copy of an item to keep in step.
/// </para>
/// <para>
/// Version 3 moved dock membership into the dock's own document section; version 2 kept it as a
/// <c>placement</c> field on each item, which made the dock a tag on the canvas rather than a thing
/// of its own. Both older shapes are read once and brought forward.
/// </para>
/// <para>
/// Version 4 adds <see cref="Takeover"/> and the source path an item was adopted from. Both are
/// choices about the user's own desktop rather than about the canvas, and both are absent from older
/// documents, where the defaults — the native desktop, and no source to name — are exactly right.
/// </para>
/// </remarks>
public sealed class DesktopLayout
{
    /// <summary>Version 4 added the takeover section and the desktop source an item came from.</summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>Document marker, so a file that is not a Muralis desktop layout is refused early.</summary>
    public const string DocumentKind = "muralis.desktopLayout";

    /// <summary>Bumped whenever the on-disk shape changes in a breaking way.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Kind { get; set; } = DocumentKind;

    /// <summary>How the free canvas magnifies items under the pointer.</summary>
    public CanvasProximityOptions Proximity { get; set; } = new();

    /// <summary>The springs the free canvas moves with.</summary>
    public CanvasMotionOptions Motion { get; set; } = new();

    public DockOptions Dock { get; set; } = new();

    /// <summary>Which desktop mode the user chose, and what adopting their own desktop means.</summary>
    public DesktopTakeoverOptions Takeover { get; set; } = new();

    /// <summary>Every item, whether it is on the canvas or in the dock. The dock names the ones it shows.</summary>
    public List<DesktopItem> Items { get; set; } = [];

    /// <summary>
    /// The layout a fresh install starts from: the parameters and no items at all. Items only ever
    /// appear because the user added one.
    /// </summary>
    public static DesktopLayout CreateEmpty() => new();

    /// <summary>Whether the item lives in the dock.</summary>
    public bool IsDocked(string? itemId) => Dock.IsDocked(itemId);

    /// <summary>The items on the canvas, in the order the dock does not decide: the free ones.</summary>
    public IReadOnlyList<DesktopItem> FreeItems() =>
        Items.Where(item => !Dock.IsDocked(item.Id)).ToList();

    /// <summary>
    /// Checks the facts consumers rely on. Returns an empty list when the layout is coherent; the
    /// store falls back to an empty layout otherwise.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (SchemaVersion <= 0)
        {
            problems.Add("The layout schema version must be positive.");
        }

        if (SchemaVersion > CurrentSchemaVersion)
        {
            problems.Add($"The layout was written by a newer version ({SchemaVersion}).");
        }

        if (!string.Equals(Kind, DocumentKind, StringComparison.Ordinal))
        {
            problems.Add($"The layout kind must be '{DocumentKind}'.");
        }

        ValidateProximity(Proximity, "proximity", problems);

        if (Motion.Hover is null)
        {
            problems.Add("The hover spring must be present.");
        }
        else
        {
            ValidateSpring(Motion.Hover, "hover spring", problems);
        }

        if (Dock is null)
        {
            problems.Add("The dock options are missing.");
        }
        else
        {
            problems.AddRange(Dock.Validate());
        }

        if (Takeover is null)
        {
            problems.Add("The takeover options are missing.");
        }
        else
        {
            problems.AddRange(Takeover.Validate());
        }

        if (Items is null)
        {
            problems.Add("The layout needs an item list.");
            return problems;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (item is null)
            {
                problems.Add("The layout contains a null item.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Id) && !ids.Add(item.Id))
            {
                problems.Add($"The item id '{item.Id}' is used twice.");
            }

            problems.AddRange(item.Validate());
        }

        if (Dock is not null)
        {
            foreach (var entry in Dock.Entries.Where(entry => entry is not null))
            {
                if (!string.IsNullOrWhiteSpace(entry.ItemId) && !ids.Contains(entry.ItemId))
                {
                    problems.Add($"The dock shows '{entry.ItemId}', which is not an item of this layout.");
                }
            }
        }

        return problems;
    }

    /// <summary>Deep copy, safe to hand to another thread while the canvas keeps editing this one.</summary>
    public DesktopLayout Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Kind = Kind,
        Proximity = Proximity.Clone(),
        Motion = Motion.Clone(),
        Dock = Dock.Clone(),
        Takeover = Takeover.Clone(),
        Items = Items.Select(item => item.Clone()).ToList(),
    };

    private static void ValidateProximity(CanvasProximityOptions options, string what, List<string> problems)
    {
        if (options is null)
        {
            problems.Add($"The {what} options are missing.");
            return;
        }

        if (options.MaxScale is < 1 or > 4)
        {
            problems.Add($"The {what} max scale must be between 1 and 4.");
        }

        if (options.InfluenceRadiusDip is < 1 or > 2000)
        {
            problems.Add($"The {what} influence radius must be 1-2000 DIP.");
        }
    }

    private static void ValidateSpring(CanvasSpring spring, string what, List<string> problems)
    {
        if (spring.PeriodSeconds is <= 0 or > 5)
        {
            problems.Add($"The {what} period must be 0-5 seconds.");
        }

        if (spring.DampingRatio is <= 0 or > 2)
        {
            problems.Add($"The {what} damping ratio must be 0-2.");
        }
    }
}
