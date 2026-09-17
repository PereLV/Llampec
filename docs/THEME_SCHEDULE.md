# Dark mode scheduling

## Architecture

- `Core/Settings/ThemeSchedule.cs`: pure calculation, no registry, networking, timer or UI.
- `Core/Settings/ThemeScheduleStatus.cs`: localized current-state/countdown text and the
  next displayed-minute boundary. Reads no Windows state and never changes the theme.
- `App/ThemeScheduler.cs`: one non-repeating DispatcherQueue schedule timer. No active timer in Off.
  Theme writes run on a worker because notifying other applications can block.
  A semaphore serializes schedule reconciliations; disposal stops the timer and removes system event handlers.
  A separate one-shot display timer runs only while the panel is visible and a real
  transition is pending. It updates the countdown at its next rounded-minute boundary.
- `Flyout/ThemeScheduleView.cs`: draft controls created only on navigation. Apply validates,
  saves atomically, then reconciles. Leaving the page disposes its location cancellation token.
- `SystemEvents`: receives time changes, resume and time-zone broadcasts. Theme notifications
  do not reapply the schedule, allowing a manual override until the next boundary.

The main Dark mode button toggles the Windows theme. Its arrow opens the schedule
configuration page.
Preview theme CLI overrides are separate from saved preferences.
Its main-grid subtitle always shows the actual Windows application theme, including a
manual override, and adds the remaining hours/minutes on a second compact line.
The countdown rounds up and refreshes on opening, applying a schedule and while visible.
Windows theme broadcasts refresh the actual state without reapplying the schedule.
Off, failed scheduling, polar-day reevaluations and elapsed deadlines show only the
current state rather than implying a future theme change. Full text including the
scheduled target mode is available to assistive technology and in the tile tooltip.

## Semantics and limitations

Light and dark start times must differ and may cross midnight. Fixed hours use the
current Windows time zone. Invalid spring times advance to the first valid minute;
ambiguous autumn times use the earlier occurrence. Coincident adjusted transitions
prefer dark mode, including the target shown in the upcoming-change text.
Starting/resuming Llampec and changing the clock re-evaluate the mode.
Off never changes the current theme. Llampec must stay running for scheduled changes.

Sunrise and sunset use the [NOAA fractional-year equations](https://gml.noaa.gov/grad/solcalc/solareqns.PDF)
with an apparent horizon of 90.833 degrees. They are an approximation for theme switching,
not a precision ephemeris. Neighbouring UTC dates cover the date line. Polar day/night
uses daily reevaluation. Saved coordinates do not follow the device automatically;
use the location button again after travel. Location is optional and never polled.

Transient theme-write errors are logged and retried after five minutes. Invalid configuration
does not retry until corrected. Errors are visible when opening/applying the schedule page.
Permission denial or unavailable location leaves manual coordinates available.

## Validation

Pure tests cover boundaries, midnight, reversed intervals, equal-time rejection,
DST gaps/overlaps, coincident transitions, coordinate validation, equinox sunset,
polar day/night and both sides of the date line. Status tests cover localization,
manual overrides, compact countdowns and minute-boundary refresh.

Manual checks should include applying each schedule mode, invalid coordinates,
location permission granted/denied/cancelled, scheduled transitions, resume and
clock/time-zone changes. The scheduler remains active when page controls are
released; see [panel lifecycle](ARCHITECTURE.md).
