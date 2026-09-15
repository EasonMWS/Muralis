# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-09-15

The second release: browsing gained filters, tags and categories, downloads got a queue with
retries, and a video can now play on the desktop behind the icons — together with the resilience
work that lets a playing video survive an Explorer restart.

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

**Browse and library**

- Browse filters for orientation (landscape/portrait/square) and minimum resolution
  (Full HD/2K/4K); results are filtered as they arrive, so paging and caching stay intact
- Editable tags on the details page and a tag filter in the library; tags live in the
  SQLite catalog and survive restarts
- Category filter (general/anime/people) for sources that classify their wallpapers,
  starting with Wallhaven
- Duplicate detection by content hash: importing or downloading an image whose bytes are
  already in the catalog points at the existing entry instead of storing it twice
- The About card checks GitHub for the newest release and links to it when a newer
  version exists

**Downloads**

- A shared download queue with bounded concurrency replaces per-page downloads: transfers
  keep running while you navigate, keep their progress, retry automatically with growing
  backoff and can be retried by hand
- A Downloads page lists active, queued, failed and finished transfers; a badge on the
  navigation entry shows how many are active

**Dynamic wallpaper**

- `IVideoWallpaperService` plus the `Muralis.DesktopHost` module: a Win32 window is
  parented to the shell's wallpaper worker, drawn into a DXGI swap chain and fed by a
  media player in frame-server mode — the video loops behind the icons, and the static
  wallpaper underneath is never modified
- Dynamic wallpaper page: choose a clip, play it on the desktop, remove it again, mute it,
  and decide whether it starts with Muralis
- A video that was left switched on comes back automatically on the next launch

**Reliability**

- Single instance: a second launch wakes the running window (shown from the tray,
  restored if minimized, moved to the front) and exits before creating a second tray icon,
  rotation timer, database writer or desktop host
- Explorer restart recovery: a hidden shell watcher window listens for `TaskbarCreated`;
  the tray icon is re-registered and a playing video wallpaper is re-mounted on the
  rebuilt desktop layer and resumes playback — the static wallpaper stays untouched
  throughout

**Performance**

- The startup path no longer waits for SQLite: the catalog loads after the window is
  visible and pages refresh through the library's change event
- Thumbnails download four at a time instead of one after another (Home's first eight
  images from an empty cache: 4.7 s → 3.0 s)
- Leaving a page cancels its provider calls and thumbnail warm-up
- Decoded bitmaps are cached least-recently-used with a hard limit, so browsing a large
  grid no longer grows memory without bound (262 MB → 251 MB after 105 items)
- Decode sizes now match their target: 512 px for cards, 1024 px for the Home hero and
  the details preview, never the full-resolution file
- Startup phases, page construction and image loading are timed in the log, and
  `tools/perf-measure.ps1` reproduces the numbers (see
  [docs/performance.md](docs/performance.md))

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

[Unreleased]: https://github.com/EasonMWS/Muralis/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/EasonMWS/Muralis/releases/tag/v0.2.0
[0.1.0]: https://github.com/EasonMWS/Muralis/releases/tag/v0.1.0
