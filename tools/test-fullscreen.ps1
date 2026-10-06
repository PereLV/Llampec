# Temporary Win32 experiment; this does not implement a Llampec action.
# Run -SelfTest to exercise only synthetic windows owned by this process.
# Use -KeepTaskbar to test mouse auto-hide with the documented NonRudeHWND property.
# Run in a fresh powershell.exe process so Add-Type does not reuse an earlier version.
[CmdletBinding()]
param(
    [ValidateRange(1, 30)][int]$DelaySeconds = 5,
    [ValidateRange(1, 120)][int]$DurationSeconds = 20,
    [switch]$KeepTaskbar,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

if (-not ('LlampecFullscreenTrial' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class LlampecFullscreenTrial
{
    const int StyleIndex = -16, ExStyleIndex = -20;
    const uint FrameStyles = 0x00CF0000, MaximizedStyle = 0x01000000;
    const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 0x10;
    const uint FrameChanged = 0x20, NoOwnerZOrder = 0x200;
    const string NonRudeProperty = "NonRudeHWND";
    static readonly IntPtr NonRudeValue = new IntPtr(1);

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
        public bool EqualsRect(Rect r)
        { return Left == r.Left && Top == r.Top && Right == r.Right && Bottom == r.Bottom; }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Placement
    {
        public uint Length, Flags, ShowCmd;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }

    sealed class Trial
    {
        readonly IntPtr window;
        readonly uint processId, threadId, originalStyle;
        readonly Placement originalPlacement;
        readonly Rect monitor;
        readonly bool keepTaskbar;
        readonly IntPtr originalNonRude;
        readonly string property = "Llampec.FullscreenTrial." + Guid.NewGuid().ToString("N");
        readonly IntPtr token = new IntPtr(Process.GetCurrentProcess().Id);
        bool claimed, restored, nonRudeApplied;

        public Trial(IntPtr target, bool preserveTaskbar)
        {
            window = target;
            keepTaskbar = preserveTaskbar;
            if (!IsWindow(window) || !IsWindowVisible(window) || IsHungAppWindow(window))
                throw new InvalidOperationException("The target window is not available or responding.");
            threadId = GetWindowThreadProcessId(window, out processId);
            if (threadId == 0) throw new Win32Exception();
            originalStyle = ReadStyle(window, StyleIndex);
            if ((originalStyle & FrameStyles) == 0)
                throw new InvalidOperationException("This window already has no standard frame. Skip this trial.");
            originalPlacement = ReadPlacement(window);
            MonitorInfo info = new MonitorInfo();
            info.Size = (uint)Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) throw new Win32Exception();
            monitor = info.Monitor; // Capture before restoring a maximized window.
            originalNonRude = GetProp(window, NonRudeProperty);
            if (!SetProp(window, property, token)) throw new Win32Exception();
            claimed = true;
        }

        bool OwnsWindow()
        {
            uint currentProcess;
            return claimed && IsWindow(window)
                && GetWindowThreadProcessId(window, out currentProcess) == threadId
                && currentProcess == processId && GetProp(window, property) == token;
        }

        void RequireOwnership()
        {
            if (!OwnsWindow()) throw new InvalidOperationException("The target window closed or ownership changed.");
        }

        public void Enter(bool failAfterStyle)
        {
            RequireOwnership();
            if (keepTaskbar)
            {
                // Documented by ITaskbarList2::MarkFullscreenWindow. Prevents
                // Shell fullscreen detection from lowering the auto-hide taskbar.
                // Set before the show/size transition; existing visible apps
                // still need a real mouse-hover trial, beyond the synthetic test.
                if (!SetProp(window, NonRudeProperty, NonRudeValue)) throw new Win32Exception();
                nonRudeApplied = true;
            }
            ShowWindow(window, 9); // SW_RESTORE: do not retain maximized client-area rules.
            RequireOwnership();
            WriteStyle(window, ReadStyle(window, StyleIndex) & ~(FrameStyles | MaximizedStyle));
            if (failAfterStyle) throw new InvalidOperationException("Synthetic failure after changing style.");
            RequireOwnership();
            if (!SetWindowPos(window, IntPtr.Zero, monitor.Left, monitor.Top,
                monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
                NoZOrder | NoOwnerZOrder | NoActivate | FrameChanged)) throw new Win32Exception();
            RequireOwnership();
            Rect actual;
            if (!GetWindowRect(window, out actual)) throw new Win32Exception();
            if (!actual.EqualsRect(monitor))
                throw new InvalidOperationException("This application did not accept the full monitor bounds.");
        }

        public bool Restore()
        {
            if (restored || !claimed) return restored;
            if (!OwnsWindow())
            {
                restored = true; // Never touch a closed/recycled HWND or another actor's claim.
                Console.WriteLine("Target closed or ownership changed; restoration skipped.");
                return false;
            }
            var failures = new List<string>();
            if (nonRudeApplied)
            {
                try
                {
                    // Restore before changing geometry so Shell can reevaluate
                    // the window using its original policy. Do not overwrite
                    // a concurrent change by the app or another utility.
                    if (GetProp(window, NonRudeProperty) != NonRudeValue)
                        throw new InvalidOperationException("The application's taskbar property changed during the trial; its new value was preserved.");
                    if (originalNonRude == IntPtr.Zero)
                        RemoveProp(window, NonRudeProperty);
                    else if (!SetProp(window, NonRudeProperty, originalNonRude))
                        throw new Win32Exception();
                    if (GetProp(window, NonRudeProperty) != originalNonRude)
                        throw new InvalidOperationException("Could not restore the original taskbar property.");
                    nonRudeApplied = false;
                }
                catch (Exception error) { failures.Add(error.Message); }
            }
            // Let SetWindowPlacement restore show state. Setting WS_MAXIMIZE here
            // can falsely mark a normal-sized window as already maximized.
            try { WriteStyle(window, originalStyle & ~(MaximizedStyle | 0x20000000)); }
            catch (Exception error) { failures.Add(error.Message); }
            if (OwnsWindow())
            {
                Placement placement = originalPlacement;
                if (!SetWindowPlacement(window, ref placement)) failures.Add(new Win32Exception().Message);
            }
            if (OwnsWindow() && !SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                NoMove | NoSize | NoZOrder | NoOwnerZOrder | NoActivate | FrameChanged))
                failures.Add(new Win32Exception().Message);
            bool stillOwned = OwnsWindow();
            if (stillOwned)
            {
                try
                {
                    Placement actual = ReadPlacement(window);
                    if (ReadStyle(window, StyleIndex) != originalStyle
                        || actual.ShowCmd != originalPlacement.ShowCmd
                        || !actual.NormalPosition.EqualsRect(originalPlacement.NormalPosition))
                        failures.Add("The application did not retain its original frame/window placement.");
                }
                catch (Exception error) { failures.Add(error.Message); }
                RemoveProp(window, property);
            }
            claimed = false;
            restored = true;
            if (failures.Count != 0)
                throw new InvalidOperationException("Could not fully restore the window: " + string.Join("; ", failures.ToArray()));
            return stillOwned;
        }
    }

    public static void Run(int durationSeconds, bool keepTaskbar)
    {
        IntPtr previousDpi = EnterDpiContext();
        try
        {
            IntPtr target = GetAncestor(GetForegroundWindow(), 2);
            RejectShellOrConsole(target);
            Trial trial = new Trial(target, keepTaskbar);
            bool windowRestored = false;
            try
            {
                trial.Enter(false);
                Console.WriteLine("Fullscreen trial active for {0} seconds; then the window will restore automatically.", durationSeconds);
                Thread.Sleep(durationSeconds * 1000);
            }
            finally { windowRestored = trial.Restore(); }
            if (windowRestored) Console.WriteLine("Original frame and window placement restored.");
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    public static void SelfTest()
    {
        IntPtr previousDpi = EnterDpiContext();
        IntPtr window = IntPtr.Zero;
        try
        {
            window = CreateWindowEx(0, "STATIC", "Llampec synthetic fullscreen test",
                0x10CF0000, 120, 140, 520, 350, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception();
            CheckRoundTrip(window, false, false, false);
            CheckRoundTrip(window, true, false, false);
            CheckRoundTrip(window, true, true, false);
            CheckRoundTrip(window, false, false, true);
            CheckRoundTrip(window, true, false, true);
            CheckRoundTrip(window, true, true, true);
            // Preserve a preexisting property, including values other than TRUE.
            IntPtr existing = new IntPtr(7);
            if (!SetProp(window, NonRudeProperty, existing)) throw new Win32Exception();
            CheckRoundTrip(window, true, false, true);
            CheckRoundTrip(window, true, true, true);
            if (GetProp(window, NonRudeProperty) != existing)
                throw new InvalidOperationException("SelfTest: preexisting taskbar property was not preserved.");
            RemoveProp(window, NonRudeProperty);
            Console.WriteLine("PASS: normal/maximized fullscreen and rollback; frame, placement, bounds, extended styles and taskbar property restored. Mouse auto-hide still requires a real application trial.");
        }
        finally
        {
            if (window != IntPtr.Zero) DestroyWindow(window);
            SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    static void CheckRoundTrip(IntPtr window, bool maximized, bool failAfterStyle, bool keepTaskbar)
    {
        ShowWindow(window, maximized ? 3 : 9);
        uint style = ReadStyle(window, StyleIndex), exStyle = ReadStyle(window, ExStyleIndex);
        Placement placement = ReadPlacement(window);
        Rect bounds;
        if (!GetWindowRect(window, out bounds)) throw new Win32Exception();
        IntPtr originalNonRude = GetProp(window, NonRudeProperty);
        Trial trial = new Trial(window, keepTaskbar);
        bool expectedFailure = false;
        try
        {
            trial.Enter(failAfterStyle);
            if (IsZoomed(window) || (ReadStyle(window, StyleIndex) & FrameStyles) != 0)
                throw new InvalidOperationException("SelfTest: fullscreen frame/state mismatch.");
            if (GetProp(window, NonRudeProperty) != (keepTaskbar ? NonRudeValue : originalNonRude))
                throw new InvalidOperationException("SelfTest: taskbar property mismatch while fullscreen.");
        }
        catch (InvalidOperationException error)
        {
            if (!failAfterStyle || error.Message != "Synthetic failure after changing style.") throw;
            expectedFailure = true;
        }
        finally { trial.Restore(); }
        Placement after = ReadPlacement(window);
        Rect afterBounds;
        if (!GetWindowRect(window, out afterBounds)) throw new Win32Exception();
        if (GetProp(window, NonRudeProperty) != originalNonRude)
            throw new InvalidOperationException("SelfTest: taskbar property restore mismatch.");
        if (ReadStyle(window, StyleIndex) != style || ReadStyle(window, ExStyleIndex) != exStyle
            || after.ShowCmd != placement.ShowCmd || !after.NormalPosition.EqualsRect(placement.NormalPosition)
            || !afterBounds.EqualsRect(bounds) || (failAfterStyle && !expectedFailure))
            throw new InvalidOperationException(string.Format(
                "SelfTest restore mismatch (maximized={0}, rollback={1}): style {2:X8}/{3:X8}, exStyle {4:X8}/{5:X8}, show {6}/{7}, normal [{8},{9},{10},{11}]/[{12},{13},{14},{15}], bounds [{16},{17},{18},{19}]/[{20},{21},{22},{23}].",
                maximized, failAfterStyle, style, ReadStyle(window, StyleIndex), exStyle, ReadStyle(window, ExStyleIndex),
                placement.ShowCmd, after.ShowCmd, placement.NormalPosition.Left, placement.NormalPosition.Top,
                placement.NormalPosition.Right, placement.NormalPosition.Bottom, after.NormalPosition.Left,
                after.NormalPosition.Top, after.NormalPosition.Right, after.NormalPosition.Bottom,
                bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
                afterBounds.Left, afterBounds.Top, afterBounds.Right, afterBounds.Bottom));
    }

    static void RejectShellOrConsole(IntPtr window)
    {
        uint processId;
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out processId) == 0)
            throw new InvalidOperationException("No foreground application selected.");
        string name = Process.GetProcessById((int)processId).ProcessName.ToLowerInvariant();
        StringBuilder className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        string cls = className.ToString();
        if (processId == Process.GetCurrentProcess().Id || cls == "Progman" || cls == "WorkerW"
            || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "#32769"
            || name == "powershell" || name == "pwsh" || name == "cmd" || name == "windowsterminal"
            || name == "openconsole" || name == "conhost")
            throw new InvalidOperationException("Select an application window during the countdown, not the desktop, taskbar or terminal.");
    }

    static IntPtr EnterDpiContext()
    {
        if (IntPtr.Size != 8) throw new InvalidOperationException("Use 64-bit PowerShell (ARM64 or x64).");
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new Win32Exception();
        return previous;
    }
    static uint ReadStyle(IntPtr window, int index)
    {
        SetLastError(0);
        IntPtr result = GetWindowLongPtr(window, index);
        if (result == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
        return unchecked((uint)result.ToInt64());
    }
    static void WriteStyle(IntPtr window, uint style)
    {
        SetLastError(0);
        IntPtr result = SetWindowLongPtr(window, StyleIndex, new IntPtr(unchecked((int)style)));
        if (result == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
    }
    static Placement ReadPlacement(IntPtr window)
    {
        Placement placement = new Placement();
        placement.Length = (uint)Marshal.SizeOf(typeof(Placement));
        if (!GetWindowPlacement(window, ref placement)) throw new Win32Exception();
        return placement;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetProp(IntPtr window, string name, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
}
'@
}

if ($SelfTest) {
    [LlampecFullscreenTrial]::SelfTest()
    return
}

Write-Host "Activa la ventana que quieras probar. La prueba dura $DurationSeconds segundos."
Write-Host 'No cierres PowerShell durante la prueba: la ventana se restaura al terminar.'
if ($KeepTaskbar) {
    Write-Host 'Prueba con NonRudeHWND: acerca el raton al borde de la barra de tareas oculta.'
}
for ($fullscreenCountdown = $DelaySeconds; $fullscreenCountdown -gt 0; $fullscreenCountdown--) {
    Write-Host "Inicio en $fullscreenCountdown..."
    Start-Sleep -Seconds 1
}
[LlampecFullscreenTrial]::Run($DurationSeconds, $KeepTaskbar.IsPresent)
