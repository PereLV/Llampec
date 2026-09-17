# Llampec

A compact Windows 11 quick-settings companion by [PereLV](https://github.com/PereLV).

**0.2.0-alpha.1** is the first functional alpha baseline. Llampec lives in the
notification area and provides six configurable quick actions:

- **HDR:** switch all compatible displays or control each display separately.
- **Display power:** turn off the displays.
- **Light/dark mode:** change the Windows theme, with optional fixed-hour or
  sunrise/sunset scheduling and a countdown to the next change.
- **Multiple displays:** choose PC screen only, Duplicate, Extend or Second screen only.
- **Taskbar:** toggle automatic hiding.
- **Caffeine:** prevent automatic sleep for a chosen duration, optionally keeping
  the display on.

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

Caffeine supports indefinite sessions, 1/2/3-hour presets and custom durations of
1–1440 minutes. Closing the panel keeps the session active. Expiry, manual sleep or
exiting the application ends it; sessions are not restored on restart. Windows
power policy may limit power requests, particularly on battery and Modern Standby.

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
`publish/Llampec-0.2.0-alpha.1-<architecture>.zip`. It refuses to reuse an existing
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

`--diagnostics` enables a local log for the session. `--theme=dark` or `--theme=light`
previews a panel theme on a fresh launch without changing the saved preference.
See [architecture and diagnostic options](docs/ARCHITECTURE.md) for details.

Developer documentation:

- [Adding an action](docs/ADDING_AN_ACTION.md)
- [Theme scheduling](docs/THEME_SCHEDULE.md)
- [Caffeine mode](docs/CAFFEINE.md)
- [Settings and languages](docs/SETTINGS.md)

## License

[MIT](LICENSE) · Copyright © 2026 PereLV.
