# Llampec

*Llampec* (Valencian for "lightning flash") is a tiny, open-source Quick Settings panel for Windows 11 with the toggles Microsoft doesn't let you add to the native one: HDR per monitor, turn off the display, dark/light theme, projection mode and taskbar auto-hide. It lives in the notification area, opens instantly and looks like it belongs.

> **Status:** early development. The skeleton (tray icon, panel, theming, hotkey) is in place; actions are being added one by one.

## Why

The native Win+A panel is a closed list. Injecting into Explorer (Windhawk-style mods) works but breaks with updates. Llampec is a separate, ordinary app: no injection, no admin rights, no services.

## Principles

- **Light.** Runs in the background permanently, so idle memory and CPU come first. Budget: **≤ 40 MB private working set, 0 % CPU when idle**; `tools/measure-memory.ps1` checks it.
- **Native-looking.** Acrylic backdrop, rounded corners, Fluent tiles and the system accent colour, in light and dark.
- **Private.** No telemetry, no analytics, no network code at all.
- **Small surface.** No third-party packages. Win32 calls are hand-written from Microsoft's documentation.
- **Easy to extend.** A new tile is one class and one line; see [docs/ADDING_AN_ACTION.md](docs/ADDING_AN_ACTION.md).

## Requirements

- Windows 11 24H2 (build 26100) or later, x64 or ARM64.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). `winget` installs it for you.

## Install

```powershell
winget install PereLV.Llampec
```

Or download the zip for your architecture from [Releases](https://github.com/PereLV/Llampec/releases), unzip anywhere and run `Llampec.exe`. No installer, no registry footprint beyond your settings and the optional "start with Windows" entry.

### About the SmartScreen warning

Llampec is not code-signed yet (certificates cost money; signing through an open-source programme is planned). On first run Windows may show *"Windows protected your PC"*: click **More info → Run anyway**. You can verify the download's SHA-256 against the value in the release notes.

## Use

- Left-click the tray icon or press **Ctrl+Alt+Space** (configurable) to open the panel; click outside or press Esc to close it.
- Right-click the tray icon for settings, "start with Windows", and exit.
- Settings live in `%LOCALAPPDATA%\Llampec\settings.json`.

## Build

```powershell
git clone https://github.com/PereLV/Llampec
cd Llampec
dotnet build
dotnet run --project src/Llampec.App
```

Publish for a specific architecture:

```powershell
dotnet publish src/Llampec.App -c Release -r win-arm64 --self-contained false -o publish/win-arm64
dotnet publish src/Llampec.App -c Release -r win-x64   --self-contained false -o publish/win-x64
```

## Architecture

```
src/Llampec.Core   logic, Win32 interop, settings — no UI dependencies (reusable by other front ends)
src/Llampec.App    WPF tray app: panel, tiles, themes
tests/             xunit tests for the pure logic
```

## License

[MIT](LICENSE).
