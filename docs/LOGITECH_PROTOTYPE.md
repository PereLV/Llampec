# Logitech MX diagnostic probe

This console tool provides read-only inspection and bounded hardware trials using
the same HID++ transport and feature layer as Llampec's
[integrated Logitech service](LOGITECH.md). Use the application for saved settings,
background remapping and connection recovery. The probe remains useful for testing
a model or connection before enabling it in the application.

## Build and inspect

From the repository root, with the .NET 10 SDK:

```powershell
dotnet run --project tools/Llampec.Logitech.Probe -c Release -- list
dotnet run --project tools/Llampec.Logitech.Probe -c Release -- inspect
```

`list` enumerates Windows HID metadata. `inspect` sends read-only HID++ queries and
prints the device name, features, controls, DPI and wheel state. If more than one
vendor collection is present, select the index from `list` with `--device N`.
Direct Bluetooth uses index 255; receiver slots 1–6 require an explicit `--slot N`.
Receiver hardware has not been validated.

## Temporary functional trials

Disable Llampec's Logitech module and close other HID++ configurators before a
probe trial. Start with one setting:

```powershell
# Record physical thumb press/release events without generating keyboard input.
dotnet run --project tools/Llampec.Logitech.Probe -c Release -- trial --watch-thumb --seconds 30

# Send Win+Tab for each thumb press.
dotnet run --project tools/Llampec.Logitech.Probe -c Release -- trial --thumb-shortcut Win+Tab --seconds 60

# Adjust the sensor DPI, independently of Windows' pointer-speed preference.
dotnet run --project tools/Llampec.Logitech.Probe -c Release -- trial --dpi 1200 --seconds 10

# Native wheel mode: ratchet, free or automatic SmartShift.
dotnet run --project tools/Llampec.Logitech.Probe -c Release -- trial --wheel-mode auto --smartshift-threshold 25 --seconds 10

dotnet run --project tools/Llampec.Logitech.Probe -c Release -- trial --invert-vertical true --invert-horizontal true --seconds 10
```

Options may be combined. The tool checks capabilities, records the settings it
will change, writes them and verifies readback. DPI values must match the reported
range and step or discrete list. Wheel inversion preserves native report routing
and resolution. The probe's SmartShift threshold argument accepts 1–50; the
integrated settings page supports the protocol's 1–254 automatic range.

Normal timeout or Ctrl+C restores and verifies changes before closing the HID
handle. Keep the mouse connected until **Restoration verified** appears. Forced
termination or disconnection can prevent cleanup, and firmware may retain wheel
settings. Unlike the application service, the probe has no persistent recovery
journal or automatic reconnection. After an interrupted trial, inspect the mouse
and restore any remaining changes before another controller takes over.

Shortcuts target the foreground application. The tool accepts Ctrl/Alt/Shift/Win
plus letters, digits, F1–F24, navigation and common media keys, or a single key.
It skips emission while a keyboard modifier or target key is already held.
Elevated windows may reject input from a normal process. No driver or elevation
request is added.

## Scope

The console command redirects one thumb control per trial. The shared feature
layer and application service support multiple advertised divertable controls;
the probe's smaller command interface does not configure all of them. Primary
left/right buttons are not assumed divertable. Capabilities are queried from the
connected device rather than inferred from its marketing name.

The reader waits asynchronously for real notifications. There is no battery
polling, keep-alive or periodic settings query. A trial has a time limit and exits
on I/O failure. Gestures, per-application profiles, custom scroll acceleration and
wheel-rotation actions remain outside the integration.

## Recorded hardware result: 2026-09-21

Windows ARM64, MX Master 3S over Bluetooth (PID B034), 20-byte HID++ collection
FF43:0202:

- Discovery reported DPI 200–8000 in increments of 50. Middle, back, forward,
  thumb and wheel-mode controls advertised diversion; primary left/right did not.
- A combined trial changed DPI 1000 to 1050, wheel mode from free spin to automatic
  SmartShift with threshold 15, both wheel inversions and thumb diversion. Every
  change passed readback. Original values were restored and verified through a
  newly opened connection.
- A 90-second trial captured three physical thumb press/release pairs. The user
  confirmed Win+Tab opened Windows Task View. Timeout restored the original
  reporting flags. Physical wheel feel and direction were not separately scored.
- Thirty memory samples over 52.6 seconds: private working set 5.8–6.8 MB (mean
  6.1 MB), total working set 34.0–34.9 MB and final private commitment 8.8 MB.
  Additional CPU time rounded to 0.00 seconds at the sampler's 0.01-second
  precision. These measurements describe this standalone trial, not a leak test
  or the incremental cost of enabling the module in Llampec.

Later application, button and suspend/resume results are recorded in
[the integrated service's validation notes](LOGITECH.md#validation-on-2026-09-21).
Other mouse models, receiver connections and prolonged operation still require
their own hardware checks.

## Automated checks

```powershell
dotnet test tests/Llampec.Core.Tests -c Release --filter "FullyQualifiedName~HidppClientTests|FullyQualifiedName~LogitechDeviceTests"
```

These tests use fake transports. They cover interleaved notifications, packet
correlation, serialized requests, malformed replies, timeouts, cancellation,
capability validation and restoration. They never reconfigure a physical mouse.

Use `tools/measure-memory.ps1 -ProcessName Llampec.Logitech.Probe` to sample a
running trial. To measure application cost, compare enabled and disabled states
of the same Llampec build under matching conditions instead.

## Sources and attribution

Selected feature behavior is adapted from Mouser. The scope, source commit and
original MIT notice are in [Acknowledgements](ACKNOWLEDGEMENTS.md#mouser-selected-logitech-hid-feature-behavior).
That attribution does not extend to Llampec's Windows transport, message router
or shortcut emitter. The probe and application packages retain the notice.

- [Logitech HID++ protocol](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst)
- [Logitech feature documents](https://lekensteyn.nl/files/logitech/)
- [Microsoft HID reports](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/obtaining-hid-reports)
- [Microsoft SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
