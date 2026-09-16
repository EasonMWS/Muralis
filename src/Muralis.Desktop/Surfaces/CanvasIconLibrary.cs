using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// The prototype's glyph set: every item gets a rounded tile with a simple glyph, drawn from
/// composition shapes at a fixed 96-unit design size, so a tile stays sharp at any DPI and costs
/// no image decoding at all. It is what an item looks like while its real icon is being read and
/// what an item without one keeps — addresses, and items whose file has no icon to give.
/// </summary>
internal static class CanvasIconLibrary
{
    /// <summary>The design box every icon is drawn in; item visuals scale it to their own size.</summary>
    internal const float DesignSize = 96f;

    private static readonly Color Glyph = Color.FromArgb(235, 255, 255, 255);

    /// <summary>The selected item's outline: bright enough to read on any wallpaper.</summary>
    private static readonly Color Selection = Color.FromArgb(235, 118, 190, 255);

    /// <summary>The missing badge's amber, and the dark ink of the mark inside it.</summary>
    private static readonly Color Warning = Color.FromArgb(255, 240, 160, 40);
    private static readonly Color Ink = Color.FromArgb(255, 32, 24, 12);

    /// <summary>Builds the tile for an icon key; unknown keys get a neutral placeholder.</summary>
    internal static ShapeVisual Create(Compositor compositor, string iconKey, float size)
    {
        ArgumentNullException.ThrowIfNull(compositor);

        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(size, size);
        visual.Shapes.Add(Tile(compositor, size, PaletteFor(iconKey)));

        foreach (var shape in GlyphFor(compositor, iconKey?.ToLowerInvariant() ?? string.Empty, size))
        {
            visual.Shapes.Add(shape);
        }

        return visual;
    }

    /// <summary>A plain rounded panel, used for the dock's backdrop.</summary>
    internal static CompositionSpriteShape Panel(Compositor compositor, float width, float height, float corner, Color fill) =>
        RoundedRect(compositor, 0, 0, width, height, corner, fill: fill);

    /// <summary>
    /// The outline that marks the selected item. It is a border with nothing inside it, drawn over
    /// the item, so it reads the same whether the item is still a tile or already a real icon.
    /// </summary>
    internal static ShapeVisual SelectionRing(Compositor compositor, float size)
    {
        ArgumentNullException.ThrowIfNull(compositor);

        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(size, size);
        visual.Shapes.Add(RoundedRect(
            compositor,
            size * 0.015f,
            size * 0.015f,
            size * 0.97f,
            size * 0.97f,
            size * 0.25f,
            stroke: Selection,
            thickness: Math.Max(2f, size * 0.045f)));
        return visual;
    }

    /// <summary>
    /// The mark an item wears when its target is gone: a badge in the item's top-right corner, so an
    /// item whose file was deleted or moved says so while staying where the user put it.
    /// </summary>
    internal static ShapeVisual MissingBadge(Compositor compositor, float size)
    {
        ArgumentNullException.ThrowIfNull(compositor);

        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(size, size);

        var radius = size * 0.17f;
        var center = (X: size - radius, Y: radius);
        visual.Shapes.Add(Ellipse(compositor, center.X, center.Y, radius, radius, fill: Warning));
        visual.Shapes.Add(Line(
            compositor,
            center.X,
            center.Y - (radius * 0.55f),
            center.X,
            center.Y + (radius * 0.12f),
            Ink,
            Math.Max(1.5f, radius * 0.3f)));
        visual.Shapes.Add(Ellipse(
            compositor,
            center.X,
            center.Y + (radius * 0.5f),
            Math.Max(1f, radius * 0.16f),
            Math.Max(1f, radius * 0.16f),
            fill: Ink));

        return visual;
    }

    private static CompositionSpriteShape Tile(Compositor compositor, float size, Color color) =>
        RoundedRect(compositor, size * 0.03f, size * 0.03f, size * 0.94f, size * 0.94f, size * 0.23f, fill: color);

    private static IEnumerable<CompositionSpriteShape> GlyphFor(Compositor compositor, string iconKey, float size)
    {
        // Every glyph is authored on a 96-unit grid and scaled to the requested size.
        var s = size / DesignSize;

        return iconKey switch
        {
            "steam" => Steam(compositor, s),
            "chrome" => Chrome(compositor, s),
            "blender" => Blender(compositor, s),
            "comfyui" => ComfyUi(compositor, s),
            "files" => Files(compositor, s),
            "folder" => Files(compositor, s),
            "music" => Music(compositor, s),
            "settings" => Settings(compositor, s),
            "terminal" => Terminal(compositor, s),
            "url" => Globe(compositor, s),
            _ => Placeholder(compositor, s),
        };
    }

    private static IEnumerable<CompositionSpriteShape> Steam(Compositor c, float s) =>
    [
        Ellipse(c, 50 * s, 44 * s, 26 * s, 26 * s, stroke: Glyph, thickness: 7 * s),
        Ellipse(c, 42 * s, 50 * s, 9 * s, 9 * s, fill: Glyph),
        Line(c, 38 * s, 52 * s, 22 * s, 70 * s, Glyph, 8 * s),
    ];

    private static IEnumerable<CompositionSpriteShape> Chrome(Compositor c, float s) =>
    [
        Ellipse(c, 48 * s, 48 * s, 27 * s, 27 * s, stroke: Glyph, thickness: 8 * s),
        Ellipse(c, 48 * s, 48 * s, 10 * s, 10 * s, fill: Glyph),
        Line(c, 48 * s, 48 * s, 48 * s, 23 * s, Glyph, 6 * s),
        Line(c, 48 * s, 48 * s, 27 * s, 61 * s, Glyph, 6 * s),
        Line(c, 48 * s, 48 * s, 70 * s, 61 * s, Glyph, 6 * s),
    ];

    private static IEnumerable<CompositionSpriteShape> Blender(Compositor c, float s) =>
    [
        Ellipse(c, 44 * s, 54 * s, 24 * s, 24 * s, stroke: Glyph, thickness: 7 * s),
        Ellipse(c, 52 * s, 46 * s, 7 * s, 7 * s, fill: Glyph),
        Line(c, 22 * s, 30 * s, 58 * s, 56 * s, Glyph, 7 * s),
    ];

    private static IEnumerable<CompositionSpriteShape> ComfyUi(Compositor c, float s)
    {
        var shapes = new List<CompositionSpriteShape>();
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var middle = row == 1 && column == 1;
                shapes.Add(RoundedRect(
                    c,
                    (20 + (column * 20)) * s,
                    (20 + (row * 20)) * s,
                    16 * s,
                    16 * s,
                    5 * s,
                    fill: middle ? Glyph : Color.FromArgb(150, 255, 255, 255)));
            }
        }

        return shapes;
    }

    private static IEnumerable<CompositionSpriteShape> Files(Compositor c, float s) =>
    [
        RoundedRect(c, 16 * s, 22 * s, 28 * s, 16 * s, 6 * s, fill: Glyph),
        RoundedRect(c, 14 * s, 30 * s, 68 * s, 46 * s, 10 * s, stroke: Glyph, thickness: 7 * s),
    ];

    private static IEnumerable<CompositionSpriteShape> Music(Compositor c, float s) =>
    [
        Ellipse(c, 32 * s, 68 * s, 9 * s, 9 * s, fill: Glyph),
        Line(c, 40 * s, 68 * s, 40 * s, 34 * s, Glyph, 7 * s),
        Ellipse(c, 64 * s, 60 * s, 9 * s, 9 * s, fill: Glyph),
        Line(c, 72 * s, 60 * s, 72 * s, 26 * s, Glyph, 7 * s),
        Line(c, 40 * s, 34 * s, 72 * s, 26 * s, Glyph, 7 * s),
    ];

    private static IEnumerable<CompositionSpriteShape> Settings(Compositor c, float s)
    {
        var shapes = new List<CompositionSpriteShape>
        {
            Ellipse(c, 48 * s, 48 * s, 19 * s, 19 * s, stroke: Glyph, thickness: 8 * s),
        };

        for (var tooth = 0; tooth < 8; tooth++)
        {
            var angle = tooth * Math.PI / 4.0;
            var (sin, cos) = (Math.Sin(angle), Math.Cos(angle));
            shapes.Add(Line(
                c,
                (float)((48 + (cos * 27)) * s),
                (float)((48 + (sin * 27)) * s),
                (float)((48 + (cos * 37)) * s),
                (float)((48 + (sin * 37)) * s),
                Glyph,
                7 * s));
        }

        return shapes;
    }

    private static IEnumerable<CompositionSpriteShape> Terminal(Compositor c, float s) =>
    [
        RoundedRect(c, 12 * s, 20 * s, 72 * s, 56 * s, 12 * s, stroke: Glyph, thickness: 7 * s),
        Line(c, 28 * s, 40 * s, 38 * s, 48 * s, Glyph, 6 * s),
        Line(c, 38 * s, 48 * s, 28 * s, 56 * s, Glyph, 6 * s),
        Line(c, 46 * s, 58 * s, 64 * s, 58 * s, Glyph, 6 * s),
    ];

    private static IEnumerable<CompositionSpriteShape> Globe(Compositor c, float s) =>
    [
        Ellipse(c, 48 * s, 48 * s, 27 * s, 27 * s, stroke: Glyph, thickness: 7 * s),
        Line(c, 21 * s, 48 * s, 75 * s, 48 * s, Glyph, 6 * s),
        Ellipse(c, 48 * s, 48 * s, 12 * s, 27 * s, stroke: Glyph, thickness: 6 * s),
    ];

    private static IEnumerable<CompositionSpriteShape> Placeholder(Compositor c, float s) =>
    [
        RoundedRect(c, 20 * s, 20 * s, 56 * s, 56 * s, 14 * s, stroke: Glyph, thickness: 7 * s),
        Ellipse(c, 48 * s, 48 * s, 9 * s, 9 * s, fill: Glyph),
    ];

    private static Color PaletteFor(string iconKey) => iconKey?.ToLowerInvariant() switch
    {
        "steam" => Color.FromArgb(255, 27, 40, 56),
        "chrome" => Color.FromArgb(255, 44, 90, 160),
        "blender" => Color.FromArgb(255, 232, 125, 13),
        "comfyui" => Color.FromArgb(255, 90, 79, 207),
        "files" => Color.FromArgb(255, 232, 163, 61),
        "folder" => Color.FromArgb(255, 232, 163, 61),
        "music" => Color.FromArgb(255, 217, 79, 112),
        "settings" => Color.FromArgb(255, 107, 114, 128),
        "terminal" => Color.FromArgb(255, 16, 185, 129),
        "url" => Color.FromArgb(255, 59, 130, 246),
        _ => Color.FromArgb(255, 75, 85, 99),
    };

    private static CompositionSpriteShape RoundedRect(
        Compositor compositor,
        float x,
        float y,
        float width,
        float height,
        float corner,
        Color? fill = null,
        Color? stroke = null,
        float thickness = 0)
    {
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2(width, height);
        geometry.CornerRadius = new Vector2(corner, corner);

        var shape = compositor.CreateSpriteShape(geometry);
        shape.Offset = new Vector2(x, y);
        Apply(compositor, shape, fill, stroke, thickness);
        return shape;
    }

    private static CompositionSpriteShape Ellipse(
        Compositor compositor,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        Color? fill = null,
        Color? stroke = null,
        float thickness = 0)
    {
        var geometry = compositor.CreateEllipseGeometry();
        geometry.Center = new Vector2(centerX, centerY);
        geometry.Radius = new Vector2(radiusX, radiusY);

        var shape = compositor.CreateSpriteShape(geometry);
        Apply(compositor, shape, fill, stroke, thickness);
        return shape;
    }

    private static CompositionSpriteShape Line(
        Compositor compositor,
        float startX,
        float startY,
        float endX,
        float endY,
        Color color,
        float thickness)
    {
        var geometry = compositor.CreateLineGeometry();
        geometry.Start = new Vector2(startX, startY);
        geometry.End = new Vector2(endX, endY);

        var shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeStartCap = CompositionStrokeCap.Round;
        shape.StrokeEndCap = CompositionStrokeCap.Round;
        Apply(compositor, shape, null, color, thickness);
        return shape;
    }

    private static void Apply(Compositor compositor, CompositionSpriteShape shape, Color? fill, Color? stroke, float thickness)
    {
        if (fill is { } fillColor)
        {
            shape.FillBrush = compositor.CreateColorBrush(fillColor);
        }

        if (stroke is { } strokeColor)
        {
            shape.StrokeBrush = compositor.CreateColorBrush(strokeColor);
            shape.StrokeThickness = thickness;
        }
    }
}
