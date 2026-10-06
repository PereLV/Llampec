// Temporary Win32 prototype, not a production Llampec action.
// Only the target app's frame, geometry and explicit fullscreen marking are changed.
// No ABM_SETSTATE, taskbar preference writes, input injection, or shell-window mutations.
// Shrink-before-unmark follows the idea in Chromium's OnBackgroundFullscreen;
// applying it to mouse hover is an experiment, not a Windows guarantee.
// https://github.com/chromium/chromium/blob/main/ui/views/win/hwnd_message_handler.cc
// https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist2-markfullscreenwindow
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class LlampecFullscreenEdgeTrial
{
    const int StyleIndex = -16, ExStyleIndex = -20;
    const uint FrameStyles = 0x00CF0000, MaximizedStyle = 0x01000000;
    const uint FrameExStyles = 0x00020301; // DLGMODALFRAME, WINDOWEDGE, CLIENTEDGE, STATICEDGE.
    const uint ExtendedFrameBoundsAttribute = 9, CornerPreferenceAttribute = 33;
    const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 0x10;
    const uint FrameChanged = 0x20, NoOwnerZOrder = 0x200;
    const uint MinimizedStyle = 0x20000000, VisibleStyle = 0x10000000;

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool EqualsRect(Rect r)
        { return Left == r.Left && Top == r.Top && Right == r.Right && Bottom == r.Bottom; }
        public bool Contains(Point p)
        { return p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom; }
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
    [StructLayout(LayoutKind.Sequential)]
    struct AppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint Callback, Edge;
        public Rect Bounds;
        public IntPtr Param;
    }

    [ComImport, Guid("602D4995-B13A-429B-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList2
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr window);
        [PreserveSig] int DeleteTab(IntPtr window);
        [PreserveSig] int ActivateTab(IntPtr window);
        [PreserveSig] int SetActiveAlt(IntPtr window);
        [PreserveSig] int MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }

    sealed class TaskbarPolicy : IDisposable
    {
        ITaskbarList2 instance;
        public TaskbarPolicy()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Use a fresh STA PowerShell process.");
            instance = (ITaskbarList2)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), true));
            try { Marshal.ThrowExceptionForHR(instance.HrInit()); }
            catch { Dispose(); throw; }
        }
        public void Mark(IntPtr window, bool fullscreen)
        { Marshal.ThrowExceptionForHR(instance.MarkFullscreenWindow(window, fullscreen)); }
        public void Dispose()
        {
            if (instance != null) { Marshal.FinalReleaseComObject(instance); instance = null; }
        }
    }

    sealed class Trial
    {
        readonly IntPtr window, monitorHandle;
        readonly uint processId, threadId, originalStyle, originalExStyle, originalCornerPreference;
        readonly bool hasCornerPreference;
        readonly Placement originalPlacement;
        readonly Rect monitor, originalBounds;
        readonly TaskbarPolicy taskbar;
        readonly string property = "Llampec.FullscreenEdgeTrial." + Guid.NewGuid().ToString("N");
        readonly IntPtr token = new IntPtr(Process.GetCurrentProcess().Id);
        bool claimed, restored, markingAttempted, cornerPreferenceChanged, geometryCalibrated;
        Rect coverBounds;
        public Rect Monitor { get { return monitor; } }
        public IntPtr MonitorHandle { get { return monitorHandle; } }
        public IntPtr Window { get { return window; } }

        public Trial(IntPtr target, TaskbarPolicy policy)
        {
            window = target; taskbar = policy;
            if (!IsWindow(window) || !IsWindowVisible(window) || IsHungAppWindow(window))
                throw new InvalidOperationException("The target window is unavailable or not responding.");
            threadId = GetWindowThreadProcessId(window, out processId);
            if (threadId == 0) throw new Win32Exception();
            originalStyle = ReadStyle(window, StyleIndex);
            originalExStyle = ReadStyle(window, ExStyleIndex);
            hasCornerPreference = TryReadDwmValue(window, CornerPreferenceAttribute, out originalCornerPreference);
            if ((originalStyle & FrameStyles) == 0)
                throw new InvalidOperationException("This window already has no standard frame. Exit its own fullscreen mode first.");
            if (GetProp(window, "NonRudeHWND") != IntPtr.Zero)
                throw new InvalidOperationException("This app already has a NonRudeHWND policy. Choose another app for this experiment.");
            originalPlacement = ReadPlacement(window);
            monitorHandle = MonitorFromWindow(window, 2);
            monitor = ReadMonitor(monitorHandle).Monitor;
            Rect bounds;
            if (!GetWindowRect(window, out bounds)) throw new Win32Exception();
            originalBounds = bounds;
            if (bounds.EqualsRect(monitor))
                throw new InvalidOperationException("This window already occupies the entire monitor. Exit its fullscreen mode first.");
            if (!SetProp(window, property, token)) throw new Win32Exception();
            claimed = true;
        }

        public bool OwnsWindow()
        {
            uint currentProcess;
            return claimed && IsWindow(window)
                && GetWindowThreadProcessId(window, out currentProcess) == threadId
                && currentProcess == processId && GetProp(window, property) == token;
        }
        void RequireOwnership()
        {
            if (!OwnsWindow()) throw new InvalidOperationException("The target closed or ownership changed.");
        }
        public bool MonitorUnchanged()
        { return ReadMonitor(monitorHandle).Monitor.EqualsRect(monitor); }

        public void Enter(bool failAfterStyle)
        {
            RequireOwnership();
            ShowWindow(window, 9); // SW_RESTORE; preserve app controls, remove only its standard frame.
            RequireOwnership();
            WriteStyle(window, ReadStyle(window, StyleIndex) & ~(FrameStyles | MaximizedStyle));
            RequireOwnership();
            WriteStyle(window, ExStyleIndex, ReadStyle(window, ExStyleIndex) & ~FrameExStyles);
            if (hasCornerPreference)
            {
                RequireOwnership();
                uint doNotRound = 1;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, CornerPreferenceAttribute, ref doNotRound, 4));
                cornerPreferenceChanged = true;
            }
            if (failAfterStyle) throw new InvalidOperationException("Synthetic failure after changing style.");
            Cover();
        }
        void SetRawBounds(Rect expected)
        {
            RequireOwnership();
            if (IsHungAppWindow(window)) throw new InvalidOperationException("The app stopped responding.");
            if (!SetWindowPos(window, IntPtr.Zero, expected.Left, expected.Top,
                expected.Width, expected.Height,
                NoZOrder | NoOwnerZOrder | NoActivate | FrameChanged)) throw new Win32Exception();
            RequireOwnership();
            Rect actual;
            if (!GetWindowRect(window, out actual)) throw new Win32Exception();
            if (!actual.EqualsRect(expected))
                throw new InvalidOperationException("The app did not accept the experimental window bounds.");
        }
        public Rect ExpectedBounds(bool shortened)
        {
            Rect expected = geometryCalibrated ? coverBounds : monitor;
            // The raw HWND must cease covering the monitor before unmarking it.
            // Subtracting one from an oversized frame would not achieve that.
            if (shortened) expected.Bottom = monitor.Bottom - 1;
            return expected;
        }
        void SetBounds(bool shortened)
        {
            if (!geometryCalibrated)
            {
                SetRawBounds(monitor);
                Rect visible = ReadDwmVisibleBounds(window);
                coverBounds = CompensateVisibleFrame(monitor, visible);
                geometryCalibrated = true;
            }
            SetRawBounds(ExpectedBounds(shortened));
            if (!shortened && !ReadDwmVisibleBounds(window).EqualsRect(monitor))
                throw new InvalidOperationException("The app's visible frame did not cover the entire monitor.");
        }
        void Mark(bool fullscreen)
        {
            RequireOwnership();
            markingAttempted = true;
            taskbar.Mark(window, fullscreen);
        }
        public void Reveal()
        {
            SetBounds(true); // Break fullscreen autodetection before canceling the explicit marking.
            Mark(false);
        }
        public void Cover()
        {
            SetBounds(false);
            Mark(true);
        }
        public bool Restore()
        {
            if (restored || !claimed) return restored;
            if (!OwnsWindow())
            {
                claimed = false; restored = true;
                Console.WriteLine("Target closed or ownership changed; restoration skipped.");
                return false;
            }
            var failures = new List<string>();
            bool keepMinimized = IsIconic(window), keepVisible = IsWindowVisible(window);
            bool wasMaximized = (originalStyle & MaximizedStyle) != 0;
            if (markingAttempted)
            {
                try { taskbar.Mark(window, false); }
                catch (Exception error) { failures.Add(error.Message); }
            }
            if (OwnsWindow())
            {
                try { WriteStyle(window, ExStyleIndex, originalExStyle); }
                catch (Exception error) { failures.Add(error.Message); }
            }
            if (OwnsWindow() && cornerPreferenceChanged)
            {
                try
                {
                    uint cornerPreference = originalCornerPreference;
                    Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, CornerPreferenceAttribute, ref cornerPreference, 4));
                }
                catch (Exception error) { failures.Add(error.Message); }
            }
            // First restore the normal placement without activation. A saved
            // SW_SHOWMAXIMIZED placement would activate a background app.
            // Recreate maximization explicitly using saved bounds and SWP_NOACTIVATE.
            if (OwnsWindow())
            {
                uint style = originalStyle & ~(MaximizedStyle | MinimizedStyle | VisibleStyle);
                if (keepVisible) style |= VisibleStyle;
                if (keepMinimized) style |= MinimizedStyle;
                try { WriteStyle(window, style); }
                catch (Exception error) { failures.Add(error.Message); }
            }
            if (OwnsWindow())
            {
                Placement placement = originalPlacement;
                placement.ShowCmd = keepMinimized ? 7u : (keepVisible ? 4u : 0u);
                // SW_SHOWMINNOACTIVE keeps a user's minimize; preserve the
                // original restore destination (WPF_RESTORETOMAXIMIZED).
                if (keepMinimized) placement.Flags = (placement.Flags & ~2u) | (wasMaximized ? 2u : 0u);
                if (!SetWindowPlacement(window, ref placement)) failures.Add(new Win32Exception().Message);
            }
            if (OwnsWindow() && wasMaximized && !keepMinimized)
            {
                try { WriteStyle(window, keepVisible ? originalStyle : originalStyle & ~VisibleStyle); }
                catch (Exception error) { failures.Add(error.Message); }
                if (OwnsWindow() && !SetWindowPos(window, IntPtr.Zero,
                    originalBounds.Left, originalBounds.Top, originalBounds.Width, originalBounds.Height,
                    NoZOrder | NoOwnerZOrder | NoActivate | FrameChanged))
                    failures.Add(new Win32Exception().Message);
            }
            if (OwnsWindow() && !keepVisible)
            {
                // Also preserve invisibility if it coincides with minimization.
                ShowWindow(window, 0);
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
                    uint stateMask = MaximizedStyle | MinimizedStyle | VisibleStyle;
                    if ((ReadStyle(window, StyleIndex) & ~stateMask) != (originalStyle & ~stateMask)
                        || IsWindowVisible(window) != keepVisible || IsIconic(window) != keepMinimized
                        || (!keepMinimized && IsZoomed(window) != wasMaximized)
                        || (keepVisible && actual.ShowCmd != (keepMinimized ? 2u : originalPlacement.ShowCmd))
                        || !actual.NormalPosition.EqualsRect(originalPlacement.NormalPosition)
                        || ReadStyle(window, ExStyleIndex) != originalExStyle)
                        failures.Add("The app did not retain its original frame/window placement.");
                    uint restoredCorner;
                    if (cornerPreferenceChanged && (!TryReadDwmValue(window, CornerPreferenceAttribute, out restoredCorner)
                        || restoredCorner != originalCornerPreference))
                        failures.Add("The app did not retain its original corner preference.");
                }
                catch (Exception error) { failures.Add(error.Message); }
                RemoveProp(window, property);
            }
            claimed = false; restored = true;
            if (failures.Count != 0)
                throw new InvalidOperationException("Could not fully restore the window: " + string.Join("; ", failures.ToArray()));
            return stillOwned;
        }
    }

    // A short dwell avoids accidental edge hits; a hold allows the bar's animation.
    // Keep the whole projected taskbar band usable, even while it is sliding in.
    sealed class EdgeState
    {
        public bool Revealed { get; private set; }
        long edgeSince = -1, outsideSince = -1, revealedAt;
        public int Update(Point cursor, Rect monitor, int taskbarHeight, bool targetActive, long now)
        {
            if (!targetActive) { edgeSince = outsideSince = -1; return 0; }
            bool atEdge = monitor.Contains(cursor) && cursor.Y == monitor.Bottom - 1;
            bool inBand = monitor.Contains(cursor) && cursor.Y >= monitor.Bottom - taskbarHeight - 4;
            if (!Revealed)
            {
                if (!atEdge) edgeSince = -1;
                else if (edgeSince < 0) edgeSince = now;
                else if (now - edgeSince >= 120)
                {
                    Revealed = true; revealedAt = now; edgeSince = outsideSince = -1;
                    return 1;
                }
            }
            else if (inBand || now - revealedAt < 600) outsideSince = -1;
            else if (outsideSince < 0) outsideSince = now;
            else if (now - outsideSince >= 180)
            {
                Revealed = false; edgeSince = outsideSince = -1;
                return -1;
            }
            return 0;
        }
    }

    public static void Run(int durationSeconds)
    {
        IntPtr previousDpi = EnterDpiContext();
        try
        {
            // Reserve the shortcut before changing any application's window.
            using (HotkeyRegistration shortcut = new HotkeyRegistration())
            using (TaskbarPolicy policy = new TaskbarPolicy())
            {
                Trial trial = null;
                try
                {
                    int taskbarHeight;
                    trial = BeginTrial(policy, out taskbarHeight);
                    Console.WriteLine("El prototipo estara disponible {0} segundos. Ctrl+Alt+F activa o desactiva la ventana actual.", durationSeconds);
                    var timer = Stopwatch.StartNew();
                    var state = new EdgeState();
                    long nextShellCheck = 0;
                    long differentWindowSince = -1;
                    IntPtr differentWindow = IntPtr.Zero;
                    bool previouslyActive = true;
                    while (timer.ElapsedMilliseconds < durationSeconds * 1000L)
                    {
                        int presses = shortcut.TakePresses();
                        for (int press = 0; press < presses; press++)
                        {
                            if (trial != null)
                                EndTrial(ref trial, "Atajo: pantalla completa desactivada.");
                            else
                            {
                                try
                                {
                                    trial = BeginTrial(policy, out taskbarHeight);
                                    state = new EdgeState();
                                    nextShellCheck = 0;
                                    previouslyActive = true;
                                }
                                catch (Exception error)
                                { Console.WriteLine("No se pudo activar: {0}", error.Message); }
                            }
                            differentWindowSince = -1;
                            differentWindow = IntPtr.Zero;
                        }
                        if (trial == null) { Thread.Sleep(30); continue; }
                        if (!trial.OwnsWindow())
                        {
                            EndTrial(ref trial, "La ventana se ha cerrado o ha cambiado de propietario.");
                            continue;
                        }
                        if (!IsWindowVisible(trial.Window) || IsIconic(trial.Window))
                        {
                            EndTrial(ref trial, "La ventana se ha ocultado o minimizado.");
                            continue;
                        }
                        long now = timer.ElapsedMilliseconds;
                        if (now >= nextShellCheck)
                        {
                            if (!trial.MonitorUnchanged())
                            {
                                EndTrial(ref trial, "La distribucion de pantallas ha cambiado.");
                                continue;
                            }
                            ReadPrimaryTaskbar(trial.MonitorHandle, trial.Monitor, out taskbarHeight);
                            nextShellCheck = now + 250;
                        }
                        IntPtr foreground = GetAncestor(GetForegroundWindow(), 2);
                        ForegroundDisposition focus = ReadForegroundDisposition(trial.Window, foreground);
                        if (focus == ForegroundDisposition.DifferentApplication)
                        {
                            // Brief NULL/shell transitions are expected when using the
                            // taskbar. Require the new application's identity to settle.
                            if (differentWindow != foreground)
                            { differentWindow = foreground; differentWindowSince = now; }
                            else if (now - differentWindowSince >= 120)
                            {
                                EndTrial(ref trial, "Otra ventana seleccionada: pantalla completa desactivada.");
                                continue;
                            }
                        }
                        else
                        { differentWindow = IntPtr.Zero; differentWindowSince = -1; }
                        bool targetActive = focus == ForegroundDisposition.Target;
                        // Shell gestures, the Llampec panel and the target's own
                        // dialogs pause edge detection without ending fullscreen.
                        if (targetActive != previouslyActive)
                        {
                            Console.WriteLine(targetActive
                                ? "Ventana activa: deteccion del borde reanudada."
                                : "Foco fuera de la ventana: deteccion del borde pausada.");
                            previouslyActive = targetActive;
                        }
                        Point cursor;
                        if (!GetCursorPos(out cursor)) throw new Win32Exception();
                        int transition = state.Update(cursor, trial.Monitor, taskbarHeight, targetActive, now);
                        if (transition > 0)
                        { trial.Reveal(); Console.WriteLine("Edge reached: allowing taskbar to appear."); }
                        else if (transition < 0)
                        { trial.Cover(); Console.WriteLine("Pointer left taskbar: covering the full monitor again."); }
                        Thread.Sleep(30);
                    }
                    Console.WriteLine("Tiempo del prototipo terminado.");
                }
                finally { EndTrial(ref trial, "Restaurando la ventana al finalizar."); }
            }
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    static Trial BeginTrial(TaskbarPolicy policy, out int taskbarHeight)
    {
        IntPtr target = GetAncestor(GetForegroundWindow(), 2);
        RejectShellOrConsole(target);
        Trial trial = new Trial(target, policy);
        try
        {
            ReadPrimaryTaskbar(trial.MonitorHandle, trial.Monitor, out taskbarHeight);
            trial.Enter(false);
            Console.WriteLine("Pantalla completa activa en {0} ({1} x {2}), ventana: {3}. Ctrl+Alt+F para salir.",
                ReadMonitorDescription(trial.MonitorHandle), trial.Monitor.Width, trial.Monitor.Height,
                ReadWindowTitle(target));
            return trial;
        }
        catch { trial.Restore(); throw; }
    }

    static void EndTrial(ref Trial trial, string reason)
    {
        if (trial == null) return;
        Trial previous = trial;
        trial = null;
        string monitorDescription;
        try { monitorDescription = ReadMonitorDescription(previous.MonitorHandle); }
        catch (Win32Exception) { monitorDescription = "monitor no disponible"; }
        Console.WriteLine("{0} Pantalla: {1}.", reason, monitorDescription);
        if (previous.Restore())
            Console.WriteLine("Marco y posicion originales restaurados. Los ajustes de la barra no han cambiado.");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Position;
        public uint Private;
    }

    sealed class HotkeyRegistration : IDisposable
    {
        const uint HotkeyMessage = 0x0312, RemoveMessage = 1;
        readonly int id;
        bool registered;
        public HotkeyRegistration() : this(0x4C46, 0x4003, 0x46) { } // Ctrl+Alt+F, no repeat.
        public HotkeyRegistration(int shortcutId, uint modifiers, uint key)
        {
            id = shortcutId;
            if (!RegisterHotKey(IntPtr.Zero, id, modifiers, key))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo reservar el atajo; puede estar en uso por otra aplicacion.");
            registered = true;
        }
        public int TakePresses()
        {
            int presses = 0;
            Message message;
            while (PeekMessage(out message, IntPtr.Zero, HotkeyMessage, HotkeyMessage, RemoveMessage))
                if (message.WParam.ToUInt64() == (ulong)id) presses++;
            return presses;
        }
        public void Dispose()
        {
            if (!registered) return;
            registered = false;
            if (!UnregisterHotKey(IntPtr.Zero, id))
                Console.WriteLine("No se pudo liberar el atajo: {0}", new Win32Exception().Message);
        }
    }

    enum ForegroundDisposition { Target, Preserve, DifferentApplication }

    static ForegroundDisposition ClassifyForeground(bool target, bool ownedByTarget,
        bool ownApp, bool shellSurface, bool eligible)
    {
        if (target) return ForegroundDisposition.Target;
        if (ownedByTarget || ownApp || shellSurface || !eligible) return ForegroundDisposition.Preserve;
        return ForegroundDisposition.DifferentApplication;
    }

    static ForegroundDisposition ReadForegroundDisposition(IntPtr target, IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || !IsWindow(foreground)) return ForegroundDisposition.Preserve;
        if (foreground == target) return ForegroundDisposition.Target;
        uint processId;
        if (GetWindowThreadProcessId(foreground, out processId) == 0 || processId == 0)
            return ForegroundDisposition.Preserve;
        string processName;
        try
        {
            using (Process process = Process.GetProcessById((int)processId)) processName = process.ProcessName;
        }
        catch (Exception error)
        {
            if (!(error is ArgumentException) && !(error is InvalidOperationException) && !(error is Win32Exception)) throw;
            return ForegroundDisposition.Preserve;
        }
        StringBuilder className = new StringBuilder(256);
        GetClassName(foreground, className, className.Capacity);
        IntPtr targetOwner = GetAncestor(target, 3);
        bool sameOwner = targetOwner != IntPtr.Zero && GetAncestor(foreground, 3) == targetOwner;
        bool ownApp = processId == Process.GetCurrentProcess().Id
            || string.Equals(processName, "Llampec", StringComparison.OrdinalIgnoreCase);
        bool shellSurface = IsShellSurface(className.ToString(), processName);
        if (sameOwner || ownApp || shellSurface) return ForegroundDisposition.Preserve;
        uint style, extendedStyle;
        try
        {
            style = ReadStyle(foreground, StyleIndex);
            extendedStyle = ReadStyle(foreground, ExStyleIndex);
        }
        catch (Win32Exception) { return ForegroundDisposition.Preserve; } // A transient foreground can close during the read.
        bool eligible = IsWindowVisible(foreground) && (style & 0x40000000) == 0
            && (extendedStyle & 0x08000080) == 0 && !string.IsNullOrWhiteSpace(ReadWindowTitle(foreground));
        return ClassifyForeground(false, sameOwner, ownApp, shellSurface, eligible);
    }

    static bool IsShellSurface(string className, string processName)
    {
        // Explorer's file windows are real applications. Exclude shell surfaces
        // by class; do not exempt the entire explorer.exe process.
        switch (className)
        {
            case "Progman": case "WorkerW": case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd":
            case "Shell_NotificationOverflowWindow": case "NotifyIconOverflowWindow":
            case "XamlExplorerHostIslandWindow": case "#32768": case "#32769":
            case "MultitaskingViewFrame": case "TaskSwitcherWnd":
                return true;
        }
        switch (processName.ToLowerInvariant())
        {
            case "startmenuexperiencehost": case "shellexperiencehost": case "shellhost":
            case "searchhost": case "searchapp": case "textinputhost": case "lockapp":
            case "widgets": case "widgetservice":
                return true;
            default: return false;
        }
    }

    static string ReadWindowTitle(IntPtr window)
    {
        StringBuilder title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        return title.ToString().Trim();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorDetails
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    static string ReadMonitorDescription(IntPtr monitor)
    {
        MonitorDetails details = new MonitorDetails();
        details.Size = (uint)Marshal.SizeOf(typeof(MonitorDetails));
        if (!GetMonitorDetails(monitor, ref details)) throw new Win32Exception();
        return details.Device + ((details.Flags & 1) != 0 ? " [principal]" : "");
    }

    static void CheckForegroundPolicy()
    {
        if (ClassifyForeground(true, false, false, false, true) != ForegroundDisposition.Target
            || ClassifyForeground(false, true, false, false, true) != ForegroundDisposition.Preserve
            || ClassifyForeground(false, false, true, false, true) != ForegroundDisposition.Preserve
            || ClassifyForeground(false, false, false, true, true) != ForegroundDisposition.Preserve
            || ClassifyForeground(false, false, false, false, false) != ForegroundDisposition.Preserve
            || ClassifyForeground(false, false, false, false, true) != ForegroundDisposition.DifferentApplication
            || !IsShellSurface("Shell_TrayWnd", "explorer")
            || !IsShellSurface("Windows.UI.Core.CoreWindow", "ShellHost")
            || !IsShellSurface("Windows.UI.Core.CoreWindow", "StartMenuExperienceHost")
            || IsShellSurface("CabinetWClass", "explorer")
            || IsShellSurface("Chrome_WidgetWin_1", "msedge"))
            throw new InvalidOperationException("SelfTest: foreground exit/shell/dialog preservation policy failed.");
    }

    static void CheckHotkeyRegistration()
    {
        // Test reservation conflicts and cleanup without sending any input.
        // A rare Ctrl+Alt+Shift+F24 combination avoids taking the trial's key.
        const uint modifiers = 0x4007, key = 0x87;
        using (HotkeyRegistration first = new HotkeyRegistration(0x4C47, modifiers, key))
        {
            if (RegisterHotKey(IntPtr.Zero, 0x4C48, modifiers, key))
            {
                UnregisterHotKey(IntPtr.Zero, 0x4C48);
                throw new InvalidOperationException("SelfTest: duplicate shortcut reservation unexpectedly succeeded.");
            }
            if (Marshal.GetLastWin32Error() != 1409)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SelfTest: wrong duplicate shortcut error.");
        }
        using (HotkeyRegistration released = new HotkeyRegistration(0x4C47, modifiers, key)) { }
    }

    static IntPtr ReadPrimaryTaskbar(IntPtr monitorHandle, Rect monitor, out int height)
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || MonitorFromWindow(tray, 2) != monitorHandle)
            throw new InvalidOperationException("For this prototype choose an app on the monitor with the primary taskbar.");
        AppBarData data = new AppBarData();
        data.Size = (uint)Marshal.SizeOf(typeof(AppBarData));
        if (SHAppBarMessage(5, ref data) == UIntPtr.Zero)
            throw new InvalidOperationException("Could not read the primary taskbar bounds.");
        Rect trayBounds;
        if (!GetWindowRect(tray, out trayBounds)) throw new Win32Exception();
        height = Math.Max(data.Bounds.Height, trayBounds.Height);
        if (height <= 0 || height >= monitor.Height / 2)
            throw new InvalidOperationException("Could not determine a usable taskbar interaction area.");
        // ABM_GETTASKBARPOS guarantees rc, not an output uEdge. Infer the
        // bottom edge from the returned rectangle, including an offscreen bar.
        if (data.Bounds.Top < monitor.Top + monitor.Height / 2
            || data.Bounds.Top > monitor.Bottom + height
            || data.Bounds.Bottom < monitor.Bottom - 1
            || data.Bounds.Right <= monitor.Left || data.Bounds.Left >= monitor.Right)
            throw new InvalidOperationException("This prototype requires the primary taskbar on the bottom edge.");
        return tray;
    }

    public static void SelfTest()
    {
        IntPtr previousDpi = EnterDpiContext(), window = IntPtr.Zero, sentinel = IntPtr.Zero;
        try
        {
            CheckForegroundPolicy();
            CheckHotkeyRegistration();
            CheckFrameGeometry();
            CheckEdgeState();
            using (TaskbarPolicy policy = new TaskbarPolicy())
            {
                window = CreateWindowEx(FrameExStyles, "STATIC", "Llampec synthetic edge test",
                    0x10CF0000, 120, 140, 520, 350, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (window == IntPtr.Zero) throw new Win32Exception();
                uint testCornerPreference;
                if (TryReadDwmValue(window, CornerPreferenceAttribute, out testCornerPreference))
                {
                    testCornerPreference = 3; // Verify preservation of a non-default preference.
                    Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, CornerPreferenceAttribute, ref testCornerPreference, 4));
                }
                sentinel = CreateWindowEx(0, "STATIC", "Llampec synthetic focus reference",
                    0x10CF0000, 160, 180, 240, 140, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (sentinel == IntPtr.Zero) throw new Win32Exception();
                int taskbarHeight;
                ReadPrimaryTaskbar(MonitorFromWindow(window, 2), ReadMonitor(MonitorFromWindow(window, 2)).Monitor, out taskbarHeight);
                CheckRoundTrip(window, sentinel, policy, false, false, false, 0);
                CheckRoundTrip(window, sentinel, policy, true, false, false, 0);
                CheckRoundTrip(window, sentinel, policy, true, true, false, 0);
                CheckRoundTrip(window, sentinel, policy, false, false, true, 0);
                CheckRoundTrip(window, sentinel, policy, true, false, true, 0);
                CheckRoundTrip(window, sentinel, policy, true, true, true, 0);
                CheckRoundTrip(window, sentinel, policy, false, false, true, 7);
                CheckRoundTrip(window, sentinel, policy, true, false, true, 7);
                CheckRoundTrip(window, sentinel, policy, false, false, true, 0x100);
                CheckRoundTrip(window, sentinel, policy, true, false, true, 0x100);
            }
            Console.WriteLine("PASS: native COM and taskbar bounds, visible full/reveal/cover geometry, extended frame and corner restoration, normal/maximized restore and rollback, fullscreen kept while inactive, foreground and minimize/hide preserved, pause/resume edge timing. Native touchscreen swipe still needs user verification.");
        }
        finally
        {
            if (window != IntPtr.Zero) DestroyWindow(window);
            if (sentinel != IntPtr.Zero) DestroyWindow(sentinel);
            SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    static void CheckRoundTrip(IntPtr window, IntPtr sentinel, TaskbarPolicy policy, bool maximized, bool failAfterStyle, bool reveal, int userShowCommand)
    {
        ShowWindow(window, maximized ? 3 : 9);
        uint style = ReadStyle(window, StyleIndex), exStyle = ReadStyle(window, ExStyleIndex);
        uint cornerPreference;
        bool hasCornerPreference = TryReadDwmValue(window, CornerPreferenceAttribute, out cornerPreference);
        Placement placement = ReadPlacement(window);
        Rect bounds;
        if (!GetWindowRect(window, out bounds)) throw new Win32Exception();
        Trial trial = new Trial(window, policy);
        bool expectedFailure = false;
        try
        {
            trial.Enter(failAfterStyle);
            uint trialCornerPreference;
            if (IsZoomed(window) || (ReadStyle(window, StyleIndex) & FrameStyles) != 0
                || (ReadStyle(window, ExStyleIndex) & FrameExStyles) != 0
                || !ReadDwmVisibleBounds(window).EqualsRect(trial.Monitor)
                || (hasCornerPreference && (!TryReadDwmValue(window, CornerPreferenceAttribute, out trialCornerPreference)
                    || trialCornerPreference != 1)))
                throw new InvalidOperationException("SelfTest: fullscreen frame/state mismatch.");
            if (reveal)
            {
                trial.Reveal(); // Restore directly from revealed mode too.
                trial.Cover();
                trial.Reveal();
            }
            if (userShowCommand != 0)
                ShowWindow(window, userShowCommand == 0x100 ? 0 : userShowCommand);
        }
        catch (InvalidOperationException error)
        {
            if (!failAfterStyle || error.Message != "Synthetic failure after changing style.") throw;
            expectedFailure = true;
        }
        finally
        {
            // Windows may decline foreground acquisition while the user is
            // working elsewhere. Preserve whichever window actually has focus;
            // do not bypass that policy to force the synthetic reference active.
            SetForegroundWindow(sentinel);
            IntPtr referenceForeground = GetForegroundWindow();
            bool fullscreenKept = true;
            try
            {
                if (!failAfterStyle && userShowCommand == 0)
                {
                    Rect expected = trial.ExpectedBounds(reveal), heldBounds;
                    fullscreenKept = GetWindowRect(window, out heldBounds) && heldBounds.EqualsRect(expected)
                        && (ReadStyle(window, StyleIndex) & FrameStyles) == 0;
                }
            }
            finally { trial.Restore(); } // Always roll back, including a failed state inspection.
            if (GetForegroundWindow() != referenceForeground)
                throw new InvalidOperationException(string.Format(
                    "SelfTest: could not preserve reference foreground (max={0}, rollback={1}, reveal={2}, userShow={3}, expected={4}, actual={5}).",
                    maximized, failAfterStyle, reveal, userShowCommand, referenceForeground, GetForegroundWindow()));
            if (!fullscreenKept)
                throw new InvalidOperationException("SelfTest: fullscreen geometry/frame changed while another window was active.");
        }
        if (userShowCommand != 0)
        {
            bool minimized = userShowCommand == 7;
            if (IsIconic(window) != minimized || IsWindowVisible(window) != minimized)
                throw new InvalidOperationException("SelfTest: user's minimize/hide was not preserved.");
            // Verify the eventual restore destination too (owned synthetic window).
            ShowWindow(window, minimized ? 9 : 8);
        }
        Placement after = ReadPlacement(window);
        uint restoredCornerPreference;
        Rect afterBounds;
        if (!GetWindowRect(window, out afterBounds)) throw new Win32Exception();
        if (ReadStyle(window, StyleIndex) != style || ReadStyle(window, ExStyleIndex) != exStyle
            || after.ShowCmd != placement.ShowCmd || !after.NormalPosition.EqualsRect(placement.NormalPosition)
            || !afterBounds.EqualsRect(bounds) || (failAfterStyle && !expectedFailure)
            || GetProp(window, "NonRudeHWND") != IntPtr.Zero
            || (hasCornerPreference && (!TryReadDwmValue(window, CornerPreferenceAttribute, out restoredCornerPreference)
                || restoredCornerPreference != cornerPreference)))
            throw new InvalidOperationException("SelfTest: original styles, placement, bounds or policy were not preserved.");
    }

    static Rect CompensateVisibleFrame(Rect raw, Rect visible)
    {
        int left = visible.Left - raw.Left, top = visible.Top - raw.Top;
        int right = raw.Right - visible.Right, bottom = raw.Bottom - visible.Bottom;
        // Only compensate a small, measured non-client frame. Larger gaps or
        // bounds outside the HWND indicate unsupported/custom geometry.
        if (left < 0 || top < 0 || right < 0 || bottom < 0
            || left > 32 || top > 32 || right > 32 || bottom > 32
            || visible.Width <= 0 || visible.Height <= 0)
            throw new InvalidOperationException("This app has unsupported visible-frame margins.");
        return new Rect { Left = raw.Left - left, Top = raw.Top - top,
            Right = raw.Right + right, Bottom = raw.Bottom + bottom };
    }

    static void CheckFrameGeometry()
    {
        Rect monitor = new Rect { Left = -1600, Top = -200, Right = 0, Bottom = 700 };
        if (!CompensateVisibleFrame(monitor, monitor).EqualsRect(monitor))
            throw new InvalidOperationException("SelfTest: zero frame changed fullscreen geometry.");
        Rect visible = new Rect { Left = -1589, Top = -189, Right = -11, Bottom = 689 };
        Rect expected = new Rect { Left = -1611, Top = -211, Right = 11, Bottom = 711 };
        if (!CompensateVisibleFrame(monitor, visible).EqualsRect(expected))
            throw new InvalidOperationException("SelfTest: measured visible-frame compensation failed.");
        visible.Left = monitor.Left + 33;
        bool rejected = false;
        try { CompensateVisibleFrame(monitor, visible); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("SelfTest: oversized frame margins were accepted.");
    }

    static void CheckEdgeState()
    {
        Rect monitor = new Rect { Left = -1600, Top = -200, Right = 0, Bottom = 700 };
        var state = new EdgeState();
        Point edge = new Point(-700, 699), band = new Point(-700, 650), outside = new Point(-700, 500);
        if (state.Update(edge, monitor, 72, true, 0) != 0
            || state.Update(edge, monitor, 72, true, 119) != 0
            || state.Update(edge, monitor, 72, true, 120) != 1
            || state.Update(outside, monitor, 72, true, 300) != 0
            || state.Update(band, monitor, 72, true, 1000) != 0
            || state.Update(outside, monitor, 72, false, 2000) != 0
            || state.Update(outside, monitor, 72, true, 2001) != 0
            || state.Update(outside, monitor, 72, true, 2180) != 0
            || state.Update(outside, monitor, 72, true, 2181) != -1)
            throw new InvalidOperationException("SelfTest: edge activation/animation hold/focus guard failed.");
        var unfocused = new EdgeState();
        if (unfocused.Update(edge, monitor, 72, false, 0) != 0
            || unfocused.Update(edge, monitor, 72, false, 1000) != 0 || unfocused.Revealed)
            throw new InvalidOperationException("SelfTest: background target revealed the bar.");
        var otherMonitor = new EdgeState();
        Point away = new Point(200, 699);
        if (otherMonitor.Update(away, monitor, 72, true, 0) != 0
            || otherMonitor.Update(away, monitor, 72, true, 1000) != 0 || otherMonitor.Revealed)
            throw new InvalidOperationException("SelfTest: another monitor triggered reveal.");
        // A shell gesture or Alt+Tab must pause, not cancel the session. Dwell
        // and close delay restart on return instead of using inactive time.
        var paused = new EdgeState();
        if (paused.Update(edge, monitor, 72, true, 0) != 0
            || paused.Update(edge, monitor, 72, true, 119) != 0
            || paused.Update(edge, monitor, 72, false, 120) != 0
            || paused.Update(edge, monitor, 72, true, 1000) != 0
            || paused.Update(edge, monitor, 72, true, 1119) != 0
            || paused.Update(edge, monitor, 72, true, 1120) != 1
            || paused.Update(outside, monitor, 72, false, 2000) != 0
            || paused.Update(outside, monitor, 72, false, 4000) != 0
            || !paused.Revealed
            || paused.Update(band, monitor, 72, true, 5000) != 0
            || paused.Update(outside, monitor, 72, true, 6000) != 0
            || paused.Update(outside, monitor, 72, true, 6179) != 0
            || paused.Update(outside, monitor, 72, true, 6180) != -1)
            throw new InvalidOperationException("SelfTest: focus pause/resume lost state or reused inactive time.");
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
        if (processId == Process.GetCurrentProcess().Id || IsShellSurface(cls, name) || name == "llampec"
            || name == "powershell" || name == "pwsh" || name == "cmd" || name == "windowsterminal"
            || name == "openconsole" || name == "conhost")
            throw new InvalidOperationException("During the countdown select an app, not the desktop, taskbar or terminal.");
    }
    static IntPtr EnterDpiContext()
    {
        if (IntPtr.Size != 8) throw new InvalidOperationException("Use 64-bit PowerShell (ARM64 or x64).");
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new Win32Exception();
        return previous;
    }
    static MonitorInfo ReadMonitor(IntPtr monitor)
    {
        MonitorInfo info = new MonitorInfo();
        info.Size = (uint)Marshal.SizeOf(typeof(MonitorInfo));
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        return info;
    }
    static uint ReadStyle(IntPtr window, int index)
    {
        SetLastError(0);
        IntPtr result = GetWindowLongPtr(window, index);
        if (result == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
        return unchecked((uint)result.ToInt64());
    }
    static void WriteStyle(IntPtr window, uint style)
    { WriteStyle(window, StyleIndex, style); }
    static void WriteStyle(IntPtr window, int index, uint style)
    {
        SetLastError(0);
        IntPtr result = SetWindowLongPtr(window, index, new IntPtr(unchecked((int)style)));
        if (result == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
    }
    static bool TryReadDwmValue(IntPtr window, uint attribute, out uint value)
    { return DwmGetWindowAttributeValue(window, attribute, out value, 4) >= 0; }
    static Rect ReadDwmVisibleBounds(IntPtr window)
    {
        Rect bounds;
        Marshal.ThrowExceptionForHR(DwmGetWindowAttributeBounds(window, ExtendedFrameBoundsAttribute, out bounds, 16));
        return bounds;
    }
    static Placement ReadPlacement(IntPtr window)
    {
        Placement placement = new Placement();
        placement.Length = (uint)Marshal.SizeOf(typeof(Placement));
        if (!GetWindowPlacement(window, ref placement)) throw new Win32Exception();
        return placement;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetMonitorDetails(IntPtr monitor, ref MonitorDetails info);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetProp(IntPtr window, string name, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeValue(IntPtr window, uint attribute, out uint value, uint size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeBounds(IntPtr window, uint attribute, out Rect bounds, uint size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, uint size);
}
