# Tablet controls

Version **0.2.0-alpha.7** adds **Touch taskbar** and **Rotation lock**.
The [initial research](TABLET_RESEARCH.md) records the scope and alternatives considered.
Separate follow-up research covers the [native rotation animation](ROTATION_ANIMATION_RESEARCH.md)
and [global touch mode](GLOBAL_TOUCH_MODE_RESEARCH.md); those features are outside this release.

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

## Global tablet posture: deferred to a future version

The user deferred the global touch-mode experiment on 2026-10-02. It is outside
this tablet-controls block and does not block its completion or a release. The
research below is retained for a future version; no system-changing experiment is planned
for the current work.

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
