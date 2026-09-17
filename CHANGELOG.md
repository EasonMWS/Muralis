# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Muralis now offers two ways to relate to the desktop and no more. The native Windows desktop is
the one where Explorer keeps drawing its own icons; Muralis Mode is the one where those icons are
hidden and the dock and the Shelf present what is on the desktop instead. They are one choice in
Settings rather than four surfaces that could disagree with each other — a mode picker on the
wallpaper page, a Clean Desktop switch, and two tray commands whose endings nobody could predict.
Windows keeps the files, the folders, the associations and its own behaviour; Muralis keeps the
wallpaper, the dock, the Shelf and the organization over them. **Windows manages the content.
Muralis manages the experience.** Nothing underneath moved to make that true: the takeover, the
recovery marker and the clean-desktop shell are the same code, and every read of a mode fails open
to the native desktop rather than hiding anything.

The dock is now a product surface of its own rather than a corner of the canvas, and its left
zone is a real launcher: pin a program or a shortcut, click it to start it, drag it along the
zone to put it in order, unpin it from its own menu — and it all comes back on the next launch.
The Dock is a small strip of glass that carries the applications you keep closest, the Shelf
of what is on your desktop, and the utilities; it is shown on a fully native desktop and it is
also what Clean Desktop puts in place of Explorer's icons, so it no longer belongs to any one
desktop mode. Everything it draws comes from one set of design tokens, and a pin whose target
has been deleted stays in the dock and says so rather than disappearing.

The desktop canvas gains its edge dock: a rail on any of the four display edges that holds
the items you keep closest, magnifies whatever the pointer passes over, slides out of its
edge when you come to it, and takes items from the canvas and gives them back — while the
native desktop keeps every one of its own icons, clicks and drags.

The canvas also stopped drawing placeholders earlier in this cycle: its items stand for real
programs, shortcuts, files, folders and web addresses — imported by hand, drawn with the
icons the shell shows, opened by double-clicking them, and marked rather than thrown away
when what they point at goes missing.

The desktop itself can now be handed over and given back. A takeover mode in which Explorer
stops drawing its own desktop icons and the canvas becomes the layer you use, with the three
modes — native, preview, takeover — as an explicit choice the page asks for and the document
remembers; a give-back that puts the icons back the way they were found rather than switching
them on; a marker on disk so that a crash leaves a desktop that the next launch pays back; an
emergency restore in the tray that works whatever else is wrong; and a rule that holds
everywhere in it: **Muralis does not own the user's desktop files.** It reads them, points at
them, and never copies, moves, renames or deletes one.

### Added

**The dock's pinned applications**

- Applications pinned to the dock: choose a program or a shortcut with the file picker, or drop
  one onto the zone, and it is named the way the shell names it and drawn with the icon the shell
  gives it. A single click starts it — through the shell, so per-user associations, elevation
  prompts and a shortcut's own arguments and start-in folder behave exactly as they do from
  Explorer
- A pinned application is the program rather than the file that names it: a shortcut and the
  program it points at are recognised as the same application, so the same thing is never pinned
  twice. A repeat is refused and the pin you already have is highlighted. A pin whose `Arguments`
  or start-in folder went missing still starts, the way it would start on its own
- The zone holds at most twelve applications and says so: the add slot dims once it is full, and
  the picker is never opened for a pin that cannot be taken. What a pin is can be edited by hand
  in `settings.json`; an entry that cannot describe an application is left out with a warning
  instead of failing the whole settings load, and one that is merely incomplete is completed from
  what it does say
- Reordering by hand: hold a pin and carry it, and it follows the pointer one to one — nothing
  eases while it is being carried, and the order is written once, on release. A line is drawn
  where it would land rather than moving the neighbours out of the way, and the release settles
  the icon into the place it was shown going to with a single ease-out
- A pin's own context menu: Open, Open file location, and Unpin. Unpinning removes the pin and
  nothing else — the program or shortcut it pointed at is never touched, renamed or deleted
- A pin whose target was deleted stays in the zone, dimmed, with a tooltip that says what is
  missing and a menu that still offers Unpin; clicking it says so in the log rather than starting
  anything or crashing

**The Muralis design foundation**

- One set of design tokens for the whole application — colour in light and dark, type, spacing,
  radius, elevation and motion durations — with a glass surface and a small set of reference
  controls built on them, and a hidden playground (`Ctrl+Shift+D`) for looking at them. No view
  invents a colour, a radius or a duration of its own

**The desktop experience and the real Shelf**

- The dock as a layer of its own: a borderless, always-on-top strip that is shown on a fully
  native desktop, hidden when you turn it off, and forced back up when Clean Desktop needs it —
  with exactly one thing allowed to show or hide that window, so a mode change can never hide the
  only thing left on the screen
- A real Shelf: the dock's middle zone is the contents of your desktop and the public desktop,
  read once and kept current by a debounced watcher, drawn with the icons the shell shows.
  Double-clicking an item opens it, and nothing is ever copied, moved or renamed
- Clean Desktop as its own presentation rather than a takeover: Explorer's own reversible
  no-icons flag is asked for through the desktop's shell view (with the icon-list window as a
  fallback), what the desktop looked like before is recorded and put back rather than "turned
  on", and a marker on disk lets the next launch give the icons back after a crash

**The desktop takeover**

- Three desktop modes, asked for on the Dynamic Wallpaper page and remembered in the desktop
  document: native, where nothing is mounted and the desktop is Windows'; preview, where the
  canvas is drawn above a desktop whose own icons are untouched; and takeover, where the
  native icons are hidden and the canvas is the layer you use. The page asks before either
  canvas mode begins, and the mode survives a restart
- A takeover that goes through the shell's own view: Muralis asks the desktop's view for the
  no-icons flag, reads back that the shell answered that way, and records which route worked.
  The registry is never written, the icon list is never rearranged, Explorer is never injected
  into and never restarted; a shell that offers none of the documented routes is refused
  rather than worked around
- A give-back that restores what was found instead of switching the icons on: what the
  desktop's icons looked like before the takeover is recorded, and a user who keeps their
  icons hidden gets them back hidden
- A data model for handover that is not a flag: native, enabling, Muralis, disabling and
  recovery-required, with every move out of a state defined. A takeover that fails before the
  icons were hidden rolls back to native; one that fails after them lands in recovery-required
  instead of pretending nothing happened, and nothing may be taken over while a give-back is
  owed
- A crash-safe marker: a small file that records that the desktop was taken over, when, by
  which run and what the desktop looked like, written atomically before the icons go. The next
  launch finds it, gives the desktop back and only then restores the mode the user asked for —
  no clean exit is required for any of it, and a marker that cannot be read is set aside rather
  than acted on
- An emergency restore that works whatever else is wrong: the page offers it when it knows the
  desktop still needs paying back, and the tray offers it always — including while the desktop
  is Muralis's and the desktop page is not the page on screen
- The tray's own desktop commands: turn the takeover off, and restore the Windows desktop.
  Both end in the same verified give-back, and both are reachable whatever the canvas is doing
- A restarted Explorer is taken into account: the rebuilt desktop is noticed and the takeover
  is applied to it again, without Muralis restarting the shell itself
- The desktop's own content is brought in by reference: the programs, shortcuts, files,
  folders and addresses already on the desktop and the public desktop are read once and shown
  as canvas items that point at where those files already are. Nothing is copied, moved,
  renamed or thrown away, an item turned down is never adopted again, and turning the import
  off leaves every file exactly where it is
- An item adopted from the desktop is tied to the path it came from rather than to the name it
  shows, so the same file is recognised however it is labelled and a file that merely shares its
  name somewhere else is a different thing; a file that is moved or renamed is a new entry, and
  the item pointing at where it used to be says so instead of following it
- A page that says which mode the desktop is in, how many items the desktop holds, how many
  would be adopted and which ones cannot be, before any of it happens

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

**Diagnostics**

- The dock's drop path can be measured stage by stage: with `MURALIS_DOCK_PROFILE=1` the app
  writes what each stage of a drop cost — wall time, processor time on the thread it ran on,
  bytes allocated, which thread it came back on — to `logs/dock-drop-profile.jsonl`. It is off
  unless the variable is set, and switched off it costs a boolean read per instrumented point

### Changed

- `settings.json` is schema version 3: the desktop experience carries the native desktop and
  Muralis Mode and nothing else, and a file written by an older version is read once and brought
  forward. A former mode name is still understood while reading — clean desktop is Muralis Mode,
  the withdrawn takeover and anything unrecognised are the native desktop
- A mode is read with its own converter: the general string-enum converter was claiming the type
  first, so a file that named a former mode failed to load at all and the whole settings file
  silently fell back to its defaults
- The desktop experience offers two options, and the dock's switch sits in the Muralis Mode group
  on the settings page: while Muralis Mode is on the dock is required, so its switch is off and the
  row says so. On the native desktop the switch is the user's to change, because the dock is useful
  there too
- The tray offers one way back to the Windows desktop — "Restore the Windows desktop" — instead of
  a separate turn-off and a separate restore whose names did not say which was which
- Reordering the pinned applications updates only the pins whose own name or warning changed,
  instead of asking every pin in the zone to work out its tooltip again
- `settings.json` is schema version 2: it carries the desktop experience and the dock with its
  pinned applications as sections of their own. Both are optional, so a version 1 file loads with
  the defaults in their place rather than failing
- The dock's window belongs to the dock's own service: Clean Desktop asks for the dock to be up
  instead of showing it, because the dock is a product surface that is also useful on a fully
  native desktop and is not a part of Clean Desktop
- The desktop layout document is version 4: it carries the takeover — mode, whether the
  desktop's own items may be adopted, which ones were turned down — and every item remembers
  the path it came from. A version 3 file is read once and brought forward
- Whether the canvas is up is no longer a setting in `settings.json`: it is part of the
  desktop mode, which lives with the desktop document, so the desktop's state has one home
  instead of two that can disagree
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

- The choice not to adopt the desktop's own items is honoured while the canvas is mounted
  too: it was only read on the path where no canvas was up, so a preview or a takeover
  brought the desktop in anyway
- Closing Muralis and reopening it within a second could leave the icon apparently doing
  nothing, because the name the second launch found was still held by the process that had
  just exited

### Removed

- The "Your desktop" and "Edge dock" sections of the wallpaper page, with the mode picker, its
  state line and the dock's four edge options: the wallpaper page is about the wallpaper, and the
  mode is chosen where the rest of the desktop experience lives. The desktop items card stays —
  the Shelf reads the very same document and that card is where it is edited
- The desktop's third and fourth relationships: clean desktop is Muralis Mode under its old name,
  and the experimental takeover is no longer something the product offers. The frozen layer that
  implemented it stays for the one job it still has — giving a desktop that owes Windows its icons
  back to Windows at startup
- The tray's separate "turn off the desktop" command
- `tools/p3d-takeover-verify.ps1`, the takeover acceptance harness: the path it verified is not a
  product path any more. The shared `tools/p3-common.ps1` and the shell probes stay
- The desktop canvas on/off setting: the desktop mode says which of the three the desktop is
  in, and a document that still carries the old setting is read into the mode it described
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
