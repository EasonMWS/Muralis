# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Project skeleton: WinUI 3 shell with custom title bar, Mica backdrop and NavigationView
- MVVM foundation with dependency injection and structured logging (Serilog rolling files)
- Home, Browse, Detail, Library, Favorites and Settings pages
- Settings service with JSON persistence in `%LOCALAPPDATA%\Muralis`
- Local wallpaper library: multi-select import through the system file picker, deduplicated
  in-place references, removal that never touches the original files (M2)
- Apply wallpapers to the desktop with Fill/Fit/Stretch/Center/Tile/Span styles, backed by
  `SystemParametersInfo` and per-monitor `IDesktopWallpaper` with graceful fallback (M2)
- Automatic WebP/AVIF → JPEG transcoding for formats Windows cannot use as a background (M2)
- Connected displays listing in Settings and per-display targeting on the details page (M2)
- SQLite catalog (`%LOCALAPPDATA%\Muralis\muralis.db`, schema v1): imported wallpapers,
  favorites and usage history persist across restarts (M3)
- Favorites works for any wallpaper — even one that is not in the library yet (M3)
- "Recently used" feed on Home backed by bounded usage history (200 entries) (M3)
- Bing daily images provider (no API key): Home and Browse show fresh daily wallpapers,
  with automatic fallback to the sample set when offline (M4)
- Download service with progress, cancellation and conflict-safe file names; downloads
  are saved to the configured folder and join the catalog with real dimensions (M4)
- Thumbnail cache so online grids render instantly and work offline (M4)
- Provider picker on the Browse page (Bing / sample wallpapers) (M4)

### Fixed

- Re-clicking the already-selected navigation item now leaves sub-pages such as details (M2)

## [0.1.0] - TBD

Initial public release. Scope tracked in the [README roadmap](README.md#roadmap).
