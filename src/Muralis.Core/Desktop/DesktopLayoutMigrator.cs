using System.Text.Json;
using System.Text.Json.Serialization;
using Muralis.Core.Canvas;

namespace Muralis.Core.Desktop;

/// <summary>What came of reading a Phase 2 prototype layout.</summary>
public sealed record DesktopLayoutMigrationResult(DesktopLayout? Layout, int DroppedItems, string? Error);

/// <summary>
/// Reads the Phase 2 prototype layout (schema 1) once and brings its parameters forward. The two
/// documents only share the options that shape the canvas: version 1 items were tiles with a glyph
/// key and nothing to open, so they cannot become desktop items and are deliberately left behind —
/// the caller reports the count instead of inventing targets for them.
/// </summary>
public static class DesktopLayoutMigrator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Migrates <paramref name="json"/>, or explains why it cannot be migrated. A failing migration
    /// never writes anything: the caller keeps the original file and starts from an empty layout.
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

        // The prototype wrote 1; a hand-edited file may have left the version out altogether.
        if (prototype.SchemaVersion is not (0 or 1))
        {
            return new DesktopLayoutMigrationResult(null, 0, $"schema version {prototype.SchemaVersion} is not a prototype layout");
        }

        var layout = new DesktopLayout
        {
            Proximity = prototype.Proximity ?? new CanvasProximityOptions(),
            Motion = prototype.Motion ?? new CanvasMotionOptions(),
            Dock = prototype.Dock ?? new CanvasDockOptions(),
        };

        var problems = layout.Validate();
        if (problems.Count > 0)
        {
            return new DesktopLayoutMigrationResult(null, 0, string.Join(" ", problems));
        }

        return new DesktopLayoutMigrationResult(layout, prototype.Items?.Count ?? 0, null);
    }

    /// <summary>The prototype document, only as far as the migration needs to see it.</summary>
    private sealed class PrototypeLayout
    {
        public int SchemaVersion { get; set; }

        public CanvasProximityOptions? Proximity { get; set; }

        public CanvasMotionOptions? Motion { get; set; }

        public CanvasDockOptions? Dock { get; set; }

        /// <summary>Only counted; the tiles themselves have nothing to carry over.</summary>
        public List<JsonElement>? Items { get; set; }
    }
}
