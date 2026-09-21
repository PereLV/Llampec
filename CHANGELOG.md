# Changelog

## 0.2.0-alpha.6 — 2026-09-21

- Screenshot now uses a dotted selection frame with rounded corners and a plus
  sign. The vector icon follows the button foreground in light, dark and
  high-contrast themes. Screenshot behavior is unchanged.
- Updated documentation and portable ARM64/x64 packages.

## 0.2.0-alpha.5 — 2026-09-21

- Screenshot quick action: waits for the panel to finish hiding, then sends
  Win+Shift+S to open Windows' native capture selector. No capture library,
  background worker or image storage is added.
- Optional Logitech MX settings: per-device button shortcuts, sensor DPI, native
  wheel mode/SmartShift and independent scroll direction. Includes multiple button
  assignments, background operation, device/power recovery and an original-state
  journal for restoration after interrupted sessions. Selected HID++ feature code
  is attributed to Mouser under MIT.
- MX Master 3S over Bluetooth on Windows ARM64: the user confirmed thumb-button
  shortcuts in the application and after a real suspend/resume cycle. Receiver
  connections and other models remain outside the current hardware validation.
- Windows startup follows the current portable location after a successful manual
  launch when startup was already registered. Stale commands no longer appear as
  enabled in Llampec; Windows' separate startup-disable choice is preserved.
- A background launch no longer opens the panel of an already running instance.
- Validation: 244 hardware-safe Core tests and six additional read-only
  action-catalog integration tests passed. ARM64 and x64 builds succeeded;
  physical Logitech checks currently cover MX Master 3S Bluetooth on ARM64.

## 0.2.0-alpha.4 — 2026-09-18

- Always on Top outlines overlap the native Windows frame border instead of
  leaving that strip visible as a gap. Placement uses DWM's physical border
  thickness and adjusts the corner cutout while retaining the chosen stroke width.

## 0.2.0-alpha.3 — 2026-09-18

- Rounded Always on Top borders follow the standard Windows 11 corner policy,
  including small corners; maximized and snapped windows keep a square perimeter.
- Ctrl+Alt+T resolves ordinary owned dialogs and the captured external target when
  Llampec has focus. Hidden topmost tooltips no longer block pinning.
- Permission failures explain why an elevated window cannot be pinned and offer
  an explicit Restart as administrator action through the standard UAC prompt.
- Regression tests cover shortcut targeting, native errors and rounded border masks.

## 0.2.0-alpha.2 — 2026-09-18

- Always on Top: last-active-window tile, configurable global shortcut (Ctrl+Alt+T),
  window selector, multiple pins and explicit Unpin all.
- Appearance section with optional accent-colored border and adjustable thickness;
  event-driven tracking of movement, DPI, minimization and desktop visibility.
- Transactional shortcut changes, per-session pin ownership and normal-exit cleanup.
- Spanish, Catalan/Valencian and English UI, and Microsoft PowerToys acknowledgements.
- Window transparency and further appearance customization remain planned for phase 2.

## 0.2.0-alpha.1 — 2026-09-17

First functional alpha baseline for Windows 11 24H2+, available for ARM64 and x64.

- WinUI 3 panel with desktop acrylic, unified compositor animations, light/dark
  appearance and support for the Windows animation preference.
- Notification-area access, global hotkey, single-instance activation and silent startup.
- HDR controls per display, display power, projection modes and taskbar auto-hide.
- Windows light/dark mode with fixed-hour or offline sunrise/sunset scheduling,
  current state and countdown to the next transition.
- Caffeine sessions with configurable duration and optional display-on requests.
- Spanish, Catalan/Valencian and English; per-user startup and button reordering.
- Local preferences, on-demand location and delayed release of hidden UI resources.
- Core tests, local diagnostics and verified portable packages for both architectures.
