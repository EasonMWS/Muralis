# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

**Localization**

- English and Simplified Chinese UI; a display-language setting with
  "Follow system", "English" and "简体中文" that applies instantly, needs no restart
  and persists across restarts
- All user-facing text now comes from embedded resources (`Strings/Resources.resx`
  plus one satellite assembly per language); view models refresh their text live
  through a weakly-referenced language-change notification

**Wallpaper sources**

- Pluggable provider architecture (`IWallpaperProvider` + `WallpaperProviderManager`):
  sources can be enabled/disabled, one is the default, and Browse can search a single
  source or all enabled sources at once — a failing source never hides the others
- New sources: **Wallhaven** (SFW only) and **NASA APOD** (free API key), alongside the
  existing Bing feed and the sample set bundled with Windows
- API keys live in `%LOCALAPPDATA%\Muralis\providers.json` or
  `MURALIS_PROVIDER_<ID>_API_KEY` environment variables — never in the repository;
  Settings shows which sources still need a key ([docs](docs/providers.md))
- Provider responses are cached on disk with per-source freshness windows, and idempotent
  requests are retried with exponential backoff (honouring `Retry-After`)
- Browse warns when some sources are unreachable but still shows the rest; search results
  are merged with the local catalog so favorites and downloaded files appear immediately

### Changed

- Downloads go through the owning provider, so a source can transform or register the
  download URL (the hook Unsplash-style APIs require)
- Formatting helpers return neutral values instead of English fallbacks
  ("Unknown resolution", "Online") so no untranslated text can reach the UI

### Fixed

- The window no longer shows the default Windows icon: the custom title bar replaces
  the system one, and the Muralis icon is used by the taskbar, Alt+Tab and Explorer
- The featured wallpaper on Home shows the cached thumbnail instead of a blank card
  while the full image has not been downloaded yet
- The details page no longer shows bare labels for wallpapers whose size or
  location is not known yet

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
