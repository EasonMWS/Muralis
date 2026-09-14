# Performance

Muralis is meant to be a low-presence Windows app: fast to show, quiet when idle, and
careful with pictures. This page records the measured baseline and how to re-measure it.

## How startup is structured

```
process start
  → .NET + Windows App SDK runtime loads (self-contained, outside the app's control)
  → App class created          ─┐
  → app resources loaded        │
  → host built (DI)             │  startup path: kept minimal on purpose
  → settings loaded             │  (no network, no database, no image decoding)
  → window created              │
  → window shown to the user   ─┘
  → catalog (SQLite) loaded     ── background, pages refresh when it is ready
  → tray icon, rotation timer   ── after the window is up
  → Home feed + thumbnails      ── background network work, cancellable
```

Every phase above is written to the log (`Startup: <phase> at <ms> ms`, plus
`Page '<key>' built in <ms> ms` for navigation and `Thumbnails warmed: …` for image
loading), so a regression shows up without a profiler.

## Baseline (September 2026, RTX 5070 laptop, 24 logical cores, Release build)

| Metric | Before | After |
| --- | --- | --- |
| Window shown, app-internal clock, warm relaunch | 387–406 ms | **346–351 ms** |
| Window shown, app-internal clock, first launch of a fresh build | 1296 ms (SQLite on the path: 879 ms) | **381 ms** (SQLite moved off the path) |
| Process start → visible window, warm relaunch | 541–572 ms | **479–499 ms** |
| Process start → visible window, fresh publish, cold file cache | – | 3082 ms, of which ~1801 ms is the OS loading the self-contained .NET/WinAppSDK runtime |
| Idle RAM (Home, nothing else) | 176–179 MB | 180–182 MB |
| RAM after browsing a 105-item grid, back on Home | 262 MB | **251 MB** |
| Idle CPU (12 s sample) | 0.00–0.03 % | 0.00–0.03 % |
| Idle GPU (12 s sample) | 0 % | 0 % |
| Home thumbnails from an empty cache (8 images) | 4729 ms | **3010 ms** |
| Catalog (SQLite) load | 206–221 ms, on the startup path | 167–215 ms, after the window |

Notes and honest caveats:

- **The 1.5 s cold-start target is not met on a cold file cache.** ~1.8 s of a fresh
  install's first launch is the operating system paging in the self-contained .NET and
  Windows App SDK DLLs before any Muralis code runs. Relaunches (the normal case) are
  ~0.5 s. Closing that gap would mean either a framework-dependent or MSIX deployment —
  both need machine-wide prerequisites this project deliberately avoids — or risky
  loader tricks; the measured app-internal path is ~0.35 s.
- **Idle RAM is above the 150 MB reference** because of the framework, not the app:
  WinUI 3 + Mica + the self-contained Windows App SDK commit roughly 120 MB before any
  content is shown, and Muralis' own data structures are kilobytes. Browsing a large
  grid adds ~70 MB that is retained by the framework's image/container pipeline; a
  decoder cache bound (24 entries) covers the app's own share of it. Reducing the rest
  would need a profiler-driven deep dive, which was deliberately skipped rather than
  guessing at unsafe changes.
- Measurements vary with the state of the OS file cache; always compare runs taken
  under the same conditions.

## What was optimised

- **SQLite moved off the startup path**: the catalog loads after the window is visible;
  pages refresh through `ILocalLibrary.Changed` when it is ready.
- **Thumbnails download in parallel** (4 at a time, unique temp files, assignments on the
  UI thread) instead of one after another.
- **Leaving a page cancels its background work** (provider calls and thumbnail warm-up)
  through a page-scoped cancellation token.
- **Decoded bitmaps are cached with a hard limit** (24 entries, least-recently-used) so a
  long browsing session cannot pin hundreds of megabytes.
- **Decode sizes match their target**: 512 px for grid cards, 1024 px for the Home hero
  and the details preview — never the full-resolution file.
- **Startup, navigation and image-loading timings are logged**, and
  `tools/perf-measure.ps1` reproduces the whole table above.

## Re-measuring

```powershell
# build first, then:
pwsh tools/perf-measure.ps1 -Label after                 # Release build (default path)
pwsh tools/perf-measure.ps1 -Exe <path\to\Muralis.exe> -Label debug-or-published
```

The harness starts the app several times (cold then warm), records process start → first
visible window, working set/private memory, idle CPU and GPU over a sampling window, and
parses the startup timeline out of `%LOCALAPPDATA%\Muralis\logs`. It stops the app between
runs and writes `artifacts/perf/<label>.json`.
