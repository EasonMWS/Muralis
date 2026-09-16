# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

The desktop canvas gains its edge dock: a rail on any of the four display edges that holds
the items you keep closest, magnifies whatever the pointer passes over, slides out of its
edge when you come to it, and takes items from the canvas and gives them back — while the
native desktop keeps every one of its own icons, clicks and drags.

The canvas also stopped drawing placeholders earlier in this cycle: its items stand for real
programs, shortcuts, files, folders and web addresses — imported by hand, drawn with the
icons the shell shows, opened by double-clicking them, and marked rather than thrown away
when what they point at goes missing.

### Added

**The dock**

- A dock of its own, not a tag on the canvas: which edge it hugs, whether it retracts, how
  it magnifies and what it holds are its own settings, and the canvas is simply every item
  the dock does not name. The layout document is version 3 and carries the dock as its own
  section; a version 2 document is read once and brought forward
- A rail on any edge — left, right, top or bottom — with one set of geometry behind all
  four: the same rail, the same run of items, the same trigger strip, turned to face the
  edge it is on. Moving the dock to another edge is a setting, not another code path
- Magnification the way a dock should feel: the item under the pointer is the largest, its
  neighbours grow less the further away they are, every item is placed so nothing overlaps
  however large it gets, and the whole run stays centred instead of sliding away as it grows
- Auto-hide with explicit states — hidden, revealing, visible, hiding and dragging — and the
  two delays that keep it from reacting to a pointer that is only passing by. A rail that is
  dragged something onto stays out, and the rail never blinks away from under the pointer
- The rail leaves a sliver of itself showing when it is away; the strip along the edge that
  summons it is four DIP wide, narrower than the first column of your own icons
- Dock items open on a single click — a rail is a launcher — while free canvas items keep
  their double-click, and a press that travels becomes a drag, so a drag never opens anything
- Dragging an item from the canvas onto the dock adds it; dragging it along the rail
  reorders it with the neighbours making room as you go; dragging it out puts it back on the
  canvas where you let go. The document changes only on the drop
- Settings for the dock in the Dynamic Wallpaper page: on or off, which edge, and whether it
  retracts. These live in the desktop layout document, not in `settings.json`

**Single instance**

- Launching Muralis right after closing it no longer does nothing: the single-instance name
  is a real lock that is released as the process exits, and a launch that finds the name
  taken asks whether that instance can still show itself before giving up instead of
  assuming it is alive

**Desktop canvas items**

- A formal desktop item: a name, an icon, a position and one typed target — application,
  shortcut, file, folder or web address — where each kind is its own thing rather than a
  bare path, and each item keeps its own identity even if its target moves or disappears
- An import entry in Settings: pick a program or a shortcut, pick a folder, or type an
  address and it lands on the canvas with the name and icon the shell shows. Nothing is
  scanned, nothing is copied — an item only ever remembers where something already is
- One launcher behind every open: programs start, documents open in their associated
  program, folders open in Explorer and addresses open in the default browser — all through
  the shell, and it is the only place in the app that starts a process
- Clicking picks an item out, double-clicking opens it, and dragging moves it anywhere on
  the canvas and saves the position; the double-click and drag thresholds are the ones your
  mouse settings define, so dragging can never open something by accident
- An item whose target was moved or deleted stays put, dimmed with a warning badge, and
  returns to normal when the target is back; removing an item never touches the file

**Icons**

- Real icons read from the shell for programs, shortcuts and folders (the shell's own
  extraction, in the platform layer), resolved once and cached on a worker thread with a
  memory budget — magnifying an item never re-reads the file, and icons stay sharp at
  hover magnification

### Changed

- The desktop layout document is version 3; a version 2 file is read once, brought forward
  and kept beside the new one as `.v2.bak`
- Dock items no longer sit in the item list with a placement tag: where an item lives is
  said by the dock alone, so nothing can disagree about it
- The dock's own spring and its magnification settings now live with the rest of its
  parameters rather than under the canvas' motion and proximity options
- The canvas previously saved to `%LOCALAPPDATA%\Muralis\desktop\layout.json` (document
  version 2) instead of the prototype file; the old document was read once — the canvas
  settings carried over, the placeholder tiles did not — and was kept beside the new one as
  a `.v1.bak` backup

### Fixed

- Closing Muralis and reopening it within a second could leave the icon apparently doing
  nothing, because the name the second launch found was still held by the process that had
  just exited

### Removed

- The Steam / Chrome / Blender / ComfyUI placeholder tiles: the canvas starts empty and
  shows only what has been pointed at something real

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
