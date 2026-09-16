using Muralis.Core.Canvas;

namespace Muralis.Core.Desktop;

/// <summary>
/// The desktop layout document: the items the user put on the desktop, plus the parameters that
/// shape hover magnification, motion and the edge dock. It is persisted on its own, away from
/// application settings, so a hand-edit or a reset never touches <c>settings.json</c>.
/// </summary>
/// <remarks>
/// The first desktop document that describes real things: every item carries a typed
/// <see cref="DesktopItem.Target"/>. Version 1 held nothing but prototype tiles, so a migrated
/// document keeps the parameters and starts with no items — nothing that could not open anything
/// is carried over.
/// </remarks>
public sealed class DesktopLayout
{
    /// <summary>Version 2 introduced typed item targets and dropped the prototype tiles.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Document marker, so a file that is not a Muralis desktop layout is refused early.</summary>
    public const string DocumentKind = "muralis.desktopLayout";

    /// <summary>Bumped whenever the on-disk shape changes in a breaking way.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Kind { get; set; } = DocumentKind;

    public CanvasProximityOptions Proximity { get; set; } = new();

    public CanvasMotionOptions Motion { get; set; } = new();

    public CanvasDockOptions Dock { get; set; } = new();

    /// <summary>All items. Dock items appear in rail order; free items use <see cref="DesktopItem.Z"/>.</summary>
    public List<DesktopItem> Items { get; set; } = [];

    /// <summary>
    /// The layout a fresh install starts from: the parameters that shape the canvas and no items
    /// at all. Items only ever appear because the user added one.
    /// </summary>
    public static DesktopLayout CreateEmpty() => new();

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
        ValidateProximity(Dock.Proximity, "dock proximity", problems);

        if (Motion.Hover is null || Motion.Dock is null)
        {
            problems.Add("Both motion springs must be present.");
        }
        else
        {
            ValidateSpring(Motion.Hover, "hover spring", problems);
            ValidateSpring(Motion.Dock, "dock spring", problems);
        }

        if (Dock.TriggerSizeDip is < 0 or > 200)
        {
            problems.Add("The dock trigger size must be between 0 and 200 DIP.");
        }

        if (Dock.ShowDelayMilliseconds < 0 || Dock.HideDelayMilliseconds < 0)
        {
            problems.Add("Dock delays cannot be negative.");
        }

        if (Dock.CollapsedScale is < 0 or > 2 || Dock.ExpandedScale is < 0 or > 2)
        {
            problems.Add("Dock scales must be between 0 and 2.");
        }
        else if (Dock.ExpandedScale <= Dock.CollapsedScale)
        {
            problems.Add("The expanded dock scale must exceed the collapsed one.");
        }

        if (Dock.ItemSizeDip is < 8 or > 512 || Dock.ItemSpacingDip is < 0 or > 512)
        {
            problems.Add("Dock item size must be 8-512 DIP and spacing 0-512 DIP.");
        }

        if (Dock.EdgeMarginDip is < 0 or > 512 || Dock.PaddingDip is < 0 or > 512)
        {
            problems.Add("Dock margin and padding must be 0-512 DIP.");
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
