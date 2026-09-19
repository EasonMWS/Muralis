# Layered Dock Renderer — product migration plan

This is the seam assessment only. **Nothing in `src/` has been changed**, and the product Dock is still
`DockHost.xaml` presented by WinUI 3. The work behind this document is the prototype in `tools/clear-dock-poc/`,
which proves that a native layered window can present the dock with genuine per-pixel transparency.

## What is already renderer-independent

These were reused **unchanged** by the prototype, which is the evidence that they carry no XAML dependency:

| Concern | Type | Evidence |
| --- | --- | --- |
| Magnification maths | `Muralis.Core.Motion.DockMotionEngine` | driven directly by the prototype; `tools/nexus-proof.ps1` checks its invariants over the whole dock span |
| Motion tuning | `Muralis.Core.Motion.DockMotionProfile` | read by the prototype for `MaxScale`/`MaximumLift`/`InfluenceRadius`; no number is invented at a call site |
| Reorder maths | `Muralis.Core.Dock.DockReorder` | `TargetIndex` and `PreviewCentres` driven by the prototype's drag; the preview the product shows is the preview the prototype drew |
| Pointer source | `Muralis.Desktop.Input.RawPointerBroker` | consumed by the prototype as a subscriber; the prototype registers no device class |
| Icon extraction | `Muralis.Desktop.Icons.ShellIconProvider` / `ShellIconReader` | used directly; reads at the largest size the icon can be drawn at |
| Stripe geometry | `Muralis.Core.Motion.DockStripeGeometry` | the prototype's `DockMetrics` reproduces its numbers; the geometry itself is what a migration should call |

The clean line is exactly where the architecture skill puts it: **Core owns the arithmetic, Desktop owns Windows,
and neither knows what draws the pixels.**

## What is still tied to XAML

| Concern | Where | Why it is not reusable as-is |
| --- | --- | --- |
| Transform channels | `DockIcon.xaml` (`MotionHost`, `NexusMotionHost`, `InteractionHost`) | three XAML elements exist to give `Scale`/`Translation`/`CenterPoint` and `RenderTransform` separate owners. A native renderer has no such channels — it composes offsets arithmetically — so this split has no counterpart and must not be copied mechanically |
| Composition write | `UI/Motion/DockIconMotion.cs` | writes `Scale`, `Translation`, `CenterPoint` on a `FrameworkElement`. The equivalent for a native renderer is adding offsets to a draw position |
| Pointer coordination | `UI/Dock/DockMotionCoordinator.cs` | owns the pointer queue, the region tests and the window-bounds requests, and dispatches onto a `DispatcherQueue`. The queue and region logic are renderer-independent in substance but live behind XAML types |
| Window ownership | `UI/Dock/DesktopDockHost.cs`, `DockHost.xaml.cs` | *is* the WinUI window. This is the type a native renderer replaces |
| Hover/press preview | `DockIcon.xaml.cs` | a local scale animation on the interaction layer; renderer-specific |

**Do not port `DockIcon.xaml`'s three-host split.** It exists to satisfy a constraint (`Scale` and
`RenderTransform` are mutually exclusive on one element) that a native renderer does not have. The prototype
instead composes `base + Nexus offset + drag offset` at draw time, which is simpler and cannot double-scale.

## The seam a migration needs

```
Dock domain / data
├─ IPinnedAppService            exists
├─ persistence                  exists
└─ running apps                 future

Dock motion                     Muralis.Core — already renderer-independent
├─ DockMotionEngine             ✓ reused unchanged
├─ DockStripeGeometry           ✓ reusable
└─ DockReorder                  ✓ reused unchanged

Dock input                      Muralis.Desktop — already renderer-independent
└─ RawPointerBroker             ✓ reused unchanged

Dock renderer                   NEW seam
├─ IDockRenderer  (proposed)      present the dock from a frame description
├─ XamlDockRenderer               today's DockHost.xaml, behind the interface
└─ NativeLayeredDockRenderer      the prototype, behind the same interface
```

The one new abstraction is a **frame description**: for each icon, where it is drawn, at what size, and which
target it shows. The engine already produces the first two, the order produces the third, and a renderer only
has to draw it. With that in place the XAML dock and the native dock are two implementations of one contract,
and `DesktopDockHost` chooses between them behind a flag.

## What the migration must add, which the prototype does not have

These are real gaps, and none of them is a reason the approach fails:

1. **Monitor-derived DPI and `WM_DPICHANGED`.** The prototype declares `PerMonitorV2` in its manifest but reads
   the scale once at startup from `GetDeviceCaps(LOGPIXELSX)`, which is the *system* DPI, and does not handle
   `WM_DPICHANGED`. On a mixed-DPI setup the dock must take its scale from the window's monitor and rebuild its
   metrics and its artwork caches when that changes. `Muralis.Core.Motion.DockPointerMath` already holds the
   product's coordinate conversion and should be the single source rather than a second copy.
2. **Drag reorder persistence.** The prototype reorders in memory and deliberately never writes settings. A
   product renderer must commit through the existing `IPinnedAppService`, once per gesture.
3. **Running-app indicators.** Not modelled at all in the prototype.
4. **Accessibility.** A layered window has no UIA tree. The product dock publishes automation properties today;
   a native renderer needs an equivalent, and this is the largest unexamined area.
5. **Multi-monitor placement.** The prototype centres on the primary display.
6. **Buttons on the broker's stream.** The prototype reads the left button with `GetAsyncKeyState` beside the
   broker's positions. A product renderer wants press and release delivered on the same ordered stream as the
   movement, as the product's own raw path already does.

## Recommended order

1. Extract `IDockRenderer` and the frame description behind the existing XAML dock, with no behaviour change.
   The build gate and the existing motion architecture tests are the safety net.
2. Add `NativeLayeredDockRenderer` behind the interface and a feature flag, defaulting to XAML.
3. Close gaps 1, 2 and 6 before the flag is offered to anyone — DPI and persistence are correctness, not polish.
4. Treat accessibility and multi-monitor as their own pieces of work with their own verification.

## Measured basis

Everything above rests on numbers taken on this machine, recorded in `docs/CLEAR-DOCK-ROUTES.md`: 5 icons at
300×106 physical pixels, transparent to the wallpaper and to a magenta backing in both topmost and non-topmost
modes, p95 0.156 ms per frame at 5 icons and 0.276 ms at 10 with zero GC collections, and the product's own
engine driving the wave to exactly `MaxScale` with its invariants checked across the whole dock.
