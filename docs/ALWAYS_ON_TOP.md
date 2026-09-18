# Always on Top - agreed design

Status: phase 1 implemented, with fixes in 0.2.0-alpha.4; phase 2 remains planned.
Decision recorded: 2026-09-18.

## Phase 1: interaction

- Add a split quick-action tile named "Always on Top" ("Siempre encima" in Spanish).
- The main button pins or unpins the last eligible external window used before
  opening Llampec. Remember foreground changes, excluding Llampec and shell
  surfaces, so opening the notification area does not lose the intended target.
- Identify the target and the operation in the tile subtitle or tooltip. Keep
  that target stable while the panel is being used.
- A configurable global shortcut pins or unpins the foreground window without
  opening the panel. The default is Ctrl+Alt+T. Changing it reserves the new
  combination before releasing the old one; conflicts or failed settings saves
  retain the working shortcut. An empty value disables the shortcut.
- Support multiple pinned windows. The tile state refers to its target; show a
  separate pinned-window count rather than implying that a click unpins all.
- The arrow opens a window selector with pinned windows first, individual
  pin/unpin controls, and an explicit "Unpin all" action for Llampec's pins.
- When the main tile has no eligible target, offer the selector. The shortcut
  resolves the active application's root/owned dialog, or the captured target
  when Llampec has focus. A successful shortcut does not open the panel; a failed
  operation opens its error details.
- Keep pinning state outside disposable panel controls. Restore the changes
  owned by Llampec on normal exit; do not restore pinned windows across sessions.

## Phase 1: Appearance

The feature subpage must include an **Appearance** section (**Apariencia** in
Spanish) from the first implementation. The user explicitly requested:

- A toggle to show or hide the border around pinned windows.
- A control to adjust border thickness.

Disable the thickness control while the border is off, retaining its chosen
value. Use the Windows accent color initially. Save the appearance preferences
and apply changes to windows already pinned by Llampec. Thickness ranges from
1 to 8 logical pixels (default 3), scaled to the target monitor. High contrast
uses the system highlight color.

## Implementation and lifetime

`AlwaysOnTopService` owns the foreground target, current pins and border lifetimes.
It is separate from `AlwaysOnTopView`, which is disposed when the panel closes.
The `AlwaysOnTopAction` tile reflects its target; the separate count reflects all
session pins. The selector keeps existing row controls and coalesces change events.

The native backend observes foreground and window lifecycle events. Every owned
pin has a process/thread identity and a Llampec-specific native ownership marker;
unpinning checks both. Windows already topmost before a request are not claimed.
Closing a target removes its record. A failed pin or unpin is reported in the UI.
Applications running with greater privileges may reject changes. Access denied
is reported with an explicit **Restart as administrator** button; only a user
click invokes the standard UAC prompt. Cancellation keeps the current instance.
An approved restart lets normal cleanup finish before the replacement acquires
the single-instance mutex. Activate the target and retry the shortcut afterwards.
Window groups that already contain visible topmost floating palettes are declined,
because Windows can propagate topmost changes between an owner and its windows.

`NativeWindowBorder` draws a hollow, nonactivating, mouse-transparent native
window. Event hooks update its geometry when the target moves, resizes, minimizes
or changes desktop visibility. It uses DWM visible frame bounds and scales the
border per monitor. Both the inner and outer contour follow the standard Windows
11 corner policy (8 DIP normal, 4 DIP small). For floating windows, the cutout
overlaps the native frame by `DWMWA_VISIBLE_FRAME_BORDER_THICKNESS`, already in
physical pixels; the outer contour and corner radius move inward by that amount
without changing the selected stroke width. This covers the native frame strip
that otherwise looks like a gap. An unavailable or zero thickness adds no inset.
Maximized/snapped windows use a square inner perimeter. DWM exposes requested
policy, not a precise rendered
radius; custom regions/layered shapes use a conservative square fallback. No polling
timer runs while idle. Border creation failures leave the pin usable and report
the missing outline. Normal application exit attempts to restore owned windows;
forced termination cannot run cleanup.

## Verification

The regular core test suite covers target capture, shell transitions, multiple
pins, ownership, recycled handles, failures, settings and shortcut transactions.
Run the opt-in native [smoke test](../tests/Llampec.AlwaysOnTop.Smoke/README.md)
for real Win32 pin/border checks using only its own synthetic windows. It does
not exercise the WinUI controls or replace a visual inspection of the panel.

## Phase 2: further appearance options

Defer window transparency and further visual customization, such as custom border
colors and border opacity, to a second phase. Extend the existing Appearance
section when adding them. These options are recorded as future work, not as part
of the initial implementation. Transparency must preserve and restore each
window's original state when unpinned or on normal application exit.

## PowerToys attribution

Use [PowerToys Always on Top](https://github.com/microsoft/PowerToys/tree/main/src/modules/alwaysontop)
as a reference and adapt useful parts to Llampec's existing architecture. Credit
Microsoft PowerToys in acknowledgements. Retain the applicable copyright and
[MIT license](https://github.com/microsoft/PowerToys/blob/main/LICENSE) notices
for any reused or adapted code.
