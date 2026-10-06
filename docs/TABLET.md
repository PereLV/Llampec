# Tablet controls

Version **0.2.0-alpha.7** adds **Touch taskbar** and **Rotation lock**.
The [initial research](TABLET_RESEARCH.md) records the scope and alternatives considered.
Separate follow-up research covers the [native rotation animation](ROTATION_ANIMATION_RESEARCH.md)
and [global touch mode](GLOBAL_TOUCH_MODE_RESEARCH.md); those features are outside this release.
Both investigations are now parked under the user's lightweight-product scope.
The [touchscreen-gesture research](#touchscreen-gestures-research) below records
configurability and local observations for future discussions, without choosing
a new feature to implement.

## Touch taskbar

Each press enables or disables Windows' preference **Optimize taskbar for touch
interactions when this device is used as a tablet**. Enabling it allows Windows
to use the touch taskbar in tablet posture; it does not simulate a detached keyboard.
The tile shows the preference, with a subtitle when the touch presentation is
inferred active. The conventional auto-hide tile is disabled in that state and
becomes available again with the normal taskbar. Its saved auto-hide flag is preserved.

The setting is the current user's existing `ExpandableTaskbar` DWORD (0 or 1),
followed by `WM_SETTINGCHANGE` with `Advanced` and a read-back. Notification runs
outside the UI thread. Unknown values, posture overrides, remote sessions and
unsupported hardware disable the control with an explanation. Missing settings
are not created, Explorer is not restarted, and OEM/posture values are not changed.

This registry preference is an implementation detail, used by Microsoft's
[HOBL tooling](https://github.com/microsoft/HOBL/blob/main/docs/support/docs/HOBL_Prep.md),
not a public settings API. The [notification](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-settingchange)
and read-back confirm that the preference was saved; there is no public API to
confirm Explorer's exact visual state. Failed notification is reported separately.
Future Windows shell changes may require revisiting the integration.

Keeping the collapsed tablet taskbar with independently smaller expanded icons
remains outside the agreed scope: no clean supported solution was found.

## Rotation

The main tile toggles the system rotation lock used by the internal display.
It reads `GetAutoRotationState`, sends one complete **Win+O** chord, and verifies
the actual result. Held keys, unavailable sensors, attached keyboards, docks,
multiple monitors, remote sessions and other suppression flags have distinct
states. A failed shortcut does not produce an optimistic successful toggle.

The arrow opens a display selector and four visual orientation cards in a 2-by-2
grid. Each card shows a screen with the letters **AB**, plus a localized caption
and accessible name. In portrait, the text baseline follows the screen's short
edge; normal portrait reads upright and flipped portrait reads upside down. The
current orientation stays selected. The integrated panel is selected initially;
each external display can be selected explicitly. The
inventory refreshes on system events and when requested, including newly connected
monitors. A disconnected selection never falls back to another display. Duplicated
displays must be extended before changing their orientation independently.

Choosing an orientation applies and saves it immediately. There is no countdown
or Keep step: the previous 15-second confirmation was implemented by Llampec,
not imposed by Windows. Successful choices survive panel closure and application
exit. When automatic rotation can run, the internal display is locked before
applying a fixed orientation. Manual orientation remains available when Windows
has already paused automatic rotation because of an attached keyboard, dock or
multiple-monitor configuration; Llampec does not require an unavailable lock
toggle in those states. The main automatic-rotation control retains Windows'
restrictions. Orienting an external screen does not change the internal screen's
lock.

Public CCD APIs identify physical displays; `EnumDisplaySettings` and
`ChangeDisplaySettingsEx` test, apply, save and verify the selected source.
Only a failed operation retains a recovery snapshot. Llampec attempts to restore
the previous current/saved mode and any lock it changed; a transient restoration
failure offers a retry and is retried at normal exit. Recovery verifies the
current topology and modes first and does not overwrite a newer external change.
This is not a crash-recovery service. Driver errors and failed recovery are shown
with a link to Windows display settings. The link explicitly calls
`Launcher.LaunchUriAsync` for `ms-settings:display` and reports a rejected launch
or exception instead of silently ignoring it. The panel preserves its opening monitor
and recalculates its work area after orientation or display changes.

In tablet posture the work area can describe only the collapsed taskbar. The panel
also queries the public `ABM_GETTASKBARPOS` bounds and reserves them when they are
valid and belong to its opening monitor, preventing the expanded bar from hiding
the footer. It uses the ordinary work area as fallback, without fixed taskbar
heights or periodic polling. The extra room remains while the bar is collapsed.
The system-taskbar API does not identify secondary taskbars on arbitrary monitors.

These controls expose Windows' state and provide a manual orientation option;
they do not replace the orientation sensor or repair its driver/firmware. Physical
rotation and resume reliability still need testing on the user's hardware.

### Rotation animation

The animation observed during physical rotation belongs to Windows' orientation
transition. Microsoft's [UWP rotation documentation](https://learn.microsoft.com/en-us/windows/uwp/gaming/supporting-screen-rotation-directx-and-cpp#reduce-the-rotation-delay-by-using-corewindowresizemanager)
describes an OS-owned animation and how an application reports that its new
layout is ready. That notification does not initiate a desktop rotation.
The documented parameters of [ChangeDisplaySettingsEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw)
and [SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig)
do not expose a way to request the same transition. No supported public hook was
found for invoking the sensor-driven animation from this WinUI desktop selector.
Llampec therefore changes the orientation without promising that animation;
it does not emulate a sensor event or overlay a fake full-screen transition.

## Global tablet posture: parked research

The user deferred the global touch-mode experiment on 2026-10-02. It is outside
this tablet-controls block and does not block its completion or a release. The
research below is retained as historical context; no system-changing experiment
is planned. After reviewing the proposals, the user reaffirmed that Llampec
should expose existing settings without deep OS changes. Reconsider only if a
simple supported approach fits that scope.

Touch-taskbar preference and global tablet posture are separate. Disabling
`ExpandableTaskbar` does not report an attached keyboard to Windows. Applications
can react to posture independently and may also have their own touch settings.
Microsoft explicitly documents that [Windows 11 has no manual tablet-mode switch](https://support.microsoft.com/es-es/windows/hardware/input-devices/turn-tablet-mode-on-or-off-in-windows).
The old [UIViewSettings.UserInteractionMode API](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uiviewsettings.userinteractionmode)
directs Windows 11 applications to Convertible Slate Mode (CSM) instead.

`HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\ConvertibleSlateMode`
represents the hardware posture: 0 means no accessible physical keyboard, 1 means
laptop mode. Microsoft documents this as an [OEM/driver signal](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-gpiobuttons-convertibleslatemode),
not a user-preference API. [GetSystemMetrics(SM_CONVERTIBLESLATEMODE)](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics)
exposes it to applications. An experimental change could affect more than the
taskbar, but the hardware driver can overwrite the signal on a posture or power
transition. This is a candidate for a controlled experiment, not an implemented
global toggle.

`ConvertibilityEnabled` instead declares whether the hardware is convertible;
it is an [OEM classification override](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/settings-for-better-tablet-experiences).
Chromium's [Windows posture detection](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/base/win/win_util.cc)
caches that override and combines it with other signals. Changing it is therefore
unsuitable for a quick switch across open applications. This source describes
Chromium, not a verified implementation of Edge's own touch-mode setting.
The legacy `ImmersiveShell\TabletMode` is not a reliable Windows 11 control.

Read-only observations on this Surface: `ConvertibleSlateMode=0`,
`ConvertibilityEnabled=1`, `ImmersiveShell\TabletMode=0`, and both TabletTip
`ConvertibleSlateModeChanged` and `ConvertibleChassis` equal 1. Access to write
the machine-level CSM setting was denied in the current session, so the proposed
temporary 0-to-1 experiment has not run and no posture value was changed.
Computer Use could not open Edge's settings because its URL policy blocked the
page. Edge's setting and visual response were not verified in this revision.
No global switch is exposed by Llampec. If this work is resumed in a future
version, its experiment must record and
restore the exact original posture and app preferences and verify the live
metric and visible behavior before drawing conclusions.

## Full-screen windows

**Integrated into the development application on 2026-10-05 after user approval.**
The tile, configurable shortcut, active-window/display indicator and session
restoration now belong to Llampec. See [fullscreen behavior](FULLSCREEN.md) for
the current implementation and validation limits. It has no trial timeout and
allows any monitor, including monitors without a local taskbar.

The following records the earlier standalone experiments.
`tools/test-fullscreen-edge.ps1` and `tools/FullscreenEdgeTrial.cs` test a
borderless window that covers its monitor while preserving the app's own controls
and the user's taskbar preferences. This is separate from browser F11 and from
the earlier `NonRudeHWND` experiment in `tools/test-fullscreen.ps1`.

The user reported that mouse reveal works with conventional taskbar auto-hide
both enabled and disabled. The native bottom-edge touch gesture also reveals the
bar. An early revision mistakenly restored the window when a shell panel took
focus; later revisions preserve the mode during shell interaction. The user then
reported a narrow transparent frame around the window. The revised experiment
removes extended frame styles, requests square corners only when the original
preference can be preserved, and compensates small measured invisible margins
using DWM visible bounds. The user confirmed that the narrow frame still appeared
in the 120-second version. The integrated backend additionally compensates measured
client insets and clips overscan to the selected monitor; its visual result still
needs validation in Edge.
[Visible versus exterior window bounds](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect).

The controls agreed in this discussion and now integrated are:

- A Llampec toggle button that restores the original window state when switched
  off. Its status should identify the active application and display.
- A global toggle shortcut, **Ctrl+Alt+F** by default, using the existing
  configurable-shortcut pattern.
- Selecting a different application window restores the fullscreen window. The
  prototype applies this to taskbar selection and Alt+Tab, including another
  window from the same process. Taskbar surfaces, system panels, Llampec and
  dialogs owned by the target preserve the mode and pause mouse-edge detection.

The shortcut remains available for the whole bounded test session, allowing
activation again after manual or automatic restoration. The console reports the
monitor device identifier; it is not a permanent on-screen overlay. This first
prototype is limited to the monitor with the primary bottom-edge taskbar;
this restriction does not apply to the integrated service.
Normal expiry restores the window and releases the shortcut. Keep its PowerShell
process open while a window is modified.

Native synthetic-window checks on ARM64 pass for visible monitor coverage,
extended-style and corner restoration, normal/maximized restoration without
changing the foreground, minimized/hidden state preservation, partial-failure
rollback, shortcut reservation/conflict/release and the foreground policy. These
checks do not establish visual compatibility with every app or every combination
of conventional auto-hide and touch-optimized taskbar modes. The integrated
button, display indicator and new client-margin compensation have their own
production tests; real multi-monitor and Edge visual validation remain ongoing.

## Touchscreen gestures research

Research on 2026-10-02, recorded on 2026-10-03. This concerns the **screen**,
not the keyboard's touchpad. No gestures, settings or app behavior were changed.

### What Windows assigns

Windows 10 assigned the left edge to Task View and the right edge to Action
Center. Windows 11's baseline assigns those edges to Widgets and notifications.
Its general support page documents three fingers up for Task View, three down
for the desktop, three horizontally for the previous app, and four horizontally
for virtual desktops. [Microsoft gesture catalogue](https://support.microsoft.com/en-us/windows/hardware/input-devices/touch-gestures-for-windows).

The user reports that **four fingers up also opens Task View**. This matches
Microsoft's original Windows 11 announcement, which explicitly gives four-finger
vertical swipes the same behavior as three-finger vertical swipes. After showing
the desktop, swiping up can instead restore the minimized windows. Do not dismiss
the user's observation because the shorter support table lists only three up.
[Windows 11 input announcement](https://blogs.windows.com/windows-insider/2021/06/28/announcing-the-first-insider-preview-for-windows-11/).

### Actual configurability

| Screen control | Scope found |
| --- | --- |
| Three- and four-finger gestures | One on/off setting; no freely assignable system actions found. |
| Left-edge swipe | Enables/disables the Widgets gesture; no Task View assignment found. |
| Right-edge swipe | Copilot+ PCs can select Click to Do or return to notifications. |
| Two-finger press-and-hold | Enables/disables the Click to Do gesture on supported Copilot+ PCs. |
| Touch to wake | Hardware-dependent wake preference, not an assignable navigation gesture. |

The screenshot supplied by the user shows three/four fingers enabled, touch to
wake enabled, left edge enabled, right edge set to **Click to Do**, and two-finger
hold enabled. The right-edge dropdown and Additional touch settings are closed
in that screenshot; their full local options were not inspected. Do not claim
that an arbitrary action or a particular full dropdown list was verified.

Microsoft documents switching the right edge back to notifications in Touch
settings. That Copilot+ behavior is newer than the generic gesture table.
[Right-edge change](https://blogs.windows.com/windows-insider/2025/04/03/announcing-windows-11-insider-preview-build-26120-3671-beta-channel/).
The two-finger hold opens Click to Do; it is distinct from a one-finger long
press for a context menu. [Released update description](https://support.microsoft.com/en-us/servicing/os/windows-11/2025/10/october-28-2025-kb5067036-os-builds-26200-7019-and-26100-7019-preview).
Touch-to-wake depends on device support, posture and power state.
[Wake-on-Touch guide](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/wake-on-touch-implementation-guide).

Disabling three/four-finger system gestures frees those interactions for apps;
it does not create replacement system mappings. This may matter for drawing
software with its own gestures. Microsoft recommends linking to
`ms-settings:devices-touch`. Touchpad action dropdowns are a separate setting.
[System reservation of gestures](https://learn.microsoft.com/en-us/troubleshoot/windows/win32/finger3or4-touch-interaction-no-longer-works).

### Why the left edge appears to do nothing on this Surface

Read-only local observations: Windows 11 Home (`Core`), **26H1 build
28000.2956**. `Get-AppxPackage -Name MicrosoftWindows.Client.WebExperience`
returned zero packages for the current user without an error. The user then
confirmed: **Widgets was removed**. These facts strongly support a missing
Widgets destination as the explanation, rather than an unused configurable
gesture. Physical swipe behavior was not independently tested or repaired.

Microsoft identifies Windows Web Experience Pack as a Widgets dependency.
Merely hiding the taskbar button would not normally disable the edge gesture;
uninstalling its component is a different action.
[Widgets dependency](https://support.microsoft.com/en-us/windows/deployment/updates-lifecycle/how-to-update-the-windows-web-experience-pack),
[Widgets access and taskbar visibility](https://support.microsoft.com/en-us/windows/experience/personalization/stay-up-to-date-with-widgets-in-windows).

The checked HKLM/HKCU `SOFTWARE\Policies\Microsoft\Dsh` values
`AllowNewsAndInterests` and `DisableWidgetsBoard`, and
`SOFTWARE\Policies\Microsoft\Windows\EdgeUI\AllowEdgeSwipe`, were absent.
HKCU `Explorer\Advanced\TaskbarDa` was also absent. This limited snapshot does
not exclude every policy or other source of failure. Nothing was installed,
enabled or restored; the user had intentionally removed Widgets.

Computer Use returned `native pipe is unavailable` / `os error 2`, including
after the documented retry and session reset. Consequently this investigation
used the user's screenshot, public documentation and read-only system queries;
it did not inspect live dropdowns or reproduce multi-touch input.

### Implications for Llampec

No supported setting/API was found to assign **Task View to the touchscreen's
left edge**. `SetGestureConfig` configures a particular window, not the shell's
global gesture actions. Edge-swipe policies permit blocking system edge UI,
not choosing replacement actions.
[Per-window configuration](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setgestureconfig),
[Edge-swipe policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-lockdown#allowedgeswipe).

This is not a claim that interception is technically impossible: the public
`RegisterPointerInputTarget` API redirects all input of a chosen pointer type
to a privileged UIAccess app, which must handle/pass on the other interactions.
That would be a new input subsystem, not a shortcut to an existing preference,
and does not fit the agreed product scope.
[Pointer redirection](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerpointerinputtarget).

Compatible ideas for a later discussion: a **Task View** action using the native
Win+Tab shortcut, a link to Touch settings, or a compact gesture guide. Windows
also has its own Task View taskbar button. None is selected for implementation.
[Windows multitasking](https://support.microsoft.com/en-gb/windows/how-to-multitask-in-windows-b4fa0333-98f8-ef43-e25c-06d4fb1d6960).

### Future idea: Llampec actions from screen edges

**Recorded on 2026-10-03: future idea, pending evaluation; not the next
implementation.** The user proposes turning off Windows' edge gestures in Touch
settings, then assigning those gestures to Llampec actions. This is independent
of the parked tablet-posture research and of the full-screen-window experiment.
No touch settings, policies or input routing were changed for this evaluation.

The first part has a lightweight route: Microsoft now documents separate left-
and right-edge switches under **Bluetooth & devices > Touch > Touch screen edge
gestures**. An entry point to that existing settings page fits Llampec's scope;
direct toggles would still need an identified integration and local validation.
Turning off a shell gesture does not register Llampec as its replacement handler.
[Current Touch settings](https://support.microsoft.com/en-us/windows/hardware/input-devices/touch-gestures-for-windows).

`AllowEdgeSwipe` blocks system UI from screen edges; it does not choose new
actions. Its documented Policy CSP edition list is Pro, Enterprise, Education
and IoT Enterprise, **not Home**, the edition observed on this Surface. The page
maps the policy to `Software\Policies\Microsoft\Windows\EdgeUI\AllowEdgeSwipe`,
but that mapping does not prove a supported Home toggle or immediate application
on this build. Prefer the existing per-user settings over introducing a broad
policy; do not create registry policy values merely to test the idea.
[Policy contract and edition scope](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-lockdown#allowedgeswipe).

The second part remains unvalidated as a lightweight global feature.
`WM_POINTER`/`WM_GESTURE` and `SetGestureConfig` handle input for a target window,
not arbitrary edge gestures over other apps. Public global redirection through
`RegisterPointerInputTarget` requires **UIAccess**, allows one target per pointer
type per desktop and redirects all input of that type, not only edge swipes.
A replacement would need to recognize gestures and preserve the remaining touch
input, including multi-touch. Microsoft's UIAccess guidance requires signing and
protected installation and restricts its intended use to assistive technologies.
This is a substantial input subsystem, not an existing Windows preference.
[Pointer targeting](https://learn.microsoft.com/en-us/windows/win32/inputmsg/wm-pointerdown),
[Window gesture messages](https://learn.microsoft.com/en-us/windows/win32/wintouch/wm-gesture),
[Global redirection](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerpointerinputtarget),
[UIAccess requirements](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview).

**Narrower candidate, based on documented window APIs:** a small Llampec window
over a screen edge, topmost and non-activating, could receive normal
`WM_POINTERDOWN` contacts that begin in its own area. Implicit pointer capture
then keeps subsequent movement routed to that window until contact ends, even
after the finger leaves the narrow strip. After disabling the native gesture,
a local recognizer could trigger a Llampec action. This avoids redirecting all
touch input and does not itself require the global API's UIAccess privilege.
It is a design hypothesis, **not a verified replacement for shell gestures**.
[Topmost/non-activating styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles),
[Position without activation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos),
[Contact targeting and implicit capture](https://learn.microsoft.com/en-us/windows/win32/inputmsg/wm-pointerdown).

The strip would own input in its area and could compete with app scrollbars or
window resizing. Its intended visual transparency needs separate hit-testing
validation: a layered window's alpha-zero/color-keyed areas pass mouse input,
and `WS_EX_TRANSPARENT` also changes mouse routing. Do not assume that an
invisible strip can both receive touch and pass unrelated input through.
Before proceeding, test real touch, native edge reservation after disabling,
ordinary mouse/pen input, app full-screen behavior and z-order, DPI/rotation and
multiple monitors. Do not promise operation on the secure desktop. No overlay,
input injection, private shell hook or driver was tested. Keep this bounded
candidate on the future ideas list; continue only if it preserves ordinary input
and fits the lightweight scope. Settings access and native shortcut buttons
remain possible independent ideas.
[Layered-window hit testing](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).

## Icons and touch targets

Tiles share a 24-DIP frame with 20-DIP Fluent glyphs or matching monochrome vectors.
The screenshot frame uses four separated dashes, rounded corners and a plus sign.
Display power uses rounded monitor corners and the same stroke weight. Both
vectors retain their full 24-unit geometry inside a `Viewbox` scaled to 20 DIP;
constraining `PathIcon` directly to 20 DIP had clipped the monitor's right edge
and stand, as well as the screenshot plus. Theme
badges stay inside the frame; navigation uses consistent Fluent sizes. Foregrounds
are inherited for accent, disabled, dark, light and high-contrast states.
Back/settings buttons have at least 40-DIP targets; tiles remain 48 DIP tall and
orientation cards are at least 108 DIP tall.

## Validation

### Current revision

On 2026-10-02, 386 hardware-safe Core tests passed, including 82 rotation cases.
Release ARM64 and x64 builds completed with zero errors or warnings. The current
tests cover immediate application and persistence, no timer-based reversal,
failure recovery, different original saved/active modes, internal/external
isolation, manual orientation while automatic rotation is paused, and concurrent
external changes using injected backends.

Computer Use verified the final portrait AB lettering in the light theme: normal
portrait reads upright and flipped portrait reads upside down. The Settings link
opened **System > Display**, confirmed by a fresh screenshot. The corrected vector
icons and light/dark colors were verified earlier; the final lettering adjustment
was not separately rechecked in the dark theme.

With the user's physical keyboard already attached, Windows reported
`AR_LAPTOP` (flags 128). A real manual choice changed the internal display from
landscape (mode 0, 2880×1920) to portrait (mode 1, 1920×2880), with matching saved
mode. The lock/suppression flags remained 128 throughout. Selecting landscape
again restored both active and saved modes and left the original flags intact.
No global posture setting was forced for this test.

The earlier immediate-orientation implementation was also checked with automatic
rotation available: the selected portrait mode persisted for more than 15 seconds
and across a complete normal application exit/relaunch. That test ended with
active and saved landscape modes restored, and the original unlocked state.

That hardware test exposed a persistence issue: `CDS_UPDATEREGISTRY | CDS_NORESET`
returned success while the saved mode remained stale, even after a 1.75-second
wait and a mode-enumeration refresh. Persistence now uses the normal
`CDS_UPDATEREGISTRY` apply-and-save operation and verifies its result. Recovery
uses the same operation for the original saved mode, then reapplies the original
active mode without persistence when the two originally differed. Regression
tests cover that distinction; a return code alone is not treated as proof that
the saved orientation changed.

Transitions through keyboard detach/fold/attach, suspend/resume, sensor reliability,
external displays, high contrast and x64 hardware still need their own validation. No
current visual verification of Edge or a global posture override has been
completed; the access limitations are recorded above. These checks form the
validation baseline for 0.2.0-alpha.7.

### Earlier revision on 2026-10-02

These results describe the previous implementation with its confirmation timer,
before the latest orientation and vector-scaling changes. They do not validate
the current revision.

353 hardware-safe Core tests passed, including 49 rotation cases.
Release ARM64 and x64 builds completed with zero errors or warnings. Deterministic
tests use injected backends and never rotate the developer's display. They cover
combined suppression flags, native mode layout, internal/external isolation,
disconnection/cloning, partial driver failures, failed persistence, transient
reads, timers, shutdown and concurrent external changes.

On the Surface ARM64, an initial reversible prototype changed the Windows taskbar
checkbox immediately in both directions (`1 → 0 → 1`) without reopening Settings
or restarting Explorer, and restored the original preference. That observation is
separate from visually verifying Explorer's collapsed/expanded taskbar.

Computer Use then exercised the ARM64 application itself:

- Touch taskbar off/on enabled/disabled the auto-hide tile immediately. The
  conventional auto-hide action was exercised in both directions while available.
- Rotation lock changed in both directions, confirmed through `GetAutoRotationState`.
- A real portrait preview changed the internal mode from 2880×1920 to 1920×2880,
  displayed the countdown, and automatically restored landscape and the original
  unlocked rotation state. The original saved mode was preserved.
- A native smoke check confirmed the current horizontal orientation with Keep,
  verified persistence, and restored the original lock, current and saved modes.
- Light and dark previews showed the new vector icons, accent and disabled states.
  The final build's footer remained visible above the expanded touch taskbar.

The Surface reported a collapsed work-area reservation of 24 DIP and taskbar
bounds of 72 DIP. These are observations on Windows 26H1 build 28000.2956, not
constants or a general Windows guarantee. Physical keyboard detach/fold/attach,
suspend/resume, sensor reliability, external displays, high contrast and x64
hardware still require their own validation. No changes were published to GitHub.
