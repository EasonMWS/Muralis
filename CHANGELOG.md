# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-09-14

The first public release: a modern, native wallpaper manager for Windows 10 & 11.

### Added

**Application shell**

- WinUI 3 (Windows App SDK) app with a custom title bar, Mica backdrop and a
  NavigationView shell; light, dark and system themes
- Home, Browse, Details, Library, Favorites and Settings pages with designed
  loading, empty and error states
- Unpackaged, self-contained distribution: unzip and run, no installer required

**Wallpapers**

- Bing daily images provider (no API key needed) with automatic offline fallback to a
  sample set; provider picker on the Browse page
- Download service with progress, cancellation and conflict-safe file names; downloads
  join the catalog with their real dimensions and file size
- On-disk thumbnail cache so online grids render instantly and offline
- WebP/AVIF images are transcoded automatically into a format Windows can use

**Desktop integration**

- Apply wallpapers with Fill, Fit, Stretch, Center, Tile and Span styles through
  `SystemParametersInfo`, with per-monitor targeting via `IDesktopWallpaper`
- Connected displays listed in Settings; choose a target display on the details page
- Auto rotation: shuffle favorites or a folder every 15 minutes to 24 hours, with a
  "Shuffle now" action
- Run at sign-in through the per-user registry
- System tray icon (native `Shell_NotifyIcon`) with show / next wallpaper / settings /
  exit; optional close-to-tray so rotation keeps running in the background

**Library and data**

- Local library: multi-select import through the system file picker, deduplicated
  in-place references, removal that never touches the original files
- SQLite catalog (`%LOCALAPPDATA%\Muralis\muralis.db`, versioned schema) persisting
  wallpapers, favorites and usage history across restarts
- Favorites work for any wallpaper, even one that is not in the library yet
- "Recently used" feed on Home backed by bounded usage history
- Settings persisted as JSON with atomic writes

**Engineering**

- Layered solution: `Muralis.App` (UI + Windows interop), `Muralis.Core`
  (platform-agnostic domain), `Muralis.Core.Tests` (xUnit)
- Dependency injection, Serilog rolling file logs, global exception handling
- CI (build + test) and tag-driven release workflows on GitHub Actions

[Unreleased]: https://github.com/EasonMWS/Muralis/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/EasonMWS/Muralis/releases/tag/v0.1.0
