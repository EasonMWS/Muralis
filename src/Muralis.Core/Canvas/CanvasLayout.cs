namespace Muralis.Core.Canvas;

/// <summary>
/// The desktop canvas prototype document: every item plus the parameters that shape hover
/// magnification, motion and the edge dock. It is persisted on its own, away from application
/// settings, so the prototype can be reset or hand-edited without touching
/// <c>settings.json</c>.
/// </summary>
public sealed class CanvasLayout
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Bumped whenever the on-disk shape changes in a breaking way.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public CanvasProximityOptions Proximity { get; set; } = new();

    public CanvasMotionOptions Motion { get; set; } = new();

    public CanvasDockOptions Dock { get; set; } = new();

    /// <summary>All items. Dock items appear in rail order; free items use <see cref="CanvasItem.Z"/>.</summary>
    public List<CanvasItem> Items { get; set; } = [];

    /// <summary>
    /// The default prototype: four free items in a row around the display centre (so hovering
    /// the middle of the row shows the magnification bell) and four items in the left dock.
    /// </summary>
    public static CanvasLayout CreateSeed()
    {
        var layout = new CanvasLayout();
        var row = new (string Id, string Name, string Icon, double Offset)[]
        {
            ("steam", "Steam", "steam", -255),
            ("chrome", "Chrome", "chrome", -85),
            ("blender", "Blender", "blender", 85),
            ("comfyui", "ComfyUI", "comfyui", 255),
        };

        var z = 0;
        foreach (var (id, name, icon, offset) in row)
        {
            layout.Items.Add(new CanvasItem
            {
                Id = id,
                Name = name,
                IconKey = icon,
                Placement = CanvasItemPlacement.Free,
                Anchor = CanvasAnchor.Center,
                OffsetXDip = offset,
                OffsetYDip = -220,
                Z = z++,
            });
        }

        foreach (var (id, name, icon) in new (string, string, string)[]
        {
            ("files", "Files", "files"),
            ("music", "Music", "music"),
            ("settings", "Settings", "settings"),
            ("terminal", "Terminal", "terminal"),
        })
        {
            layout.Items.Add(new CanvasItem
            {
                Id = id,
                Name = name,
                IconKey = icon,
                Placement = CanvasItemPlacement.Dock,
                Z = z++,
            });
        }

        return layout;
    }

    /// <summary>
    /// Checks the facts consumers rely on. Returns an empty list when the layout is coherent;
    /// the store falls back to the seed layout otherwise.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (SchemaVersion <= 0)
        {
            problems.Add("The layout schema version must be positive.");
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

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                problems.Add("Every item needs a non-empty id.");
            }
            else if (!ids.Add(item.Id))
            {
                problems.Add($"The item id '{item.Id}' is used twice.");
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                problems.Add($"The item '{item.Id}' needs a name.");
            }

            if (string.IsNullOrWhiteSpace(item.IconKey))
            {
                problems.Add($"The item '{item.Id}' needs an icon key.");
            }

            if (item.SizeDip is < 16 or > 512)
            {
                problems.Add($"The item '{item.Id}' has a size outside 16-512 DIP.");
            }

            if (!double.IsFinite(item.OffsetXDip) || !double.IsFinite(item.OffsetYDip))
            {
                problems.Add($"The item '{item.Id}' has a non-finite offset.");
            }
        }

        return problems;
    }

    /// <summary>Deep copy, safe to hand to another thread while the canvas keeps editing this one.</summary>
    public CanvasLayout Clone() => new()
    {
        SchemaVersion = SchemaVersion,
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
