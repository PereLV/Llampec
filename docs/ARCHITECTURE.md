# Architecture

Llampec is an unpackaged .NET 10 application for Windows 11 24H2+, using WinUI 3
and the Windows App SDK WinUI 2.3 and Foundation 2.3 component packages (not the
metapackage, whose AI, ML, Search and Widgets binaries Llampec never uses). The app is
published ReadyToRun for ARM64 and x64 with Windows App
SDK files included and a separate .NET Runtime requirement.

## Components

- `Llampec.Core`: action contracts, Windows interop, settings, localization and
  schedule calculations, independent of the UI framework.
- `Llampec.App`: WinUI panel, notification-area icon, global hotkey, single-instance
  activation and theme scheduler.
- Always on Top keeps pin ownership and native borders in a UI-thread service,
  independent of the disposable window-selector controls. Foreground and window
  events drive state updates; borders receive their own target-specific events.
- Fullscreen keeps one window's original state in an App-owned UI-thread service.
  Its mouse-edge timer runs only during an active fullscreen session. Native
  taskbar preferences remain unchanged; foreground changes restore the window
  when another app is selected. See [fullscreen behavior](FULLSCREEN.md).
- `Llampec.Core.Tests`: deterministic logic tests and Windows integration tests.
- The optional Logitech mouse service lives in Core and is owned by the `mouse`
  module in `App`, separate from disposable settings controls. A single HID reader
  routes notifications, including battery events; Windows device/power events and
  read failures trigger reconnection. Settings changes use a durable original-state
  record before touching hardware. See [Logitech lifecycle](LOGITECH.md).
- The camera module observes Windows' Media Foundation sensor activity monitor only
  while the panel is visible. The power module reads its state when the panel opens.
  See [camera](CAMERA.md) and [power](POWER.md).

`ModuleCatalog` supplies static id/title/icon/subpage metadata for all fourteen tile
modules, including disabled ones, without constructing services. Preferences retain
order, membership and visibility by stable identifier. `App.Modules` constructs
only enabled actions and owns the dynamic lifetime of their services and shortcuts.
Stateful enabled services such as caffeine and theme scheduling live outside page
controls, so hiding the panel does not stop them. The catalogue comprises HDR,
display power, theme, projection, taskbar, touch taskbar, caffeine, Always on Top,
rotation, screenshot, fullscreen, power, MX mouse and camera. Removing the MX mouse
module first restores the mouse's original settings; if that fails, the module
stays enabled. Then its Logitech service and HID reader are released.

`DisabledModules` means explicit module unloading; old `HiddenTiles` means only
button visibility and does not silently disable a service on migration. Applying
an editor draft is serialized and checks busy actions first. `App.Modules` prepares
removal by restoring session-owned pins/fullscreen windows and retrying pending
rotation recovery. Failed restoration retains the enabled module and recovery
ownership. Settings persistence precedes unloading; a failed save rolls back
panel preferences and releases newly added module instances. Successful removal
unsubscribes module-specific events and releases its shortcuts. Theme removal
drains any pending schedule application; caffeine releases its power request.
Shared `SystemEvents` and tray integration remain available even with no tile
modules enabled. Re-enabling preserves configuration and appends an unassigned
icon, without recreating the previous pin, fullscreen or caffeine session.

`DisplayOrientationService` belongs to `App`, independently of the disposable
`RotationView`. It serializes verified lock/mode changes and applies and saves an
orientation immediately, without a confirmation timer. Only a failed operation
retains a recovery snapshot; recovery checks that a newer external change has not
superseded it. Normal shutdown retries any pending failure recovery while the
dispatcher remains alive; successful orientation choices remain applied.
System display/settings/resume events coalesce refreshes
of rotation and taskbar controls and reposition the panel on its opening monitor.
See [tablet controls](TABLET.md) for contracts and hardware limitations.

One-shot actions pass their operation through `TileViewModel` and
`FlyoutViewModel.RunWithPanelHiddenRequested`.
`FlyoutWindow.RunWithPanelHiddenAsync` awaits `FinishHide`, checks that the native
window remains hidden and has not reopened, then invokes the operation in the same
UI continuation. Reopening or cancelling dismissal prevents the pending action
from being sent. `ScreenshotAction` emits Win+Shift+S through the shared
`Platform.KeyboardShortcut`, leaving capture and image handling to Windows.
There is no screenshot-specific delay or recurring work. See [Screenshot](SCREENSHOT.md).

## Panel and motion

The visible panel is a rounded XAML surface containing `SystemBackdropElement`,
controls, outline and `ThemeShadow`. `TransparentPanelBackdrop` supplies a
transparent, borderless native host. The visible width is 360 DIP when space allows;
placement keeps the surface 12 DIP from the work-area right and bottom edges, with
additional transparent padding for the shadow. The opening monitor remains the
placement anchor, including during layout and DPI changes.

The development layout supports 1–6 columns (default 3). Its main grid uses
120 DIP per column, with a 180 DIP minimum; available monitor width can reduce
the effective column count. Options pages remain 360 DIP wide when space allows.
Optional user categories are built only with the tile controls; stable action ids
retain order, visibility and membership. The main-panel pencil opens a visual
editor with a fixed columns/category header and Save/Cancel footer. It draws each
group as a block with the panel's real button faces and a corner × to disable a
module. Buttons are dragged by their face and categories by their heading, with
a floating item, an empty landing slot and animated reflow; keyboard menus offer
the same moves. Views are reused within the session, and pointer capture starts
after an 8 DIP threshold; destinations come from layout slots, following pointer
movement and viewport scroll updates. Empty groups and No category
remain editor drop destinations; removing a category appends its icons to the end
of No category in visual order. `PanelLayoutDraft` holds independent plain data
until Save, and static catalogue metadata renders inactive module choices. See
[panel layout](PANEL_LAYOUT.md) for persistence and migration contracts.
Panel placement queries existing primary/secondary taskbar geometry and reserves
its expanded band even with auto-hide. Size-change placement requests are coalesced;
these layout features introduce no recurring background work.

`PanelMotion` animates the surface's translation and opacity through Composition.
The host does not move or resize per animation frame. Entry uses a 320 ms eased
slide and a 70 ms fade; exit uses a 220 ms eased slide with a fade over its final
100 ms. Newly built content gets two render submissions before becoming visible.
Reversals start at the presented value and retain the current page. Disabled Windows
animations use the endpoint immediately.

A composition completion batch finishes each transition. A one-shot deadline
handles occluded or suspended rendering. Rendering handlers are used only for entry
preparation and are removed afterward. Queued page measurements wait until entry
finishes; progress indicators do not change tile geometry.

`PanelAcrylicBackdrop` keeps the material input-active through dismissal while the
native controller retains transparency, contrast and power-policy fallbacks.
Keep one backdrop assigned for the XAML connection's lifetime. Idle suspension
releases controllers on the UI thread but retains projected backdrop targets;
releasing those thread-affine links on a finalizer thread can cause a native failure.
The host filters frame-style changes because WinUI can reapply classic frame bits
during activation and DPI changes.

## Hidden lifetime

Closing hides the panel and disposes the current subpage. After two seconds hidden,
the app releases tile controls and suspends acrylic controllers, then requests a
nonblocking collection of discarded objects. A second one-shot delay of 500 ms
returns resident pages to Windows. Reopening cancels pending cleanup and rebuilds
the controls. Cleanup is deferred while an action is busy.

An unfinished panel-editor draft survives hiding as plain data in the panel host;
its disposable controls, drag state and menus do not. Reopening rebuilds the editor
over that same draft. Cancel discards it and Save applies it through `App.Modules`.
Escape first cancels an editor drag, then otherwise follows normal hide behavior.
Editor edge scrolling follows captured-pointer and viewport scroll events, with
no recurring timer or idle polling.

The host, shared tray integration and enabled actions/scheduler remain alive. There is no periodic
memory-cleaning loop. Returning resident pages reduces the working set; it does not
unload the runtimes or release all private memory commitment.

## Diagnostics

`Llampec.exe --diagnostics` enables the local log for that session without changing
the saved logging preference. The log records preparation, composition completion
and hidden cleanup. `--theme=light` or `--theme=dark` previews the selected panel
theme on a fresh launch without changing the stored preference. If Llampec is
already running, another launch reopens that instance; command-line preview and
diagnostic options take effect only when starting a new process.

`Llampec.exe --exit` requests a normal shutdown of the running instance, including
restoration of fullscreen windows, session-owned pins and Logitech settings. It does not start a panel when no instance is
running. `tools/run.ps1` uses this path before rebuilding an active development
instance, and stops if normal shutdown does not complete.

To sample memory and CPU:

```powershell
./tools/measure-memory.ps1 -Seconds 30 -ProcessId <PID> -OutputPath artifacts/memory.csv
```

`Seconds` specifies the sample count, with a one-second pause between samples;
the script reports actual elapsed time. Compare stable states under matching
architecture, display scale and workload. These samples do not measure GPU frame
pacing or establish a cross-device memory guarantee.

The final 2026-10-06 visual-editor ARM64 Debug run, hidden after editing with all
eleven modules enabled, averaged 10.18 MiB private working set across ten samples.
The sample workload, elapsed-time overhead and separate private-commitment value
are recorded in [panel resource review](PANEL_LAYOUT.md#resource-review). This is
one local observation, not a controlled comparison with the earlier settings-page
run or a cross-device guarantee.

Before a release, check tray/hotkey activation, Escape/outside dismissal, both
themes, high contrast, reduced motion, disabled transparency, mixed-DPI placement,
idle reconstruction and the published app on each supported architecture. Validate
HDR and projection changes on appropriate physical hardware.
HDR monitor children are currently inventoried at startup; newly attached displays
require restarting Llampec to appear. Logitech coverage and measured costs are
recorded separately in [its validation notes](LOGITECH.md#validation-on-2026-09-21).

## References

- [Microsoft: element-scoped system materials](https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials)
- [Microsoft: system shadows](https://learn.microsoft.com/en-us/windows/apps/design/layout/depth-shadow)
- [WinUI system-backdrop element specification](https://github.com/microsoft/microsoft-ui-xaml/blob/main/specs/SystemBackdropElement/SystemBackdropElement_Spec.md)
- [PowerToys backdrop lifetime implementation](https://github.com/microsoft/PowerToys/blob/main/src/modules/cmdpal/Microsoft.CmdPal.UI/Controls/TintedBackdrops.cs)
- [Microsoft: EmptyWorkingSet semantics](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset)
