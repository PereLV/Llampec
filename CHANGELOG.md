# Changelog

## 0.2.0-alpha.8 — 2026-10-06

- Simplify the panel editor to look like the finished panel: each group is a block,
  buttons keep their real face and caption, and a small circular × at the top-left
  corner disables a module. Drag a button by its face or a category by its heading,
  without drag handles; the item floats under the pointer while an empty slot and
  animated reflow show where it will land, as in Windows Quick Settings. Escape
  restores the previous position. Keyboard users open a button's options with
  Enter/Space/Shift+F10 and disable it with Delete.
- Reuse editor views during a session and fill shared menus when opened, instead of
  rebuilding the editor and its per-item menus after every change. The panel keeps
  its size during a drag and repositions once on release.
- Dropped editor items glide into their slot, and holding an item at the top or
  bottom edge keeps scrolling. Both timers exist only during a drag.
- Reduce idle CPU: Always on Top and Fullscreen hook window lifecycle events only
  while the panel is shown, a pin exists or a fullscreen session is active, using
  narrow event ranges instead of all object events (which included every cursor
  and caret move). With the panel hidden, Always on Top processes only pinned
  windows' events. With a moving mouse and a window changing title 20 times per
  second, the hidden ARM64 Debug instance went from about 1 s to 0 ms of CPU per
  20 s, and from 0.77 s to 0.17 s while a window is pinned.
- An absent Logitech mouse (missing HID path) backs off to 5-minute retries after
  2/5/15 s, since Windows device notifications report its return; a present but
  silent device keeps 15-second retries.
- Fix panel hit testing: the shadow's Z elevation moved to a non-interactive layer,
  since a Z translation on the interactive surface displaced clickable areas from
  the drawn controls by several DIP across the whole panel.

- Add a visual panel editor from the main-panel pencil, with a fixed column/category
  header and Save/Cancel footer. Choose 1–6 columns (default 3), create or rename
  categories, and move icons within/between groups or reorder categories through
  drag handles and keyboard menus. Empty groups remain drop destinations; deleting
  a category appends its icons to the end of No category in visual order.
- Disable and re-enable tile modules through the editor, using a static metadata
  catalogue for inactive choices. Explicit DisabledModules releases actions,
  services and shortcuts on Save; legacy hidden buttons retain their services.
  Keep module configuration and add enabled icons at the end of No category,
  without recreating previous pin, fullscreen or caffeine sessions.
- Prepare restoration before module unloading; failed recovery keeps the module
  enabled and retains ownership. Drain pending theme work, release caffeine power
  requests, and keep shared tray/events and the independent Logitech service alive.
- Reserve room for expanded primary/secondary taskbars when positioning the panel,
  including conventional auto-hide and collapsed touch taskbars.
- Retain only unfinished editor draft data when hidden and rebuild disposable
  controls on reopening. Cancel discards the draft; Escape cancels an active drag
  or hides the panel. Use local pointer capture on drag handles with an 8 DIP
  threshold, geometric destinations and event-driven edge scrolling in one bounded
  viewport, without a system drag payload or new idle timer.
- Use validated task-button geometry when Explorer's tray host includes transparent
  padding, removing the excess panel-to-taskbar gap. Missing or invalid shell
  geometry keeps the conservative placement fallback.
- Credit Pere Esquerdo Ramis in copyright, author metadata and About, with a clear
  open-source/MIT label and a source-code link. GitHub publication remains deferred.
- Coalesce panel layout work and avoid process/title reads on every active fullscreen
  tick. Layout controls remain disposable and add no idle polling.
- Keep fullscreen mouse-edge detection associated with its auto-hidden taskbar
  when vertically stacked monitors put the hidden bar's HWND on the next screen.
- Editor validation: 482 Core tests, including the opt-in native fullscreen smoke
  test, and final ARM64/x64 Debug builds passed with no warnings or errors. GUI
  checks covered responsive columns, category and cross-group icon moves, empty
  category drops, creation, Cancel, and actual module unloading/re-enabling.
  Physical touchscreen dragging remains untested. A local hidden ARM64 run with
  all eleven modules enabled averaged 10.18 MiB private working set; the workload
  and measurement limits are recorded in the panel notes.

- Add Fullscreen: toggle the last active app from Llampec or with configurable
  Ctrl+Alt+F, retaining its own controls. Show the active window and monitor;
  restore the original state on toggle, normal exit or selection of another window.
- Support fullscreen on all monitors and mouse-edge taskbar reveal where Windows
  provides a taskbar, preserving the user's auto-hide and touch preferences.
- Compensate measured window and client-area margins to cover the monitor,
  including Chromium's frame margins. Visual hardware validation remains ongoing.

## 0.2.0-alpha.7 — 2026-10-02

- Add a Touch taskbar toggle for Windows' touch-optimization preference, with
  verified read-back, external-change refresh and explicit unavailable states.
- Add Rotation lock with the internal display selected by default, a dynamic
  display selector and four visual screen/AB orientation choices in a 2-by-2 grid.
  Orientation is applied and saved immediately, without a confirmation countdown.
  Failed operations retain conditional recovery across panel closure and respect
  newer external display changes.
- Keep AB lettering parallel to the screen's short edge in portrait cards, with
  flipped choices upside down. Report failures when opening Windows display settings.
- Allow manual orientation while Windows pauses automatic rotation for an attached
  keyboard, dock or multiple-monitor configuration; the automatic-rotation lock
  control continues to follow Windows' availability restrictions.
- Disable automatic taskbar hiding while the tablet-optimized taskbar is inferred
  active, showing the reason without changing the saved auto-hide preference.
  Refresh availability on posture/settings changes, resume and Explorer restart;
  recheck before applying a change. Unknown configurations have a separate status.
- Keep the panel on its opening monitor during rotation and reserve the
  shell-reported touch taskbar bounds so its expanded state cannot cover the footer.
- Refine the screenshot icon to four separated dashes, round the display-power
  icon, and standardize Fluent glyphs, optical alignment and navigation sizes.
  Scale the complete 24-unit vector geometry to 20 DIP so the monitor edge, stand
  and screenshot plus are no longer clipped.
- Defer the global touch-mode experiment to a future version at the user's request.
  It is separate from the touch-taskbar preference and does not block this release.
- Save orientation with the normal apply-and-save operation: on this Surface,
  the no-reset variant reported success but its saved-mode read-back stayed stale.
  Failure recovery restores separately saved and active modes when they differ.
- Validation: 386 hardware-safe Core tests passed, including 82 rotation cases;
  ARM64 and x64 builds completed without errors or warnings. Computer Use verified
  the final portrait lettering in light mode and the Windows display-settings link.
  Real manual rotation with an attached keyboard applied and saved portrait, then
  restored landscape without changing Windows' rotation flags. Earlier checks
  covered light/dark icon colors and persistence beyond 15 seconds and a normal
  app exit/relaunch. Keyboard transitions, sensor reliability, external-display
  and suspend/resume coverage remain pending.

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
