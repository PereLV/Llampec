# Settings and languages

The gear opens Settings inside the flyout. It is also available from the tray's
context menu. No settings window or background worker is kept alive separately.

- Language: Spanish, Catalan/Valencian, English, or follow Windows. Changes apply
  immediately and persist in settings.json. Titles, statuses, navigation, settings,
  reordering and the theme schedule use the shared UiText catalogue. Monitor names
  and diagnostic/system error details are preserved. Root.Language supplies the
  chosen language to native controls; date/number formatting follows the user locale.
- Startup: registers the quoted executable path with --background in HKCU Run,
  for the current user only, without elevation. This portable/unpackaged build has
  no package identity for a packaged StartupTask. Windows Startup Apps can independently
  disable execution; the UI links there and does not override that decision. Moving
  the portable folder requires updating the registration from the new location.
- Reorder: a separate native ListView supports drag-and-drop and explicit Up/Down
  buttons. It edits stable ids in a draft. Save writes the order and rebuilds the grid;
  Cancel/Back/dismissal discards it. There are no action buttons in the editing list.
  Stale/duplicate ids are ignored and new actions are appended in catalogue order.

Language/order saves roll back on persistence errors. Startup reads the actual
registration, rather than storing an independent boolean in JSON. Registry handles
are disposed immediately. Page controls and their handlers are released on navigation
or closing; existing delayed idle cleanup remains responsible for returning pages.

Core tests cover language fallback/catalogue, order normalization, command quoting
and startup enable/disable in an isolated registry key. They do not change the real
login registration. Manual checks should include language changes, Save/Cancel,
restart persistence and an actual sign-in with startup enabled. Technical error
details from Windows may remain in the system's language.

References: [Run registration](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys),
[native reordering](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.listviewbase.canreorderitems),
[Windows settings links](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings).
