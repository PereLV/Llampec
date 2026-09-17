# Caffeine mode

The caffeine tile uses the Segoe Fluent Icons Cafe glyph (EC32), shared split-button
styling and a disposable WinUI settings page. It participates in normal reordering.
Spanish, Catalan/Valencian and English strings use the shared catalogue.

## Behaviour

- The cup toggles the last saved duration. The arrow edits a draft; Activate saves
  and starts it. Apply and restart replaces the active request and resets its interval.
- Presets: indefinite, 1, 2, 3 hours. Custom: whole minutes from 1 through 1440.
- Screen-on is optional and defaults off. An active session shows its local end time,
  or Indefinite; there is no periodic countdown refresh.
- Closing the flyout releases its controls, not the session. Expiry, Deactivate,
  application exit and WM_POWERBROADCAST suspend release the request. Startup is off;
  only duration and screen preference are saved. No startup tasks are added.
- Failed replacement leaves the previous request and deadline intact and displays
  an error. Failure to save preferences prevents activation.

## Implementation and cost

`CaffeineRequest` calls documented PowerCreateRequest / PowerSetRequest, owning a
SafeFileHandle. SystemRequired prevents automatic idle sleep; optional DisplayRequired
also keeps the display on. Each acquired request is cleared on disposal, and closing
the kernel handle releases remaining requests even on process termination. Partial
acquisition failure disposes the whole request. There is no thread-affine execution
state, simulated input, power-plan change, broker or service.

`CaffeineAction` owns the request independently of XAML. Off has no request or timer;
indefinite has one native handle and no timer; finite sessions have one one-shot
TimeProvider timer. Generation checks reject stale callbacks after replacement.
The interval uses monotonic timestamps; wall-clock changes update the displayed
deadline without extending or shortening it. No polling or per-second UI updates.

## Windows limits

This is a request, subject to Windows power policy. It does not override intentional
sleep/lid closure, screen locking or security policy. On Modern Standby and battery,
Windows can terminate system/execution requests five minutes after its sleep timeout.
We do not bypass this with a privileged service. The session stops on a suspend
broadcast instead of advertising an old request as still active after resume.

References: [PowerSetRequest](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powersetrequest),
[REASON_CONTEXT](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-reason_context),
[PowerToys Awake behaviour](https://learn.microsoft.com/en-us/windows/powertoys/awake),
[Segoe Fluent Icons](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font).

## Validation

Deterministic tests cover off/indefinite allocation, main-button toggling, all preset
deadlines and custom boundaries, stale callbacks, request failure with an active
session, clock changes, invalid input and disposal. They use fake requests and time,
so they do not change the developer's power plan or wait hours.

Manual hardware checks should cover timed expiry, display-on behaviour, intentional
sleep, application exit and battery/Modern Standby policy. `powercfg /requests`
can inspect active requests from an elevated terminal. Power behaviour depends on
the device and its Windows policy; a successful API call alone does not establish
long-idle behaviour.
