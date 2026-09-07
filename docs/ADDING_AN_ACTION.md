# Adding an action (tile)

Llampec is deliberately simple: there is no plugin system, no reflection and no DI container. A tile is a class plus one line in the catalog.

## 1. Implement the action

Create a class under `src/Llampec.Core/Actions/<Name>/` deriving from `QuickActionBase`:

```csharp
using Llampec.Actions;

namespace Llampec.Actions.Example;

public sealed class ExampleAction : QuickActionBase
{
    public override string Id => "example";              // stable, never localized; used in settings
    public override string Title => "Example";           // localized later through Strings.resx
    public override string Glyph => "";           // a Segoe Fluent Icons code point
    public override ActionKind Kind => ActionKind.Toggle; // Button | Toggle | ToggleWithSubpage

    public override void Refresh()
    {
        // Re-read the real state from the system. Never assume it after ExecuteCoreAsync.
        State = ReadStateFromWindows() ? ActionState.On : ActionState.Off;
        IsAvailable = true; // false greys the tile out (e.g. no HDR-capable display)
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        // Do the work. Exceptions are caught and logged by the base class; the tile shows a busy state meanwhile.
        return Task.CompletedTask;
    }
}
```

Rules of thumb:

- All Win32 calls go through `Llampec.Core/Interop`, written from the Microsoft documentation. Keep declarations minimal.
- Never poll. If the state can change behind your back, subscribe to `SystemEvents` (display change, setting change) in the constructor and call `Refresh()`.
- Set `Subtitle` for a secondary line under the title (e.g. "2 of 3").
- For `ToggleWithSubpage`, override `SubActions` with the per-item actions; the primary toggle usually applies to all of them and reports `ActionState.Mixed` when they disagree.
- `ActionKind.Button` tiles close the panel before `ExecuteAsync` runs (like the native panel). If the action needs the panel to be really gone (e.g. turning the display off), add a short delay at the start of `ExecuteCoreAsync`, as `DisplayOffAction` does.
- No network, no telemetry, no third-party packages.

Existing actions to copy from: `DisplayOff/DisplayOffAction.cs` (a button) and `Taskbar/TaskbarAutoHideAction.cs` (a toggle that reads its state from the shell).

## 2. Register it

Add one line to `ActionCatalog.Create` in `src/Llampec.Core/Actions/ActionCatalog.cs`. The position there is the default tile order.

## 3. Test it

Run the app (`dotnet run --project src/Llampec.App`), open the panel from the tray icon or the hotkey (default `Ctrl+Alt+Space`) and check the tile in both light and dark themes, on a 100 % and a 200 % DPI display if you can.
