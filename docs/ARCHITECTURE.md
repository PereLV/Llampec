# Architecture

Llampec is an unpackaged .NET 10 application for Windows 11 24H2+, using WinUI 3
and Windows App SDK 2.4.0. The app is published for ARM64 and x64 with Windows App
SDK files included and a separate .NET Runtime requirement.

## Components

- `Llampec.Core`: action contracts, Windows interop, settings, localization and
  schedule calculations, independent of the UI framework.
- `Llampec.App`: WinUI panel, notification-area icon, global hotkey, single-instance
  activation and theme scheduler.
- `Llampec.Core.Tests`: deterministic logic tests and Windows integration tests.

`ActionCatalog` registers actions with stable identifiers. Preferences retain tile
order and visibility by identifier. Stateful services such as caffeine and theme
scheduling live outside page controls so that closing the panel does not stop them.

## Panel and motion

The visible panel is a rounded XAML surface containing `SystemBackdropElement`,
controls, outline and `ThemeShadow`. `TransparentPanelBackdrop` supplies a
transparent, borderless native host. The visible width is 360 DIP when space allows;
placement keeps the surface 12 DIP from the work-area right and bottom edges, with
additional transparent padding for the shadow. The opening monitor remains the
placement anchor, including during layout and DPI changes.

`PanelMotion` animates the surface's translation and opacity through Composition.
The host does not move or resize per animation frame. Entry uses a 320 ms eased
slide and a 70 ms fade; exit uses a 220 ms eased slide with a fade over its final
100 ms. Newly built content gets two render submissions before becoming visible.
Reversals start at the presented value and retain the current page. Disabled Windows
animations use the endpoint immediately.

A composition completion batch finishes each transition. A one-shot deadline
handles occluded or suspended rendering. Rendering handlers are used only for entry
preparation and are removed afterward. Queued page measurements wait until entry
finishes; progress indicators do not change tile geometry.

`PanelAcrylicBackdrop` keeps the material input-active through dismissal while the
native controller retains transparency, contrast and power-policy fallbacks.
Keep one backdrop assigned for the XAML connection's lifetime. Idle suspension
releases controllers on the UI thread but retains projected backdrop targets;
releasing those thread-affine links on a finalizer thread can cause a native failure.
The host filters frame-style changes because WinUI can reapply classic frame bits
during activation and DPI changes.

## Hidden lifetime

Closing hides the panel and disposes the current subpage. After two seconds hidden,
the app releases tile controls and suspends acrylic controllers, then requests a
nonblocking collection of discarded objects. A second one-shot delay of 500 ms
returns resident pages to Windows. Reopening cancels pending cleanup and rebuilds
the controls. Cleanup is deferred while an action is busy.

The host, tray integration, actions and scheduler remain alive. There is no periodic
memory-cleaning loop. Returning resident pages reduces the working set; it does not
unload the runtimes or release all private memory commitment.

## Diagnostics

`Llampec.exe --diagnostics` enables the local log for that session without changing
the saved logging preference. The log records preparation, composition completion
and hidden cleanup. `--theme=light` or `--theme=dark` previews the selected panel
theme on a fresh launch without changing the stored preference. If Llampec is
already running, another launch reopens that instance; command-line preview and
diagnostic options take effect only when starting a new process.

To sample memory and CPU:

```powershell
./tools/measure-memory.ps1 -Seconds 30 -ProcessId <PID> -OutputPath artifacts/memory.csv
```

`Seconds` specifies the sample count, with a one-second pause between samples;
the script reports actual elapsed time. Compare stable states under matching
architecture, display scale and workload. These samples do not measure GPU frame
pacing or establish a cross-device memory guarantee.

Before a release, check tray/hotkey activation, Escape/outside dismissal, both
themes, high contrast, reduced motion, disabled transparency, mixed-DPI placement,
idle reconstruction and the published app on each supported architecture. Validate
HDR and projection changes on appropriate physical hardware.

## References

- [Microsoft: element-scoped system materials](https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials)
- [Microsoft: system shadows](https://learn.microsoft.com/en-us/windows/apps/design/layout/depth-shadow)
- [WinUI system-backdrop element specification](https://github.com/microsoft/microsoft-ui-xaml/blob/main/specs/SystemBackdropElement/SystemBackdropElement_Spec.md)
- [PowerToys backdrop lifetime implementation](https://github.com/microsoft/PowerToys/blob/main/src/modules/cmdpal/Microsoft.CmdPal.UI/Controls/TintedBackdrops.cs)
- [Microsoft: EmptyWorkingSet semantics](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset)
