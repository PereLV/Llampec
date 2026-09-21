# Settings and languages

The gear opens Settings inside the flyout. It is also available from the tray's
context menu. Settings controls are disposed when the panel closes; optional
device services have an independent lifetime in the application.

- Language: Spanish, Catalan/Valencian, English, or follow Windows. Changes apply
  immediately and persist in settings.json. Titles, statuses, navigation, settings,
  reordering and the theme schedule use the shared UiText catalogue. Monitor names
  and diagnostic/system error details are preserved. Root.Language supplies the
  chosen language to native controls; date/number formatting follows the user locale.
- Startup: registers the quoted executable path with --background in HKCU Run,
  for the current user only, without elevation. This portable/unpackaged build has
  no package identity for a packaged StartupTask. Windows Startup Apps can independently
  disable execution; the UI links there and does not override that decision. After
  moving or updating the portable folder, open the new executable once: a successful
  manual launch refreshes an existing registration to its current location. This
  does not opt users in when no registration exists and leaves Windows' separate
  startup approval unchanged. Background and exit invocations never refresh it.
- Reorder: a separate native ListView supports drag-and-drop and explicit Up/Down
  buttons. It edits stable ids in a draft. Save writes the order and rebuilds the grid;
  Cancel/Back/dismissal discards it. There are no action buttons in the editing list.
  Stale/duplicate ids are ignored and new actions are appended in catalogue order.
  Version 0.2.0-alpha.5 adds Screenshot to the eight-action catalogue; existing
  saved ordering is retained and the new action is appended when not already listed.
- Logitech MX: an optional selected-device service for button shortcuts, sensor DPI,
  SmartShift and native scroll direction. Scan/select and edit a draft, then Apply;
  opening the page does not alter the mouse. Empty button assignments preserve
  their original behavior. The enabled service survives panel closure and restores
  owned changes on disable/normal exit. See [lifetime and recovery](LOGITECH.md).
  The page displays the reported DPI range/steps and advertised buttons, keeps
  disconnected-device preferences, and offers Reconnect for the saved configuration.

Language/order saves roll back on persistence errors. Startup checks that the actual
registration matches this executable and its background argument, rather than treating
any stale entry as enabled or storing an independent boolean in JSON. Registry handles
are disposed immediately. Page controls and their handlers are released on navigation
or closing; existing delayed idle cleanup remains responsible for returning pages.

Core tests cover language fallback/catalogue, order normalization, command quoting
and startup enable/disable and relocation in isolated registry keys. They do not change the real
login registration. Manual checks should include language changes, Save/Cancel,
restart persistence and an actual sign-in with startup enabled. Technical error
details from Windows may remain in the system's language.

References: [Run registration](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys),
[native reordering](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.listviewbase.canreorderitems),
[Windows settings links](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings).
