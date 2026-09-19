---
name: muralis-performance
description: Use when a change is on a hot path or could affect startup, memory, CPU, GPU or frame pacing - pointer tracking, scrolling, drag, wallpaper or video rendering, icon extraction, image loading, page navigation, or any cache. Also use before optimising anything, when asked for performance numbers, or when tempted to add a polling loop or timer.
---

# Muralis performance

## When to use

- Anything that runs per pointer report, per frame, or per scroll: motion, drag, scrolling, hover.
- Startup, navigation, image decode/loading, icon extraction, caching, or background work.
- Wallpaper and video rendering.
- Before optimising anything, and whenever asked for a performance number.

## Core rules

**Measure before optimising, and do not optimise from a theory.**
Hypotheses in this codebase have been wrong in both directions — a suspected-expensive per-event
allocation measured negligible, and a suspected icon re-extraction turned out to be exactly zero.
Get a number, then decide. State which number justified the change.

**Percentiles, not averages.** Report median, p95, p99 and max, plus a drop count. An average hides
the stalls that a person actually feels. Also state the sample size and whether the run was
representative.

**Do not assume a frame rate, and do not lock to one.**
There is no stated frame-rate target for this project, and the only documented pacing anti-pattern
is a *resident 60 fps polling loop* — not "must hit 60 fps". Displays and mice vary widely; a design
that samples on a timer rather than on real input reports will be wrong on most of them. Drive from
events, and if you must gate, gate on elapsed time rather than assuming a period.

**Hot paths stay allocation-light and side-effect free.** Per pointer report or per frame:
no LINQ, no synchronous file I/O, no settings reads, no shell calls, no icon extraction, no
serialization, no logging of every event, no theme reapplication, no layout pass, no view-model
rebuild, no measurements or visual-tree walks.

**Never animate layout.** Transform and opacity are compositor work; `Width`/`Height`/`Margin`/
`Padding` force a layout pass per frame. This is the difference between a smooth surface and a
stuttering one, and it is guarded in this repo.

**Keep the shell and disk off the interactive path.** Shell calls are serialised onto their own
thread and the UI never waits on them. Disk is asked about at defined moments — mount, press,
drop — not every frame, and never by polling the filesystem.

**Timers are a smell.** The steady state should have essentially no periodic work: a dock that is
not being pointed at should cost nothing, and a page that is not visible should have cancelled its
work. Prefer one-shot timers and state machines over polling.

**Bound every cache.** Unbounded caches are how a long session becomes a memory problem. Decode to
the size you will actually draw, cap the cache, and evict oldest-first. Known bounds to respect
rather than reinvent: a small LRU for decoded bitmaps, a disk cap for thumbnails with oldest-first
pruning, a short retention for logs, and a per-screen cap for widget instances.

**Cancel work whose result nobody will see.** Page-scoped cancellation on navigation is the
established pattern; leaving a page must stop its provider calls and image warm-up.

**A motion token is not a benchmark dial.** Changing a duration or scale to make a number look
better trades feel for a reading, and the values are design decisions.

**Respect the measurement floor.** Process and thread CPU counters tick at roughly 15.6 ms; any
segment smaller than one tick reads as 0 or one tick. Report such numbers as "at most N ticks", not
as precise values, and prefer wall-clock or allocation counters for very small work.

**Profile opt-in, never resident.** The runtime profiler is enabled by an environment variable and
is off by default, because the paths it measures are the ones that must stay clean. When it is off
its cost is a single bool read per call site.

**Do not trade correctness for a number.** Not UI correctness, image quality, stability, error
handling or data integrity. An optimisation that only drops work nobody will see is acceptable; one
that changes what the user gets is not.

## Forbidden patterns

- A resident polling loop or a fixed-rate ticker driving a visual.
- `GetCursorPos`-style sampling on a timer when an event-driven source exists.
- Animating `Width`, `Height`, `Margin` or `Padding`.
- LINQ, allocation, formatting, string building, or reflection per pointer report or per frame.
- Synchronous file or shell I/O on the UI thread; blocking the UI on the shell thread.
- Persisting or re-theming from a hot path.
- Re-extracting shell icons when nothing changed; extracting an icon on a hover or scroll path.
- Decoding full-resolution images for a thumbnail or a card.
- An unbounded cache, or one with no eviction policy.
- Logging every event on a high-frequency path.
- Adding a scheduler, a thread pool, or a queue for a problem that has not been measured.
- Reporting only an average, or reporting a number from a harness that was visibly misbehaving.
- Tuning a motion duration or scale to improve a reading.

## Relevant architecture / files

- `src/Muralis.Core/Diagnostics/DropProfile.cs` — the opt-in runtime profiler (environment-gated)
  and where percentiles come from.
- `src/Muralis.App/UI/Motion/InteractionMotion.cs` — shared animation helpers; transform/opacity only.
- `src/Muralis.Core/Motion/DockMotionEngine.cs` — the allocation-free hot-path maths.
- `src/Muralis.App/UI/Dock/DockMotionCoordinator.cs` — the pointer path and its queue.
- `src/Muralis.Core/Services/ImageCacheService.cs`, `MetadataCache.cs` — the bounded caches.
- `src/Muralis.Desktop/Icons/IconBitmapCache.cs` — the icon cache and its single serialising thread.
- `tools/perf-measure.ps1` — startup, memory, idle CPU/GPU measurement; writes `artifacts/perf/`.
- `docs/performance.md` — the measured baseline, the method, and how to re-measure.
- Hot-path guards: `tests/Muralis.Desktop.Tests/Architecture/ArchitectureGuardTests.cs`
  (`ActiveCanvasDragHotPathIsCompositionOnly`),
  `tests/Muralis.Core.Tests/Architecture/Phase4CArchitectureTests.cs`
  (`TheReorderHotPathStaysDirectManipulationOnly`),
  `tests/Muralis.Core.Tests/Architecture/DockFoundationCorrectnessTests.cs`
  (`ScrollHotPath_DoesNotRethemePersistOrExtractIcons`).

## Required verification

1. `./tools/phase4d-verify.ps1` — all tests, 0 warnings (`muralis-build-verification`).
2. **Before and after numbers from the same conditions.** Measurements here vary with OS file-cache
   state, so compare runs taken the same way, and say what those conditions were.
3. Percentiles plus drops, not an average, and the sample size.
4. For a hot path: confirm the guards for that path still pass, and add a guard if you introduced a
   new invariant worth protecting.
5. For a claim about feel or smoothness, runtime numbers are necessary but not sufficient — say
   plainly that hand-feel acceptance is pending (`muralis-verification`).
6. Check the idle case too: a change that is fast under load but leaves a timer spinning is a
   regression.

## Stop / escalation conditions

- You cannot measure the thing you are about to optimise → stop and find a way to measure it first,
  or report that the change is unjustified.
- The fix is a scheduler, a thread, or a queue → stop; a measured bottleneck is the only thing that
  justifies that complexity.
- Numbers contradict each other across runs → stop and find out why. Do not report the run that
  agreed with your expectation.
- An optimisation changes what the user sees or gets → stop; it is out of bounds.
- The only way to hit a number is to change a design token → stop; that is a design decision.
- The measurement tool itself stalls the thing being measured → stop; a harness that perturbs the
  system cannot report on it.
- You are about to claim smoothness on a mixed-DPI or high-refresh setup that has not been verified
  in this repo → stop and scope the claim to what was actually measured.
