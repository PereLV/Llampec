# Logitech MX integration

Llampec can manage a selected Logitech HID++ mouse from **Settings → Logitech MX mouse**.
The feature is optional and disabled by default. The initial hardware target is
MX Master 3S over Bluetooth, with native Windows ARM64 and x64 builds.

## Configure

1. Open Logitech MX settings and scan for devices. Select the intended mouse.
2. Enable the feature and assign shortcuts to the buttons the device advertises
   as divertable. An empty assignment keeps its original behavior. A useful first
   assignment is **thumb button → Win+Tab**.
3. Optionally set the sensor DPI, wheel mode/SmartShift threshold, or the native
   direction of either wheel. Leave a setting unmanaged to retain its current value.
4. Apply the draft. Closing the panel does not stop the remapping service.

Opening or rebuilding the page does not change the device. Scan reads capabilities;
**Reconnect** retries the saved configuration, not unsaved edits. Status updates do
not replace the draft. A disconnected device's saved settings remain available,
and unsupported controls are not silently removed. Settings are saved before the
service applies them; a persistence failure leaves the running configuration alone.

Assignments send one complete keyboard chord per press. Modifiers, letters, digits,
F1-F24, navigation keys and common media keys are supported. Held physical keyboard
modifiers suppress injection to avoid releasing the user's keys. Elevated target
windows may reject input from a normal Llampec process.

DPI values must match the sensor's advertised range or discrete values. SmartShift
sensitivity controls when the physical wheel changes to free spin; it does not
change lines per wheel step. Automatic sensitivity accepts 1–254; fixed ratchet and
free-spin modes are separate choices. Wheel inversion is performed by the mouse firmware,
preserving native report routing and resolution. Continuous scroll processing,
gestures, macros and per-application profiles are not part of this integration.

## Lifetime and recovery

Preferences are saved in the `logitech` section of Llampec's `settings.json`.
The service binds to the chosen device path, product, receiver index and available
unit ID; receiver mice require a stable unit ID so re-pairing a slot cannot apply
another mouse's settings. It does not silently transfer assignments to another
mouse. The actual capability inventory
determines which controls and settings are available.

Before changing the mouse, Llampec atomically writes the original selected settings
to `%LOCALAPPDATA%\Llampec\logitech-recovery.json`. Disabling the feature, changing
configuration and normal shutdown restore those settings before releasing the
connection. A successful verified restoration removes the recovery record.
If the mouse disconnects first or the process is interrupted, the record remains
so a later connection can restore it before applying the saved preferences again.

HID read failures and Windows device/power events drive reconnection. Retries are
bounded and back off while the configured mouse is absent. A healthy connected
session waits for input without periodic HID queries, battery polling or keep-alive.
Suspension stops input dispatch; resuming establishes a fresh session. No kernel
driver, Python/Qt runtime or separate background process is installed.

Use only one HID++ configurator at a time. Existing diverted controls are not
claimed from another controller. A recovery failure is shown as an error and
its record is retained rather than reporting success. Physical disconnection or
forced termination can temporarily leave firmware settings applied.

## Implementation

- `WindowsHidTransport`: shared Windows vendor HID collection, async read/write.
- `HidppClient`: serialized commands and a single notification reader, correlated
  by device index, feature/function and software ID.
- `LogitechDevice`: capability discovery, per-control diversion, DPI and wheel
  operations with verified readback and restoration.
- `LogitechMouseService`: settings application, input dispatch, selected-device
  reconnection and durable recovery. Its lifetime belongs to `App`.
- `LogitechMouseView`: disposable settings draft; status changes do not require
  keeping the panel alive.

The headless [probe](LOGITECH_PROTOTYPE.md) remains available for hardware diagnosis.
Automatic tests use fake transports/backends and temporary recovery files, never
the user's mouse. Hardware evidence is recorded below and applies only to the
tested model, operating-system architecture and connection.

Selected feature behavior adapts Mouser code; the exact scope and original MIT
notice are in [Acknowledgements](ACKNOWLEDGEMENTS.md). Transport, service lifecycle,
recovery storage and Llampec controls are implemented in C# for this application.

## Validation on 2026-09-21

- 244 Core tests passed with the CI hardware-safe filter, covering packet correlation, capability
  validation, multiple buttons, cancellation, lifecycle, stale input, physical
  identity, file recovery and source-generated settings JSON. Six additional
  read-only action-catalog integration tests passed in the local Windows session.
- ARM64 and x64 application builds succeeded without warnings. The ARM64 published
  application passed the compiled WinUI resource check and ran natively.
- The real `LogitechMouseService` discovered an MX Master 3S over Bluetooth,
  applied DPI 1000 to 1050, SmartShift threshold 15, both wheel inversions and thumb
  diversion, and verified every setting by readback. Normal stop restored the
  original state, verified through a freshly opened connection, and removed the
  recovery journal.
- The actual application loaded the saved thumb-to-Win+Tab preference, connected
  successfully and persisted its original reporting state. A normal `--exit`
  restored reporting flags to zero and removed the journal; a new background
  launch reactivated the saved mouse configuration.
- The user confirmed the physical thumb button worked inside Llampec and remained
  functional after a real Windows suspend/resume cycle. These checks used the
  MX Master 3S over Bluetooth on Windows ARM64.

Two short background samples used the same ARM64 publication, 15 observations
each, after Llampec's usual idle release. With Logitech disabled/enabled,
respectively: average private resident RAM was 6.0/5.9 MB; final private committed
memory was 46.5/47.9 MB; CPU time increased 0.03/0.05 seconds over 25.9/22.3 seconds.
These are whole-process measurements with natural run-to-run variation, not a
precise incremental memory estimate or a prolonged leak test.

Remaining hardware coverage includes Bluetooth power cycling, receiver connections,
other MX models, simultaneous use with a second pointing device and prolonged
operation. The x64 build also needs equivalent physical-device checks. Native wheel
settings passed device readback; that does not replace a user's assessment of wheel
feel or direction. The existing results do not establish compatibility with every MX
mouse or connection type.
