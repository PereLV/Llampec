# Power

The **Power** tile (**Energía** in Spanish, **Energia** in Catalan/Valencian) shows
the power mode and battery charge. Tapping it cycles the Windows power mode:
best power efficiency → balanced → best performance. Like Windows Settings, the
mode applies to the current power source: changing it on battery leaves the
plugged-in mode unchanged, and vice versa. The options page applies each change
immediately and reads it back.

## Options

- **Power mode:** the three Windows modes, available with the Balanced plan.
  A note appears when energy saver is on, because it can limit performance.
- **Power plan:** shown only when Windows offers more than one plan. Modern
  standby devices, such as Surface, normally have only Balanced.
- **Lid and power button:** the actions for closing the lid and pressing the power
  button, on battery and plugged in. Llampec lists only the actions Windows offers
  on the device. Hibernate appears only when hibernation is available. Like Control
  Panel, a change applies to every power plan. Each row appears only when the
  device has a lid or a power button.
- **Energy saver:** whether it is on and the battery level that turns it on, with a
  link to its Windows settings. Windows offers no documented way to turn it on
  immediately, so Llampec leaves that switch to Quick Settings. Raising the plugged-in
  threshold to 100 % did not turn it on in testing. While it is on, Windows manages
  the power mode.
- **Battery:** charge, charge or discharge rate in watts, and full-charge capacity
  compared with the design capacity.
- **More power settings** opens Windows Settings.

## Implementation

- `Platform/PowerOptions` uses the documented power-management API for plans,
  lid and power-button actions (`PowerReadACValueIndex`/`PowerWriteACValueIndex`
  and their DC versions), device capabilities and power status.
  `Windows.Devices.Power.Battery` provides charge rate and capacities.
- Power mode is the one approved exception to Llampec's documented-API rule.
  Windows offers no documented way to set it, so Llampec calls the `powrprof.dll`
  overlay functions that Windows Settings uses (`PowerGetActualOverlayScheme`,
  `PowerSetActiveOverlayScheme`). These functions take the overlay GUID by
  reference; passing it by value crashes on ARM64. If Windows removes them, the
  mode controls become unavailable and the rest of the page still works.
- `Actions/Power/PowerAction` reads only the power status (`GetSystemPowerStatus`, which
  also reports energy saver), the active plan and the mode whenever the panel opens.
  The options page reads plans, button actions and the energy saver threshold
  (`ESBATTTHRESHOLD`) only while it is open. There is no timer, power-event
  subscription or background work.

Validated on a Surface (ARM64, build 28000) on battery and plugged in: each mode
applied to the current power source and read back correctly, the
original mode was restored, and lid/button values matched Control Panel.
