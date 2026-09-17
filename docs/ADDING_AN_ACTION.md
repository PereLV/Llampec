# Adding an action

Llampec uses a WinUI 3 frontend and a UI-independent Core project.
There is no plugin discovery or DI container.

1. Create a class under `src/Llampec.Core/Actions/<Name>/` deriving from `QuickActionBase`.
2. Implement stable `Id`, `Title`, `Glyph`, `Kind`, `Refresh()` and `ExecuteCoreAsync()`.
3. Register it in `ActionCatalog.Create`; catalog order is the default display order.
4. Test both system themes, keyboard navigation and multiple display scales.

## Contract

- Re-read actual system state in `Refresh`; do not assume a successful change.
- `ActionKind.Button` closes the panel before execution. Display power needs the
  existing short delay so the triggering input does not immediately wake it.
- `ToggleWithSubpage` exposes independent child switches. Set
  `SubActionsAreExclusive` for a radio-button list such as projection.
- `Subtitle` describes secondary state; `GlyphBadge` adds a small secondary glyph.
- Use documented Win32 APIs through `Interop`, or WinRT APIs through the Windows SDK.
- Observe each API's threading, permissions and package-identity requirements.
  In particular, location access must originate from a foreground user gesture.
- Do not introduce telemetry. Justify new dependencies and their maintenance cost.

The current view models snapshot child actions at construction. Dynamic hardware
discovery and child-list reconciliation remain a separate follow-up.
Some existing tests access real hardware; keep hardware-changing tests opt-in.
