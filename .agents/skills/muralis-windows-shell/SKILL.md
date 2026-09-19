---
name: muralis-windows-shell
description: Use when a change touches Windows itself - Explorer, the desktop layer, shell icons, shortcuts, launching, context menus, HWNDs, window classes, z-order, DPI, monitors, raw input, the tray, single-instance behaviour, or Win32/COM interop. Also use when deciding where a new P/Invoke declaration belongs, or when a shell feature must degrade instead of failing.
---

# Muralis and the Windows shell

## When to use

- Any Win32 / COM / HWND work: new P/Invoke, a new window, a new window class, a new hook point.
- Shell behaviour: desktop icons, `.lnk` shortcuts, shell icons, file associations, launching,
  context menus, Explorer restarts, the wallpaper worker layer.
- Monitors, DPI, z-order, activation policy, the tray, single-instance handling.
- A shell feature that fails and must decide how to behave.

## Core rules

**Explorer stays alive and keeps ownership of content.**
Never kill, restart or inject into Explorer as a product behaviour, and never take ownership of
the user's files. Restarting Explorer is what a *user* does to recover a broken desktop — it is not
how Muralis changes a mode. The presentation contract is explicit that an implementation may only
change the *visibility* of Explorer's desktop icons and mount an alternative presentation; it must
never move, delete, rename or take ownership of desktop files.

**Fail open, and degrade in a defined direction.**
A shell call that cannot be made must leave the desktop working rather than leave Muralis
half-applied. The established ladder is to fall back to the static/native behaviour, log a warning,
and continue — attach paths never throw. `Windows manages content, Muralis manages presentation`
means a broken presentation layer returns the desktop to Windows.

**One owner per native mechanism.** This is the rule the codebase guards hardest:

| Mechanism | Sole owner |
| --- | --- |
| Desktop window create / destroy / mount | the surface host |
| Wallpaper-worker discovery (`WorkerW`, `SHELLDLL_DefView`, `EnumWindows`, `FindWindowEx`) | the desktop worker window interop file |
| Remount / geometry policy after Explorer restarts | the desktop shell |
| Explorer-restart detection (`TaskbarCreated`) | the shell event source |
| Video rendering (`MediaPlayer`) | the video surface content |
| Monitor enumeration | the monitor manager |
| Shell icon extraction | the icon reader + its interop surface |
| Mouse raw input registration | the process-wide pointer broker |
| The Dock window | the Dock experience service |
| The desktop icon list (`SysListView32`) | named in the Win32 interop file only |

Route a new capability to the *domain* that owns it, not to whichever file already has a P/Invoke.

**A window is created through the project's own host, not by raw P/Invoke.**
Desktop-layer windows go through the surface host; interactive windows go through the interactive
host factory so activation and z-order policy stay uniform. Bare `CreateWindowEx` outside those
owners is a guard violation.

**Declare P/Invoke where it is owned.** Interop lives with the layer that owns the mechanism. The
App project must not declare desktop-layer entry points at all — it may not name the desktop layer.

**Windows semantics beat hand-rolled parsing.** Prefer the shell's own answer for shortcuts, icons,
launching and associations over parsing formats yourself. Launching goes through the shell's `open`
verb; do not build command lines or pass arguments, and allow only safe URL schemes.

**Every native resource needs an owner and a cleanup path.**
Any handle, window, registration, hook point, COM object or subscription added must be released on
the corresponding teardown path, and the teardown must be idempotent and non-throwing. Start/stop
cycles must not leak handles, threads or memory — measure it rather than assuming it.

**The Dock is passive.** `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, never always-on-top, no taskbar or
Alt+Tab entry, never takes focus. It is covered by ordinary windows by design.

**Do not assume the display.** Single-monitor behaviour is the verified case; multi-monitor, hot-plug
and mixed-DPI remain unverified in this repository. Write code that is DPI-correct by construction
(scale from the window's own DPI, keep monitor origins non-zero-safe) but do not claim multi-monitor
correctness you have not measured. Treat `IsPrimary`, ordering and device names as non-identity.

## Forbidden patterns

- Ending or restarting Explorer; reading or writing another process's memory; injecting a thread.
- Owning, moving, deleting or renaming the user's desktop files.
- Hiding desktop icons via the registry value, the undocumented system parameter, list-view item
  rearrangement, or any route that cannot be undone from a recorded state.
- Global hooks (`SetWindowsHookEx`, `WH_MOUSE_LL`, `WH_KEYBOARD_LL`) — Muralis never inserts itself
  into other applications' input path.
- `RIDEV_NOLEGACY` / `RIDEV_CAPTUREMOUSE`, and registering a raw input device class outside the
  process broker.
- Click-through hacks (`WS_EX_TRANSPARENT`, `HTTRANSPARENT`) as a way to see the pointer.
- Declaring a desktop-layer P/Invoke (window creation, parenting, monitor lookup, worker discovery)
  inside the UI project.
- A second hidden window, window class, or shell-icon path that duplicates an existing owner.
- Launching a process from the UI layer, or from anywhere except the launcher abstraction.
- `[Obsolete]` attributes to mark retired paths — this project marks them in doc comments to avoid
  build noise.
- Blocking a shell call on the UI thread, or waiting on the shell thread from the UI.

## Relevant architecture / files

- `src/Muralis.Desktop/Surfaces/Win32SurfaceHost.cs` — window creation and mounting.
- `src/Muralis.Desktop/Interop/DesktopWorkerWindow.cs` — the only desktop-layer lookup.
- `src/Muralis.Desktop/Shell/DesktopShell.cs`, `ShellEventSource.cs` — lifetime, remount, restarts.
- `src/Muralis.Desktop/Icons/ShellIconReader.cs` — the only shell-icon reader.
- `src/Muralis.Desktop/Interop/NativeMethods.cs` — the shared declaration surface.
- `src/Muralis.Desktop/Input/RawPointerBroker.cs`, `RawInputRegistry.cs` — the raw-input owner.
- `src/Muralis.App/UI/Dock/DesktopDockHost.cs` — the Dock window's style policy.
- `src/Muralis.Core/Abstractions/ICleanDesktopPresentation.cs` — the no-ownership contract.
- Guards: `tests/Muralis.Desktop.Tests/Architecture/ArchitectureGuardTests.cs`,
  `tests/Muralis.Desktop.Tests/Architecture/Phase4DArchitectureTests.cs`,
  `tests/Muralis.Core.Tests/Architecture/DockFoundationCorrectnessTests.cs`.
- `docs/architecture-v2.md` — the ownership table and interop rules.

## Required verification

1. `./tools/phase4d-verify.ps1` — all tests, 0 warnings (`muralis-build-verification`). The
   architecture guards are xunit facts and run with it; a new interop owner without a guard will
   pass silently, so check whether the rule you relied on is actually asserted.
2. Any new native resource: demonstrate the teardown, and check handle/thread counts across repeated
   start/stop cycles rather than asserting "it is disposed".
3. Any change to what Windows sees (window style, registration flags, hooks, z-order): say
   explicitly what other applications now observe differently, and verify the desktop still
   receives input normally.
4. Prefer harnesses that read only Muralis' own windows and logs over anything that captures the
   user's screen. Restore any state the harness touched, including user settings, and verify the
   restore.

## Stop / escalation conditions

- The fix requires a hook, a driver, or another process's memory → stop. These are refusals, not
  trade-offs.
- A shell feature cannot be made to fail open → stop; treat it as architecture-level work rather
  than shipping a path that can leave the desktop broken.
- You need a new owner for a mechanism that already has one → stop and either extend the owner or
  justify a genuine split; do not add a second one.
- Behaviour is only reproducible on this machine's display configuration, and the change touches
  monitors or DPI → stop and declare single-monitor-only verification instead of generalising it.
- You are about to disable, restart or work around Explorer to make a symptom go away → stop.
- A z-order or activation problem is being solved by making a window topmost → stop; the passive
  policy is deliberate.
