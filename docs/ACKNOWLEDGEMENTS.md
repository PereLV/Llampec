# Acknowledgements

## Microsoft PowerToys

Llampec's Always on Top feature is inspired by
[Microsoft PowerToys Always on Top](https://github.com/microsoft/PowerToys/tree/main/src/modules/alwaysontop),
particularly the direct window shortcut and the visual outline for pinned windows.
Thank you to Microsoft and the PowerToys contributors.

The Llampec implementation uses its own C# service and documented Win32 APIs;
the feature does not bundle or require PowerToys. No PowerToys source code was
copied into this implementation. PowerToys is distributed under the
[MIT license](https://github.com/microsoft/PowerToys/blob/main/LICENSE).

If future work copies or adapts PowerToys code, retain the original copyright and
license notices with that code and include them in distributable packages.

## Mouser: selected Logitech HID++ feature behavior

The Logitech feature session in
`src/Llampec.Core/Devices/Logitech/LogitechDevice.cs` adapts selected behavior from
[Mouser's `core/hid_gesture.py`](https://github.com/TomBadash/Mouser/blob/e780641d3e709f914d6273985da9ac2ab85a7322/core/hid_gesture.py):
thumb-control selection/reporting, sensor DPI operations and SmartShift function
selection. Credit: Tom Badash and the Mouser contributors. The original
[MIT copyright and license notice](third-party/Mouser.LICENSE.txt) is retained.

This attribution covers that feature code. The Windows transport, HID++ message
router and shortcut emitter are new C# implementations using protocol and Windows
documentation. Llampec does not incorporate Mouser's Python/Qt runtime,
interface, global mouse hooks or profile engine.

The packaging script includes these documents and the original license. Both
Llampec and the standalone Logitech probe also copy the notice into their
build/publish output.
