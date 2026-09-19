---
name: muralis-architecture
description: Use when a change touches the product-level boundary between Windows/Explorer and Muralis - adding a desktop feature, deciding who owns desktop content, choosing which project a type belongs in, adding or changing a Desktop Experience mode, or touching the Dock, Desktop Shelf, Clean Desktop or the retired takeover/canvas paths. Also use before introducing any new top-level service, window or cross-layer abstraction.
---

# Muralis architecture

The one-line principle, which every rule below follows from:

> **Windows manages content. Muralis manages presentation and experience.**

Explorer keeps the filesystem, the shell, file associations and the desktop's content. Muralis
owns how that is presented: wallpaper experience, Dock, Desktop Shelf, widgets, themes, motion.

## When to use

- A feature would read, move, own or replace desktop content or desktop icons.
- A new type needs a home: `Muralis.Core`, `Muralis.Desktop` or `Muralis.App`.
- A new Desktop Experience mode, window, or process-wide service is proposed.
- Work touches Dock, Desktop Shelf, Clean Desktop, or anything under the retired Phase 3 paths.

## Core rules

**Two product modes, and the enum is the enforcement.**
`DesktopExperienceMode` has exactly `Native` and `Muralis`. `CleanDesktop` and the experimental
full takeover are *not* members. The JSON converter still *reads* the old names so an existing
settings file is not stranded, and maps every unknown or null value to `Native`. A former mode
therefore degrades to the safe native desktop rather than to a broken state.

**Retired paths may exist, but nothing new may depend on them.**
Kept for startup recovery and internal diagnostics only, and **not to be referenced by new code**:
`IDesktopModeService`, `DesktopModeService`, `DesktopTakeoverService`, `IDesktopCanvasService`,
`FullTakeover`, `DesktopCanvas`, and the `WorkerW` takeover. A bug in a current surface is never
fixed by reaching back into them. There is an architecture test that fails the build if the Dock's
pointer code so much as names them.

Retirement is marked with **XML doc comments, deliberately not `[Obsolete]`** — the project does not
want attribute-generated build noise. Do not add `[Obsolete]` to these, and do not delete them to
"clean up": their presence is asserted by a test, because a desktop that owes Windows its icons back
still needs the give-back path.

**The Dock is not a mode feature.** It is an independent experience layer available in Native too.
The two modes differ over *whether Explorer's icons are shown and who presents desktop content* —
not over whether a dock exists. So a disabled/greyed Dock switch in Native, or un-hiding the Dock
there, is not a bug to "fix".

**Layering is enforced by tests, not by convention.**

| Layer | May reference | Must never reference |
| --- | --- | --- |
| `Muralis.Core` | nothing platform-specific | `Muralis.Desktop`, `Microsoft.UI.Xaml`, `Microsoft.Windows.SDK.NET`, `WinRT.Runtime` |
| `Muralis.Desktop` | `Muralis.Core` | `Microsoft.UI.*`, `Microsoft.WindowsAppSDK.*`, `Muralis.App` |
| `Muralis.App` | both | the desktop layer (`WorkerW`, `SHELLDLL_DefView`, `Win32SurfaceHost`, `SetParent(`) |

Put a rule where it can be tested: pure logic and contracts in Core, Windows interop in Desktop,
XAML and view models in App.

**The Dock is a desktop-experience layer, not a global HUD.**
It is `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` and `IsAlwaysOnTop = false`. A normal window is
*expected* to be able to cover it. It never takes focus and never appears in Alt+Tab or the
taskbar. Making it topmost to win a z-order fight is a regression, not a fix.

**The Desktop Shelf presents real desktop content.**
It is fed from the shell (`DesktopShelfService`, which uses a `FileSystemWatcher` with a debounce
delay) — it is not a virtual file database and not a second desktop. Explorer stays alive and
keeps its files; Muralis never moves, deletes or renames them, and never kills or restarts
Explorer as a product strategy.

**Fail open toward the Windows desktop.**
An unreadable settings value, an unavailable shell capability, or a failed surface mount resolves
to the native desktop, not to a half-applied mode. Defaults are the safe value: a fresh install
leaves Windows untouched. Show the Dock before hiding anything, never after.

Fail-open has a precise shape here, and it is worth copying:

- **Unknown input degrades, it does not throw** — an unrecognised mode value becomes `Native`.
- **Order is the safety mechanism** — mount the replacement and *verify it is really there* before
  taking the icons away. On a desktop with no replacement, hiding icons is taking, not replacing.
- **Record reality, not intent** — persist what actually happened (icons hidden vs not), write the
  record *before* the risky step and clear it *after* verification, so the record errs toward
  "possibly unnecessary" and never toward "silently lost".
- **Restore what the user had**, not a default: if the user's icons were already hidden, giving
  back must not turn them on.
- **A failed upgrade never destroys the original** — migrate by read/write/keep-a-backup, and on
  failure keep the old file and fall back to defaults. A newer-than-known schema is refused
  explicitly rather than guessed at.

**One owner per native mechanism.** Every native capability has exactly one owning component — one
hidden window, one window class, one device class, one desktop-layer lookup. Duplicate ownership is
an architecture error even when it appears to work, and it is the specific failure this codebase
has the most guards against.

**Single process, and the UI thread never touches native.** The desktop layer lives in the same
process and on its own thread; a blocked or crashing native path must not be able to stall the
window. Startup recovery is fire-and-forget with the waiting done in the background.

**Runtime state must be deletable.** Anything stored as runtime state must be reconstructible:
deleting the state directory may not lose user configuration, scene bindings, or local file
bindings. If losing it would matter, it belongs in settings/catalog instead.

## Forbidden patterns

- Adding a member to `DesktopExperienceMode`, or making a retired mode selectable again.
- Referencing `DesktopCanvas`, `FullTakeover`, `WorkerW`, `IDesktopShell` or `DesktopPointerRouter`
  from Dock or motion code.
- Fixing a current-surface bug by reintroducing a takeover, a canvas desktop, or icon hiding.
- Making the Dock permanently topmost, focusable, or activatable.
- Hiding desktop icons before the replacement surface is confirmed up.
- Moving, deleting or renaming a user's files; writing the desktop-icon registry value; restarting
  Explorer; reading or writing another process's memory.
- Parsing the settings file, JSON, or the filesystem from view code — persistence belongs behind a
  Core service interface.
- Starting a process from the UI layer; launching goes through the launcher abstraction.

## Relevant architecture / files

- `src/Muralis.Core/Models/DesktopExperienceSettings.cs` — the mode enum and its fail-open converter.
- `src/Muralis.Core/Services/DesktopExperienceService.cs` — the only place modes change.
- `src/Muralis.Core/Services/DockExperienceService.cs`, `src/Muralis.Core/Abstractions/IDesktopDockHost.cs`
  — the only owner of the Dock window.
- `src/Muralis.Core/Abstractions/ICleanDesktopPresentation.cs` — the no-ownership-of-files contract.
- `src/Muralis.Desktop/CleanDesktop/CleanDesktopPresentation.cs` — Dock-then-hide ordering.
- `src/Muralis.Desktop/Shelf/DesktopShelfService.cs` — the Shelf's real-content source.
- `src/Muralis.App/UI/Dock/DesktopDockHost.cs` — the Dock window's style and presenter policy.
- Architecture tests (these are the executable source of truth; read them before arguing with them):
  `tests/Muralis.Desktop.Tests/Architecture/ArchitectureGuardTests.cs`,
  `tests/Muralis.Core.Tests/Architecture/Phase4AArchitectureTests.cs`,
  `Phase4CArchitectureTests.cs`, `DockFoundationCorrectnessTests.cs`,
  `DesktopExperienceConsolidationTests.cs`.
- `docs/architecture-v2.md`.

## Required verification

- `./tools/phase4d-verify.ps1` — every existing test must pass with 0 warnings (see
  `muralis-build-verification`).
- If you added a new architectural boundary, add the guard test that fails when it is broken. A rule
  with no test is a comment.
- If a change crosses a layer, state which test proves it did not.

## Stop / escalation conditions

- A feature cannot be built without owning desktop content → stop; this is a product-boundary
  question, not an implementation detail.
- A change needs a third `DesktopExperienceMode` → stop and treat it as architecture-level work.
- Two components now want the same window or device class → stop and fix ownership before writing
  feature code.
- You find yourself adding a reference from App to the desktop layer, or from Desktop to XAML →
  stop; the type is in the wrong project or the feature is in the wrong layer.
- A documented rule and the code disagree → the code and its architecture tests win. Report the
  discrepancy rather than "fixing" the test.
