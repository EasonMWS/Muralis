using System.Text.Json;
using System.Text.Json.Serialization;
using Muralis.Core.Canvas;
using Muralis.Core.Dock;

namespace Muralis.Core.Desktop;

/// <summary>What came of reading an older desktop layout.</summary>
public sealed record DesktopLayoutMigrationResult(DesktopLayout? Layout, int DroppedItems, string? Error);

/// <summary>
/// Brings the older desktop documents forward into the current shape. Each step is a one-way read of
/// a file that is left exactly where it was: a migration that cannot be understood costs the user
/// nothing, because the caller keeps the original and starts from an empty layout.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Version 1 (the Phase 2 prototype) held tiles with a glyph key and nothing to open. Its
/// parameters carry over and its items cannot, so they are counted and left behind.</item>
/// <item>Version 2 held real items but tagged the docked ones with a <c>placement</c> field. Version
/// 3 names them in the dock's own entry list instead, so this step reads the tag once and turns it
/// into entries, and moves the dock's spring in with the rest of the dock's parameters.</item>
/// </list>
/// </remarks>
public static class DesktopLayoutMigrator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The schema version the document says it is, or null when the text is not a document at all.
    /// Read without binding to any version's shape, so a file written by a newer version can be
    /// recognised and refused rather than silently half-read. A version that is not there at all is
    /// zero: a hand-edited file may have left it out, and the current shape is what it is then
    /// judged against.
    /// </summary>
    public static int? SchemaVersionOf(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // The name is read without regard to case, exactly as the document itself is read.
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value)
                    ? value
                    : null;
            }

            return 0;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Migrates a Phase 2 prototype layout (version 1, or no version at all): the parameters carry
    /// over, the tiles do not.
    /// </summary>
    public static DesktopLayoutMigrationResult MigrateFromPrototype(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        PrototypeLayout? prototype;
        try
        {
            prototype = JsonSerializer.Deserialize<PrototypeLayout>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return new DesktopLayoutMigrationResult(null, 0, $"it is not valid JSON ({ex.Message})");
        }

        if (prototype is null)
        {
            return new DesktopLayoutMigrationResult(null, 0, "the file is empty");
        }

        if (prototype.SchemaVersion is not (0 or 1))
        {
            return new DesktopLayoutMigrationResult(null, 0, $"schema version {prototype.SchemaVersion} is not a prototype layout");
        }

        var layout = new DesktopLayout
        {
            Proximity = prototype.Proximity ?? new CanvasProximityOptions(),
            Motion = prototype.Motion ?? new CanvasMotionOptions(),
            Dock = PrototypeDockOptions(prototype.Dock),
        };

        var problems = layout.Validate();
        if (problems.Count > 0)
        {
            return new DesktopLayoutMigrationResult(null, 0, string.Join(" ", problems));
        }

        return new DesktopLayoutMigrationResult(layout, prototype.Items?.Count ?? 0, null);
    }

    /// <summary>
    /// Upgrades a version 2 layout: the docked items' placement tag becomes dock entries, and the
    /// dock's old scale pair becomes the peek it leaves showing when it is away.
    /// </summary>
    public static DesktopLayoutMigrationResult UpgradeFromVersion2(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Version2Layout? old;
        try
        {
            old = JsonSerializer.Deserialize<Version2Layout>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A target whose discriminator is missing or unknown arrives as a shape the serialiser
            // will not map rather than as broken JSON.
            return new DesktopLayoutMigrationResult(null, 0, $"it cannot be read as a version 2 layout ({ex.Message})");
        }

        if (old is null)
        {
            return new DesktopLayoutMigrationResult(null, 0, "the file is empty");
        }

        var items = old.Items ?? [];
        var dock = old.Dock ?? new Version2Dock();

        // The old dock was a rail the layout drew: an item was in it when it said so. That tag is the
        // only record of the order the user had, so the entries are read straight off the item list.
        var entries = items
            .Where(item => item is not null && item.Placement == Version2Placement.Dock)
            .Select(item => new DockEntry { ItemId = item!.Id ?? string.Empty })
            .ToList();

        var thickness = dock.RailThicknessDip();
        var peek = Math.Clamp(Math.Round(thickness * Math.Max(0, dock.CollapsedScale)), 0, thickness);

        var layout = new DesktopLayout
        {
            Proximity = old.Proximity ?? new CanvasProximityOptions(),
            Motion = new CanvasMotionOptions { Hover = old.Motion?.Hover ?? new CanvasSpring() },
            Dock = new DockOptions
            {
                Enabled = entries.Count > 0,
                Edge = dock.Edge,
                AutoHide = true,
                TriggerThicknessDip = dock.TriggerSizeDip,
                PeekSizeDip = peek,
                ShowDelayMilliseconds = dock.ShowDelayMilliseconds,
                HideDelayMilliseconds = dock.HideDelayMilliseconds,
                ItemSizeDip = dock.ItemSizeDip,
                SpacingDip = dock.ItemSpacingDip,
                EdgeMarginDip = dock.EdgeMarginDip,
                PaddingDip = dock.PaddingDip,
                MaxScale = dock.Proximity?.MaxScale ?? 1.6,
                InfluenceRadiusDip = dock.Proximity?.InfluenceRadiusDip ?? 130,
                Falloff = dock.Proximity?.Falloff ?? ProximityFalloff.Smoothstep,
                Spring = old.Motion?.Dock ?? new CanvasSpring { PeriodSeconds = 0.34, DampingRatio = 0.78 },
                Entries = entries,
            },
            Items = items
                .Where(item => item is not null)
                .Select(item => item!.ToItem())
                .ToList(),
        };

        var problems = layout.Validate();
        if (problems.Count > 0)
        {
            return new DesktopLayoutMigrationResult(null, 0, string.Join(" ", problems));
        }

        return new DesktopLayoutMigrationResult(layout, 0, null);
    }

    /// <summary>
    /// The prototype's dock, as far as it can still mean anything: it had no entries and no reveal
    /// state, and its two scales described a rail that grew in place, so all that carries over is
    /// where it sat and how long its delays were.
    /// </summary>
    private static DockOptions PrototypeDockOptions(PrototypeDock? dock)
    {
        if (dock is null)
        {
            return new DockOptions();
        }

        return new DockOptions
        {
            Edge = dock.Edge,
            ShowDelayMilliseconds = Math.Max(0, dock.ShowDelayMilliseconds),
            HideDelayMilliseconds = Math.Max(0, dock.HideDelayMilliseconds),
            TriggerThicknessDip = Math.Clamp(dock.TriggerSizeDip, 0, 200),
            ItemSizeDip = Math.Clamp(dock.ItemSizeDip, 8, 512),
            SpacingDip = Math.Clamp(dock.ItemSpacingDip, 0, 512),
            EdgeMarginDip = Math.Clamp(dock.EdgeMarginDip, 0, 512),
            PaddingDip = Math.Clamp(dock.PaddingDip, 0, 512),
            MaxScale = dock.Proximity?.MaxScale ?? 1.6,
            InfluenceRadiusDip = dock.Proximity?.InfluenceRadiusDip ?? 130,
            Falloff = dock.Proximity?.Falloff ?? ProximityFalloff.Smoothstep,
        };
    }

    /// <summary>The prototype document, only as far as the migration needs to see it.</summary>
    private sealed class PrototypeLayout
    {
        public int SchemaVersion { get; set; }

        public CanvasProximityOptions? Proximity { get; set; }

        public CanvasMotionOptions? Motion { get; set; }

        public PrototypeDock? Dock { get; set; }

        /// <summary>Only counted; the tiles themselves have nothing to carry over.</summary>
        public List<JsonElement>? Items { get; set; }
    }

    /// <summary>The prototype's dock parameters under the names they had then.</summary>
    private sealed class PrototypeDock
    {
        public DockEdge Edge { get; set; } = DockEdge.Left;

        public double TriggerSizeDip { get; set; } = 24;

        public int ShowDelayMilliseconds { get; set; } = 150;

        public int HideDelayMilliseconds { get; set; } = 600;

        public double ItemSizeDip { get; set; } = 56;

        public double ItemSpacingDip { get; set; } = 16;

        public double EdgeMarginDip { get; set; } = 10;

        public double PaddingDip { get; set; } = 10;

        public CanvasProximityOptions? Proximity { get; set; }
    }

    /// <summary>A version 2 document, read only to be brought forward.</summary>
    private sealed class Version2Layout
    {
        public CanvasProximityOptions? Proximity { get; set; }

        public Version2Motion? Motion { get; set; }

        public Version2Dock? Dock { get; set; }

        public List<Version2Item>? Items { get; set; }
    }

    private sealed class Version2Motion
    {
        public CanvasSpring? Hover { get; set; }

        public CanvasSpring? Dock { get; set; }
    }

    private sealed class Version2Dock
    {
        public DockEdge Edge { get; set; } = DockEdge.Left;

        public double TriggerSizeDip { get; set; } = 24;

        public int ShowDelayMilliseconds { get; set; } = 150;

        public int HideDelayMilliseconds { get; set; } = 600;

        public double CollapsedScale { get; set; }

        public double ItemSizeDip { get; set; } = 56;

        public double ItemSpacingDip { get; set; } = 16;

        public double EdgeMarginDip { get; set; } = 10;

        public double PaddingDip { get; set; } = 10;

        public CanvasProximityOptions? Proximity { get; set; }

        public double RailThicknessDip() => ItemSizeDip + (2 * PaddingDip);
    }

    /// <summary>Where a version 2 item said it lived.</summary>
    private enum Version2Placement
    {
        Free,
        Dock,
    }

    private sealed class Version2Item
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? IconKey { get; set; }

        public Version2Placement Placement { get; set; }

        public CanvasAnchor Anchor { get; set; } = CanvasAnchor.Center;

        public double OffsetXDip { get; set; }

        public double OffsetYDip { get; set; }

        public double SizeDip { get; set; } = 96;

        public int Z { get; set; }

        public bool IsVisible { get; set; } = true;

        public DesktopItemTarget? Target { get; set; }

        public DesktopItem ToItem() => new()
        {
            Id = Id ?? string.Empty,
            Name = Name ?? string.Empty,
            IconKey = IconKey ?? string.Empty,
            Anchor = Anchor,
            OffsetXDip = OffsetXDip,
            OffsetYDip = OffsetYDip,
            SizeDip = SizeDip,
            Z = Z,
            IsVisible = IsVisible,
            Target = Target!,
        };
    }
}
