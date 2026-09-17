# Muralis Design Foundation v1.0

The foundation lives inside `Muralis.App/UI` so it can evolve without adding a new assembly or crossing the existing App/Core/Desktop ownership boundaries.

## Resource order

`App.xaml` merges resources in dependency order:

1. Colors
2. Typography
3. Spacing
4. Radius
5. Elevation
6. Motion
7. Materials
8. Shared control styles
9. Compatibility styles used by existing pages

Feature views should use semantic resources such as `MuralisAccentPrimaryBrush`, `MuralisSurfaceMediumBrush`, `MuralisRadiusS`, and `MuralisSpace16`. A future theme package can replace the accent, surface tint, glow tint, and wallpaper mood resources centrally.

## Materials and controls

`GlassSurface` provides Low, Medium, and High material levels. The levels vary opacity, border visibility, shadow elevation, and edge highlight rather than applying blur to every element.

The reference controls are:

- `MuralisButton` with Primary, Secondary, and Ghost variants
- `MuralisIconButton`
- `MuralisCard` with Default, Interactive, and Selected variants
- `MuralisNavigationItem`
- `WallpaperCard`
- `DockIcon`

`DockIcon.MotionTarget` is intentionally exposed and its local preview motion can be disabled. The Phase 4 dock magnification engine can therefore drive scale and translation continuously without replacing the control template.

## Motion

Shared motion is defined at 120 ms (Fast), 180 ms (Standard), and 280 ms (Slow). Hover, press, entrance, and restrained spring helpers animate transforms and opacity without triggering layout recalculation.

## Design Playground

Press `Ctrl+Shift+D` in the main window to open the development-only Design Playground route. It demonstrates typography, semantic colors, all three material levels, button and card states, navigation states, a wallpaper card, and dock icon states against a varied built-in background.

The route is intentionally absent from the production navigation pane.

## Reference migration

The main shell, Home, Settings card compatibility styles, and `WallpaperCard` use the foundation. Other pages remain compatible and can migrate incrementally; no Desktop, Shell, takeover, Explorer recovery, or persistence code is coupled to this UI layer.
