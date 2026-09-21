# Llampec

A compact Windows 11 quick-settings companion by [PereLV](https://github.com/PereLV).

**0.2.0-alpha.5** adds Logitech MX controls and a screenshot action. Llampec lives in
the notification area and provides eight configurable quick actions:

- **HDR:** switch all compatible displays or control each display separately.
- **Display power:** turn off the displays.
- **Light/dark mode:** change the Windows theme, with optional fixed-hour or
  sunrise/sunset scheduling and a countdown to the next change.
- **Multiple displays:** choose PC screen only, Duplicate, Extend or Second screen only.
- **Taskbar:** toggle automatic hiding.
- **Caffeine:** prevent automatic sleep for a chosen duration, optionally keeping
  the display on.
- **Always on Top:** pin the last active window, use a configurable shortcut, or
  choose windows from a list. An optional accent-colored border has adjustable thickness.
- **Screenshot:** close the panel, then open the native Windows capture selector
  with **Win+Shift+S**.

Settings also includes an optional **Logitech MX** module: assign keyboard shortcuts
to supported mouse buttons (including the thumb button), adjust sensor DPI and
configure native wheel mode, SmartShift and scroll direction. It keeps working
with the panel closed and reconnects to the selected mouse. See
[Logitech setup and recovery](docs/LOGITECH.md) for scope and hardware coverage.

The panel uses WinUI 3, desktop acrylic and compositor animations that respect the
Windows animation setting. Settings include Spanish, Catalan/Valencian and English,
per-user startup, and button reordering.

## Requirements

- Windows 11 24H2 (build 26100) or later.
- ARM64 or x64.
- .NET 10 Runtime matching the application architecture.

The portable build includes its Windows App SDK dependencies. Keep the entire
application folder together; copying only `Llampec.exe` is insufficient.
This alpha does not include an installer or code signing.

## Use

Download the archive for your architecture from [Releases](https://github.com/PereLV/Llampec/releases)
and extract it to a permanent folder.

Open `Llampec.exe`, click its notification-area icon, or press **Ctrl+Alt+Space**.
Click outside the panel or press **Escape** to close it. Right-click the icon for
the menu; the gear opens Settings. Use `Llampec.exe --background` to start silently.

Each split button has an arrow for its options. For light/dark scheduling, choose
fixed hours or sunrise/sunset and select **Apply**. Solar scheduling accepts manual
coordinates or an explicit **Use current location** request. Llampec must remain
running for scheduled changes; it does not wake the computer. A manual theme change
lasts until the next scheduled transition.

**Screenshot** waits until Llampec's panel is hidden before sending **Win+Shift+S**,
so its panel is not part of the selection. Windows handles the capture selector,
clipboard and subsequent editing or saving. Llampec does not store or upload the
image. See [screenshot behavior](docs/SCREENSHOT.md).

For Logitech MX, open **Settings → Logitech MX mouse**, find and select the mouse,
enable the module, then assign shortcuts and press **Apply**. DPI and wheel settings
are optional. The selected device's advertised capabilities determine what is
available; other mice retain their behavior. Disabling the module or exiting normally
restores the settings Llampec changed. [Setup and recovery](docs/LOGITECH.md) explains
connection recovery and the hardware combinations tested.

Caffeine supports indefinite sessions, 1/2/3-hour presets and custom durations of
1–1440 minutes. Closing the panel keeps the session active. Expiry, manual sleep or
exiting the application ends it; sessions are not restored on restart. Windows
power policy may limit power requests, particularly on battery and Modern Standby.

Always on Top uses **Ctrl+Alt+T** by default to pin or unpin the active window. The
tile acts on the window used before opening Llampec; its subtitle identifies that
target, and a separate count shows all windows pinned by Llampec. Its arrow opens
the window selector, **Unpin all**, **Appearance** (border on/off and thickness),
and shortcut preferences. Existing topmost windows owned by other applications
are not taken over. Normal exit removes Llampec's pins; closing the panel does not.
The shortcut works directly, including with ordinary application dialogs. The
border follows standard Windows 11 rounded corners and uses a square inner border
for maximized or snapped windows. Applications with custom window shapes may differ.
To pin an elevated application (such as Task Manager), Llampec needs administrator
permissions too. A permissions error offers **Restart as administrator**, using the
normal Windows UAC prompt. After restarting, activate the target and press the
shortcut again. Cancelling UAC leaves the current Llampec instance running.

Preferences are stored in `%LOCALAPPDATA%\Llampec\settings.json`. Llampec has no
telemetry or application-owned network requests. Location is requested only through
the location button; Windows location providers may use the network. Saved
coordinates are used for offline solar calculations.

## Build and run

Development requires the .NET 10 SDK and the Windows build tools required by
Windows App SDK. Dependencies are pinned in the project and restored from NuGet.

```powershell
dotnet build Llampec.sln
dotnet run --project src/Llampec.App
```

The default build uses the SDK's native architecture. Create a distributable package
for either target:

```powershell
./tools/package.ps1 -RuntimeIdentifier win-arm64
./tools/package.ps1 -RuntimeIdentifier win-x64
```

The script publishes Release, verifies the executable and required WinUI PRI/XBF
resources, includes the README, license and dependency notices, and creates
`publish/Llampec-0.2.0-alpha.5-<architecture>.zip`. It refuses to reuse an existing
output folder. Keep the complete extracted folder together.

For a development publish without packaging:

```powershell
dotnet publish src/Llampec.App -c Release -r win-arm64 --self-contained false -o publish/win-arm64
./tools/test-publish.ps1 -PublishDirectory publish/win-arm64
```

Use `win-x64` instead for x64.

## Verification and development

Run the tests used by CI without accessing the active display setup:

```powershell
dotnet test tests/Llampec.Core.Tests -c Release --filter "FullyQualifiedName!~ActionCatalogTests&FullyQualifiedName!~HdrDisplaysTests&FullyQualifiedName!~ProjectionModesTests"
```

The excluded integration tests inspect the real Windows session, and
`ProjectionModesTests` reapplies the current display topology. Run those tests
explicitly on suitable hardware.

Validation on 2026-09-21: 244 tests passed with that filter, plus six read-only
action-catalog integration tests on the local Windows session (250 in total).
Hardware and manual coverage are recorded in the feature documents below.

HDR's per-monitor list is captured at startup. Restart Llampec after connecting a
new monitor to add it to that list; unplugged monitors are disabled. ARM64 and x64
packages are built separately. Current Logitech hardware coverage is MX Master 3S
over Bluetooth on Windows ARM64; receiver connections and other MX models need
their own checks.

`--diagnostics` enables a local log for the session. `--theme=dark` or `--theme=light`
previews a panel theme on a fresh launch without changing the saved preference.
See [architecture and diagnostic options](docs/ARCHITECTURE.md) for details.

Developer documentation:

- [Adding an action](docs/ADDING_AN_ACTION.md)
- [Theme scheduling](docs/THEME_SCHEDULE.md)
- [Caffeine mode](docs/CAFFEINE.md)
- [Settings and languages](docs/SETTINGS.md)
- [Always on Top](docs/ALWAYS_ON_TOP.md)
- [Screenshot](docs/SCREENSHOT.md)
- [Logitech MX diagnostic probe](docs/LOGITECH_PROTOTYPE.md)
- [Logitech MX settings and service](docs/LOGITECH.md)
- [Acknowledgements](docs/ACKNOWLEDGEMENTS.md)

## License

[MIT](LICENSE) · Copyright © 2026 PereLV.
