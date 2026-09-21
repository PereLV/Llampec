# Screenshot

The **Screenshot** tile (**Captura de pantalla** in Spanish and Catalan/Valencian)
opens Windows' native screen-capture selector with **Win+Shift+S**. It is one of
Llampec's eight quick actions and participates in the existing button ordering.
Its icon is a dotted selection frame with rounded corners and a plus sign.

## Behavior

Click the tile, then select the region or capture mode in Windows. Llampec waits
for its panel to finish hiding before sending the shortcut. Reopening the panel
or cancelling dismissal cancels that pending action. No animation-duration guess
or screenshot timer is used.

Windows owns the capture UI, clipboard contents, editing and saving. Llampec does
not read the captured pixels, create an image file or upload a screenshot. Capture
availability and the selector's options depend on the installed Windows components
and policy. The tile does not install or replace Snipping Tool.

The shared shortcut emitter skips input while a keyboard modifier or its target
key is already held. Release those keys before clicking the tile. Input is subject
to Windows integrity and desktop restrictions; Llampec does not force input into
a protected desktop.

## Implementation

- `Actions/Screenshot/ScreenshotAction`: stable ID `screenshot`, button action and
  one Win+Shift+S chord through `Platform.KeyboardShortcut`.
- `TileViewModel` passes the operation through
  `FlyoutViewModel.RunWithPanelHiddenRequested` and awaits the combined task.
- `FlyoutWindow.RunWithPanelHiddenAsync` waits until `FinishHide` has hidden the
  native window. It checks that the panel has not reopened and invokes the action
  in that same UI continuation; interrupted dismissal does not launch it.
- `FlyoutWindow.ScreenshotIcon` draws a vector selection frame and plus sign,
  inheriting the button's foreground for themes, high contrast and disabled state.

There is no new dependency, worker, hook, persistent capture state or periodic task.
The feature uses the same native Windows operation as pressing Win+Shift+S.

## Verification

On 2026-09-21, 244 Core tests passed with the CI hardware-safe filter, including
shared shortcut parsing and native input-layout checks. Six additional read-only
action-catalog integration tests passed on the local Windows session. These tests
do not inject capture input or verify the selector visually.

The dismissal path was reviewed for repeated activation, cancellation and reopening
between native hiding and shortcut delivery. Manual validation must also confirm
that Windows opens its selector after the panel has disappeared, that Escape cancels
a selection, and that capture and clipboard behavior remain under Windows' control.
Automated checks alone do not establish those visual and clipboard outcomes.
