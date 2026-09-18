# Always on Top native smoke test

Run explicitly on an interactive Windows desktop:

```powershell
dotnet run --project tests/Llampec.AlwaysOnTop.Smoke -c Release
```

This console harness is intentionally outside the solution and CI. It opens
synthetic windows in two helper processes, changes only those windows, and
closes the helpers when finished. The windows appear briefly without requesting
activation. No installed Llampec instance or saved preferences are required or
modified.

The harness exercises the real Win32 service and native border with a message
loop. It checks enumeration, multiple pins, foreground preservation on pin/unpin,
border ownership and styles, hit-test/no-activation responses, hollow regions,
thickness and DPI geometry, appearance changes, movement/resizing,
minimizing/restoring, target destruction, normal-disposal cleanup, and preservation
of preexisting visible topmost owned palettes. Regression cases also check that
hidden topmost tooltips do not block pinning and retain their original topmost
state after unpinning the owner, captioned owned dialogs are eligible,
native child handles resolve to the dialog, and an inheriting dialog resolves to
the owner whose pin belongs to Llampec. These cases pass synthetic captured HWNDs
to the native resolver; they do not activate windows or send keyboard input.

At border thicknesses 1, 3 and 8 DIP, native region checks independently verify
that the painted stroke reaches all four frame edges after compensating for
DWM's native border thickness, with no gap before the hollow center. The middle
stays empty and each side's physical pixel width matches the selected thickness.

Success prints `PASS: 75 native checks` and exits with code 0. A failed check exits
with code 1. This checks native behavior; it does not automate or visually inspect
the WinUI panel, simulate physical mouse clicks, or cover every monitor setup.
