# Panel layout

The original layout was integrated on 2026-10-05; its scroll and taskbar-spacing
corrections were validated on 2026-10-06. The approved visual editor and dynamic
module lifecycle were integrated into the development source on 2026-10-06.
Final ARM64/x64 Debug builds and mouse/keyboard editor checks completed on
2026-10-06. Physical touch-drag validation remains pending.

## Columns and categories

The panel supports **1–6 columns**, with **3** as the default. The main panel grows
with the chosen count and reduces the effective count when the monitor cannot
fit it. One-column layouts retain enough width for the footer; options pages keep
their usual width. No categories are created by default.

The pencil in the main panel opens a visual editor drawn like the panel it edits.
Each group is a block (card), and each button has the same face, split options
chevron and caption as in the ordinary panel; editor buttons never run their
actions. A small circular **×** at each button's top-left corner disables that
module. The fixed header contains the column selector and category creation;
**Save** and **Cancel** remain in the fixed footer. The central viewport scrolls
within the actual monitor-limited height.

Following the Windows Quick Settings editing pattern, a button is dragged by its
own face and a category by its heading; there are no separate drag handles. While
dragging, the item floats under the pointer and an empty slot shows where it will
land; the other buttons or blocks reflow around it with a reposition animation.
Releasing glides the item into its slot for 150 ms and then commits that position
to the draft (immediately when Windows animations are off); Escape or a lost
pointer before release restores the previous position. Holding an item in the
top or bottom 36 DIP of the viewport keeps scrolling, faster deeper in the band,
until the pointer leaves it or the view reaches its end. Categories can also be created, renamed and removed.
For keyboard users, Enter, Space, Shift+F10 or the context-menu key opens a
button's options (move before/after, move to category, disable), and Delete
disables it. Each category block keeps its **⋯** menu (move up/down, rename,
delete). Empty categories remain visible as drop destinations in the editor, and
the **No category** block always contains **+ Add module**; like the ordinary
panel, its heading is shown only when categories exist. The ordinary panel omits
empty headings. Removing a category appends its icons to the end of No category
in their current visual order.

`PanelLayoutDraft` keeps columns, category names and membership, global icon order,
and module choices independent from saved settings until Save. Cancel discards
that draft. Category identities remain stable when renamed. Hiding the panel
retains only draft data; disposable editor controls and menus are released and
rebuilt on reopening. Escape cancels an in-progress drag first; otherwise it hides
the editor while retaining the draft.

Button and category views are created once per editor session and reused: a drag
preview, menu move or module change only re-places them in their grids, so menus
are shared and filled when opened. A press records its position; pointer capture
on the editor starts only after an 8 DIP movement threshold, which leaves a
stationary touch free to open the context menu. Destinations come from layout
slots (column/row geometry for buttons, other blocks' centres for categories),
never from in-flight animation positions, and reflow only moves other items away
from the pointer, so the destination cannot oscillate. Pressed views opt out of
touch panning; the rest of each block still pans. The panel keeps its size during
a drag, because resizing the bottom-anchored window would move content under the
pointer; it is repositioned once on release. Pointer moves and viewport scroll
events update the preview. Edge scrolling uses a 16 ms timer that exists only
while the pointer rests in an edge band during a drag, and settling uses one
150 ms one-shot timer. There is no system drag payload or idle polling.

The panel's elevation shadow is cast by a separate non-interactive layer. A
`Translation` Z offset on the interactive surface displaced WinUI hit testing from
the drawn controls (by roughly 6–16 DIP at the measured upper-left button), which made
small targets such as the **×** unreliable and also affected the ordinary panel;
measurements and the corrected behaviour are recorded under Validation.

WinUI's documented built-in reordering does not support grouped items, so this
small eleven-module editor uses local pointer capture and direct draft moves
rather than grouped `ListViewBase.CanReorderItems`. See [Microsoft's reordering
contract](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.listviewbase.canreorderitems?view=windows-app-sdk-2.0).

## Module choices and saving

Disabling an icon is a draft change until Save. A saved disabled module is removed
from the panel and its action, owned services, shortcuts and event subscriptions
are released. **+ Add module** reads the static `ModuleCatalog` metadata, including
inactive modules, without constructing them or checking system availability.
Enabling one appends it to the end of No category.

`DisabledModules` records explicit service shutdown independently from legacy
`HiddenTiles`. Opening or saving older hidden-button settings never turns those
buttons into disabled services. They stay hidden until explicitly enabled through
the module chooser. Enabling removes both kinds of visibility flag; saved module
preferences, such as shortcuts, schedules and appearance, are retained. Previous
pin, fullscreen and caffeine sessions are not recreated. A newly enabled theme
module uses its saved schedule.

`App.Modules` serializes application of a draft. Before unloading, it restores
session-owned pins and fullscreen windows and retries pending rotation recovery.
If restoration fails, the module remains enabled and retains recovery ownership;
the draft stays available for another attempt. Settings are committed before
unloading the prepared modules. Save failures roll back panel preferences and
release newly constructed modules. Theme shutdown drains any in-flight scheduled
application, and caffeine disposal releases its power request.

`TileColumns`, `TileCategories`, `TileOrder`, `HiddenTiles` and `DisabledModules`
use the existing per-user settings JSON. Null collections and malformed ids are
normalized; old settings keep three columns and their saved layout. Applying a
draft copies only these panel preferences and shares no mutable lists with it.
Shared tray and `SystemEvents` integration stay alive. The Logitech service belongs
to the MX mouse module: removing it restores the mouse and releases the service.

## Taskbar clearance

Panel placement reserves the band of an expanded primary or secondary taskbar,
including conventional auto-hide. Per-monitor appbar queries associate hidden
bars with the correct monitor, even when their HWND extends beyond its edge.
`ABM_GETTASKBARPOS` provides its rectangle; the code does not rely on its
undocumented output edge field. Explorer's outer tray rectangle can include a
transparent band. When available, the native task-button container supplies the
expanded thickness, validated as inside the host, at least 32 DIP, plausibly
oriented and anchored to the same edge. The primary lookup uses
`ReBarWindow32`/`MSTaskSwWClass`; the secondary lookup uses
`WorkerW`/`MSTaskListWClass`. These class paths are Explorer implementation details,
not a public Windows contract. Missing or invalid geometry retains the full host
or primary appbar rectangle as a conservative fallback.

Each tray HWND supplies one selected rectangle, so an accurate button measurement
cannot subsequently lose to its padded host. Measurements are combined by edge
before applying a conservative DPI-scaled allowance when only a collapsed strip
is available. A saved touch-taskbar preference does not determine the current
posture or enlarge a conventional taskbar while a keyboard is attached.
Monitors without a taskbar retain their work area. Existing work-area reservations
are combined with the taskbar band, rather than subtracted a second time.

The visible surface also keeps its usual 12 DIP margin. These queries happen
when positioning the panel and introduce no idle timer, shell hook or preference
change. Windows owns mouse and touch taskbar reveal.

On the user's 2880-by-1920 display at 200% scaling with conventional auto-hide,
the primary appbar and hidden host reported 144 pixels but the task-button
container measured 96 pixels. The corrected reserve ends at y=1824 and the visible
panel ends at y=1800: its usual 24-pixel (12 DIP) margin, removing the extra 48
pixels seen in the reported screenshot. DWM frame bounds, client bounds and the
window region did not distinguish this transparent padding; exploratory queries
were removed after measurement.

Fullscreen mouse-edge detection also asks Windows for the registered auto-hidden
bar on its monitor. This avoids losing the bar when Windows slides its HWND into
the bounds of the next screen in a vertically stacked monitor arrangement.
The normal-taskbar fallback remains in place, and these queries occur only during
an active fullscreen session.

## Resource review

Layout and editor controls are built on demand and released by the existing
hidden-panel lifecycle; retaining a draft does not retain its control tree.
Size-change requests are coalesced into one queued layout pass. That pass reuses
its monitor/work-area measurement instead of querying again for column calculation
and tile construction. Module metadata is static, and disabled modules do not
keep their action/service instances or shortcuts alive. No editor idle timer,
system hook or recurring availability scan is added.

The active fullscreen poller now checks native ownership, visibility, minimization
and cloaking directly. It no longer reads process metadata and window captions on
every tick. Foreground/title events still update app eligibility and displayed
names. No PID cache or new recurring background task was added.

Before this change, ten samples of the existing hidden development instance
reported **14.2 MB private working set**, **26.4 MB total working set**, and
**0.01 CPU seconds over 18.1 elapsed seconds**. This is one local measurement;
physical working set and private committed memory are different quantities.
After exercising settings, changing to four columns, creating and assigning a
category, saving it and opening About, ten hidden-panel samples reported
**18.1 MB average private working set** (17.5–18.5 MB), **29.2 MB average total
working set**, **115.4 MB private committed memory**, and **0.08 CPU seconds over
14.8 elapsed seconds**. Both runs used the ARM64 Debug build on this machine;
the first was an already-idle instance and the second followed UI automation,
so they do not isolate the cost of each change. The observation remains within
the user's acceptable roughly 18 MB physical-memory range; it is not a general
memory or leak guarantee. CSV samples are in the local ignored `artifacts` folder.
These samples predate the visual editor and dynamic module unloading.

After opening the final visual editor, dragging, cancelling and hiding with
Escape, ten new samples of the hidden ARM64 Debug instance with **all eleven
modules enabled** reported **10.18 MiB mean private working set**
(9.406–10.84 MiB), **26.12 MiB mean total working set** and **88.81 MiB mean
private committed memory**. The process used **0.047 CPU seconds over 28.18
elapsed seconds**. Sampling requested a two-second interval; CIM query overhead
is included in the actual elapsed time. The local ignored CSV is
`artifacts/visual-editor-idle-final.csv`.

This is a measurement on this machine after that workload, not a controlled
comparison with the preceding settings-page run. It establishes neither a
cross-device memory guarantee nor the isolated cost of editor or module changes.
Resident physical memory and private committed memory remain distinct measures.

## Validation

Simplified editor with live drag preview (2026-10-06):

- ARM64 and x64 Debug builds completed with **zero warnings and errors**; Core
  tests passed (**481 passed, 1 opt-in native fullscreen test skipped**). After
  the settling, edge-scrolling and idle-hook changes, **486 passed** and the opt-in
  native fullscreen smoke test passed when enabled.
- Holding a dragged button at the bottom edge of a one-column editor scrolled to
  the end within about 1.4 s, with the slot following into No category; the
  release settled it there. Cancel kept the saved layout.
- Synthetic mouse checks on the user's four-column, three-category layout moved a
  button between categories with a visible floating button and slot, moved a whole
  category above the others by its heading, cancelled a drag with Escape while the
  button was held, removed a button with its **×** and restored it from **+ Add
  module** at the end of No category. Enter on a focused button opened its options
  with Move before disabled for the first button. Cancel restored the saved layout.
- Before the shadow-layer change, logged press positions showed a stationary offset
  between drawing and hit testing: a press on the drawn **×** at (4, 2.5) DIP inside
  a button reached the category block, while (100, 50) DIP — outside the 94-DIP
  button — reached the button. With the surface at Z = 0 the same presses reached
  the **×**, the face and the block respectively; the shadow remained visible.
- After two drags, Cancel and hiding, ten settled samples of the hidden ARM64 Debug
  instance with all eleven modules enabled reported **13.1 MB private working set**,
  **28.4 MB total working set**, **82.7 MB private committed memory** and no CPU
  time over 14 seconds (`artifacts/visual-editor-v2-idle-settled.csv`). This is a
  single local measurement, not a controlled comparison or cross-device guarantee.
- Physical touchscreen dragging and the long-press context menu remain untested.

Earlier visual-editor integration:

- The latest Core run for the visual-editor integration passed **482 tests**,
  including the opt-in native fullscreen smoke test.
  Coverage includes moves within and between groups, empty drop destinations,
  category removal order, explicit module flags, legacy hidden-button migration,
  independent draft/save references and preservation of module configuration.
- Final ARM64 and x64 Debug builds completed with **zero warnings and errors**.
- Initial GUI checks opened the pencil editor with the user's four columns and
  three categories, verified responsive header/footer layouts at one and six
  columns, and confirmed Cancel retained the saved four-column layout. An earlier
  Escape check hid the editor and reopened the retained draft.
- Local pointer checks moved Always on Top before Theme, moved the whole Finestres
  category before Sistema, and moved Caffeine between categories before the
  taskbar button. Enter created the temporary Prova category, and HDR was dropped
  into that empty category. Cancel retained the original layout. The final
  insertion-overlay build also verified dragging Caffeine before Theme and
  cancelling back to the saved layout.
- Saving with caffeine, fullscreen, Always on Top, rotation and theme disabled
  released their runtime service fields; pin/fullscreen shortcut ids were zero and
  the fullscreen timer was absent. The **+ Add module** menu still listed all five
  inactive modules from metadata. Enabling caffeine added it at the end of No
  category and saving recreated the module in its off state, without an old session.
- After normal application exit, only the five panel-layout preference fields
  were restored from the saved originals through the CLI and compared exactly.
  The final ARM64 instance was launched with `--background`, without
  `--diagnostics`, and all eleven modules enabled. GitHub publication was not
  performed.
- Pointer/keyboard checks do not verify physical touchscreen dragging, which
  remains untested. Fullscreen hardware limitations retain their separate
  validation status in [fullscreen behavior](FULLSCREEN.md).

The preceding layout and spacing implementation had the following validation;
these checks do not verify the new visual editor:

- ARM64 and x64 app builds completed with no warnings or errors.
- All 464 core tests passed, including the opt-in native fullscreen smoke test.
- Layout tests cover old/invalid settings, category drafts and persistence,
  hidden/new buttons, and ordering. Taskbar geometry tests cover all four edges,
  negative coordinates, mixed DPI, collapsed touch bars, secondary monitors and
  hidden bars that move into a neighboring screen with no local taskbar.
  Button-container tests cover the measured 144/96-pixel case, hidden hosts,
  all edges, different measured thicknesses and conservative malformed/missing
  geometry fallbacks.
- Visual checks confirmed the four-column layout, category creation/assignment
  and Save, and About's full name, MIT text, source link and third-party credits.
  The original three-column layout, categories and custom button order were
  restored after the temporary UI checks.
- The follow-up visual check scrolled over organizer rows to reach the last
  Fullscreen row and Save/Cancel with the footer visible. Native geometry logs
  confirmed the corrected 96-pixel taskbar reserve. The user's subsequently saved
  three categories and eleven-button order were preserved, and the final app was
  relaunched without the temporary diagnostics flag.

## Attribution

Copyright and author metadata now identify **Pere Esquerdo Ramis**. About explicitly
labels Llampec open-source under MIT and links its source on GitHub. README and the
project's MIT notice use the same name. Third-party notices are retained.
Repository, project URL and SPDX license-expression metadata follow
[Microsoft's package metadata guidance](https://learn.microsoft.com/en-us/nuget/create-packages/package-authoring-best-practices).
Publishing to GitHub was deferred; `AGENTS.md` records the attribution for that
future update.
