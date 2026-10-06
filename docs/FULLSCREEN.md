# Fullscreen

Development feature, integrated after user approval on 2026-10-05. The standalone
PowerShell trials in `tools/` remain investigation tools; the application owns
its own session without their timeout.

## Use

The Fullscreen tile acts on the application used before opening Llampec.
**Ctrl+Alt+F** toggles it globally; the options page can change or disable the
shortcut and identifies the active window and monitor. Only one window is managed
at a time. The app retains its own controls, unlike a browser's F11 mode.

Toggle again, select another application window from the taskbar or Alt+Tab, or
exit Llampec normally to restore the original frame and normal/maximized state.
Windows shell surfaces, Llampec and dialogs belonging to the fullscreen app
preserve the mode. Minimizing or hiding the target ends the session while keeping
that new minimized/hidden state. Sessions are not resumed after restarting Llampec.

Any monitor can host fullscreen. Mouse hover at its bottom edge briefly releases
fullscreen taskbar classification so its primary or secondary taskbar can appear;
moving away covers the monitor again. When Windows does not provide a taskbar on
a particular monitor, this feature does not create one. The native bottom-edge
touch gesture remains Windows-owned. No auto-hide or touch-taskbar preference is
changed.

## Implementation

`FullscreenService` belongs to `App`, independent of disposable page controls. It
captures the external target before panel activation, reports changes to the tile
and options page, and uses the existing configurable-hotkey registration with
conflict handling. A 30 ms dispatcher timer runs only while a session is active;
it checks hover and debounces selection of a different application for 120 ms.
Native foreground events keep the inactive target up to date. Window lifecycle,
name and cloaking events are hooked only while the panel is shown or a session is
active.

The native session records identity, window placement, styles, extended styles,
physical monitor bounds and the original corner preference when readable. A unique
window property protects restoration against handle reuse or ownership changes.
Frame styles are removed and small measured DWM/client-area insets are compensated
so the content covers the monitor. Insets are combined by taking the maximum on
each side, not by adding them. Measurement and positioning use a per-monitor DPI
context; unreasonable margins are rejected with rollback.

Chromium can retain a non-client inset when its internal fullscreen flag remains
false, even after the caption is removed externally. Checking DWM bounds alone did
not resolve the user's narrow transparent frame in the 120-second trial. The
integrated backend therefore also measures the client rectangle in screen pixels.
Its actual visual result in Edge still needs hardware validation.
[Chromium frame handling](https://github.com/chromium/chromium/blob/main/ui/views/win/hwnd_message_handler.cc),
[Windows window bounds](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect).

The taskbar transition uses `ITaskbarList2.MarkFullscreenWindow` and an exterior
bottom edge one pixel short of the monitor during reveal, following Chromium's
background-fullscreen approach. It never resizes, hides or otherwise controls
Explorer's taskbar windows. Primary and secondary tray windows are inspected only
to determine the available hover band. Explorer restart reapplies session policy.
[Microsoft fullscreen classification](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist2-markfullscreenwindow).

Restoring a previously maximized window uses its captured maximized geometry and
style with no activation. Applying its original maximized `WINDOWPLACEMENT`
directly was observed to steal focus from the newly selected app, so the native
session avoids that path. Partial activation failures also restore captured state.
After a monitor or resolution change, restoration uses an available monitor's
current work area and frame insets instead of obsolete maximized coordinates.
Failed restoration retains the owned session for an explicit retry, with edge
polling stopped; normal shutdown retries cleanup.

## Validation

The user verified the mouse/touch behavior of earlier prototypes with conventional
auto-hide enabled and disabled. The first integrated build extends this behavior
to secondary monitors and adds client-inset compensation; those changes remain
subject to visual testing on the user's apps and display configuration.

Deterministic tests cover session state, target capture, focus policy, failures,
edge timing and geometry. Opt-in native smoke tests use only synthetic test-owned
windows. They verify the production backend without resizing the user's apps or
changing taskbar preferences. They do not establish compatibility with every app,
touch posture or mixed-DPI monitor arrangement.

Validation on 2026-10-05:

- ARM64 and x64 Debug application builds pass with no warnings or errors.
- The complete Core suite passes: **413 tests**, with the opt-in native smoke
  enabled. Synthetic windows cover each attached monitor in normal, maximized and
  initially borderless states, including persistent 7-pixel client insets, clipped
  overscan, taskbar reveal/cover and exact style/placement/region restoration.
- The ARM64 development app was launched after normally exiting the previous
  instance. Its tile, options, Catalan text and registered default shortcut were
  verified through Computer Use.
- Real Edge visual validation could not proceed: Computer Use blocked activation
  because it could not determine the current browser URL confidently enough to
  enforce its policy. No fullscreen keyboard input was sent to Edge. Native touch
  and real multi-monitor taskbar behavior still require user validation.

Only normal shutdown can restore an active session reliably. Force termination
has no cleanup opportunity; use Llampec's Exit command before rebuilding or moving
the application.
