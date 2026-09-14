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

### Fixed

- Re-clicking the already-selected navigation item now leaves sub-pages such as details (M2)

## [0.1.0] - TBD

Initial public release. Scope tracked in the [README roadmap](README.md#roadmap).
