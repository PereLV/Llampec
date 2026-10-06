// Independent Win32 implementation. The one-pixel reveal transition follows Chromium's
// documented OnBackgroundFullscreen idea; no shell preferences or shell window states are changed.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Llampec.Diagnostics;
using Llampec.Interop;

namespace Llampec.Actions.Fullscreen;

internal sealed class NativeFullscreenWindowSystem : IFullscreenWindowSystem
{
    // Create/destroy/show/hide, name change and cloaking: the only events handled below.
    private static readonly (uint First, uint Last)[] WindowEventRanges = [(0x8000, 0x8003), (0x800C, 0x800C), (0x8017, 0x8018)];
    private readonly WinEventProc _eventCallback;
    private readonly List<nint> _windowHooks = [];
    private nint _foregroundHook;
    private TaskbarPolicy? _taskbar;
    private bool _disposed;
    private readonly bool _allowOwnWindowsForTesting;
    public event Action? WindowChanged;
    public nint ForegroundWindow => GetAncestor(GetForegroundWindow(), 2);

    public NativeFullscreenWindowSystem(bool allowOwnWindowsForTesting = false)
    {
        _allowOwnWindowsForTesting = allowOwnWindowsForTesting;
        _eventCallback = OnWindowEvent;
        _foregroundHook = SetWinEventHook(3, 3, 0, _eventCallback, 0, 0, 0);
        if (_foregroundHook == 0)
            Log.Warn("Fullscreen foreground tracking hook is unavailable.");
    }

    /// <summary>Call on the thread that created this instance, which owns the message loop.</summary>
    public void SetWindowTracking(bool enabled)
    {
        if (_disposed || enabled == (_windowHooks.Count != 0)) return;
        if (!enabled) { UnhookWindows(); return; }
        // WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS.
        foreach (var (first, last) in WindowEventRanges)
            if (SetWinEventHook(first, last, 0, _eventCallback, 0, 0, 2) is var hook and not 0) _windowHooks.Add(hook);
        if (_windowHooks.Count != WindowEventRanges.Length)
            Log.Warn("Fullscreen window tracking hook is unavailable.");
    }

    private void UnhookWindows()
    {
        foreach (nint hook in _windowHooks) _ = UnhookWinEvent(hook);
        _windowHooks.Clear();
    }

    private void OnWindowEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == 0 || (eventType != 3 && (objectId != 0 || childId != 0))) return;
        // Location changes during our own resize are handled by the active poller. Global
        // motion must not rebuild the tile on every animation frame.
        if (eventType is not (3 or 0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x800C or 0x8017 or 0x8018)) return;
        try { WindowChanged?.Invoke(); }
        catch (Exception ex) { Log.Warn($"Fullscreen window event failed: {ex.Message}"); }
    }

    public FullscreenWindowSnapshot? Read(nint handle)
    {
        if (handle == 0 || !IsWindow(handle)) return null;
        uint thread = GetWindowThreadProcessId(handle, out uint process);
        if (thread == 0 || process == 0) return null;
        var identity = new FullscreenWindowIdentity(handle, process, thread);
        bool own = process == Environment.ProcessId;
        string title = ReadWindowTitle(handle);
        var className = new StringBuilder(256);
        GetClassName(handle, className, className.Capacity);
        string processName;
        try { using var app = Process.GetProcessById((int)process); processName = app.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
        bool shell = IsShellSurface(className.ToString(), processName);
        bool visible = IsWindowVisible(handle), minimized = IsIconic(handle);
        uint style, extendedStyle;
        try { style = ReadStyle(handle, StyleIndex); extendedStyle = ReadStyle(handle, ExStyleIndex); }
        catch (Win32Exception) { return null; }
        bool eligible = (!own || _allowOwnWindowsForTesting) && !shell && visible && !minimized && !string.IsNullOrWhiteSpace(title)
            && (style & 0x40000000) == 0 && (extendedStyle & 0x08000080) == 0
            && GetAncestor(handle, 2) == handle
            && (!TryReadDwmValue(handle, 14, out uint cloaked) || cloaked == 0);
        if (GetWindowThreadProcessId(handle, out uint currentProcess) != thread || currentProcess != process) return null;
        return new(identity, title, eligible, own, shell, minimized, visible);
    }

    public FullscreenWindowSnapshot? ResolveForeground(nint handle)
    {
        var first = Read(GetAncestor(handle, 2));
        if (first is null || first.IsOwnWindow || first.IsShellSurface) return first;
        var visited = new HashSet<nint>();
        FullscreenWindowSnapshot? selected = first.IsEligible ? first : null;
        // Prefer the owner so a modal dialog never becomes the frame we later resize.
        for (nint owner = GetWindow(first.Identity.Handle, 4); owner != 0 && visited.Add(owner); owner = GetWindow(owner, 4))
            if (Read(owner) is { IsEligible: true } snapshot) selected = snapshot;
        return selected ?? first;
    }

    public IFullscreenSession Enter(FullscreenWindowIdentity identity)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeFullscreenWindowSystem));
        if (Read(identity.Handle) is not { IsEligible: true } snapshot || snapshot.Identity != identity)
            throw new InvalidOperationException("This window is no longer available.");
        using var dpi = new DpiScope();
        _taskbar ??= new TaskbarPolicy();
        var trial = new Trial(identity.Handle, _taskbar);
        Session? session = null;
        try { session = new Session(this, trial, identity); trial.Enter(false); return session; }
        catch (Exception activation)
        {
            try { trial.Restore(); }
            catch (Exception restoration)
            {
                if (session is not null && trial.OwnsWindow())
                    throw new FullscreenActivationRecoveryException(session, new AggregateException(activation, restoration));
                throw new InvalidOperationException("Could not fully restore the window.", new AggregateException(activation, restoration));
            }
            throw;
        }
    }

    private sealed class Session : IFullscreenSession
    {
        private readonly NativeFullscreenWindowSystem _windows;
        private readonly Trial _trial;
        private readonly EdgeState _edge = new();
        private int _taskbarHeight;
        private long _nextTaskbarCheck;
        public FullscreenWindowIdentity Identity { get; }
        public string MonitorDescription { get; }
        public string MonitorDevice { get; }
        public bool OwnsWindow => _trial.OwnsWindow();
        public bool IsVisible => IsWindowVisible(Identity.Handle);
        public bool IsMinimized => IsIconic(Identity.Handle);
        public bool IsCloaked => TryReadDwmValue(Identity.Handle, 14, out uint cloaked) && cloaked != 0;
        public bool DisplayUnchanged { get { using var dpi = new DpiScope(); return _trial.MonitorUnchanged(); } }

        public Session(NativeFullscreenWindowSystem windows, Trial trial, FullscreenWindowIdentity identity)
        {
            _windows = windows; _trial = trial; Identity = identity;
            MonitorDetails details = ReadMonitorDetails(trial.MonitorHandle);
            MonitorDevice = details.Device;
            MonitorDescription = details.Device;
        }

        public FullscreenForegroundDisposition ClassifyForeground(nint foreground)
        {
            if (foreground == Identity.Handle) return FullscreenForegroundDisposition.Target;
            if (foreground == 0 || !IsWindow(foreground)) return FullscreenForegroundDisposition.Preserve;
            if (GetAncestor(foreground, 3) == GetAncestor(Identity.Handle, 3)) return FullscreenForegroundDisposition.Preserve;
            var window = _windows.Read(foreground);
            return window is { IsEligible: true } ? FullscreenForegroundDisposition.DifferentWindow
                : FullscreenForegroundDisposition.Preserve;
        }

        public void UpdatePointer(long now)
        {
            using var dpi = new DpiScope();
            if (now >= _nextTaskbarCheck)
            {
                _taskbarHeight = ReadTaskbarHeight(_trial.MonitorHandle, _trial.Monitor);
                _nextTaskbarCheck = now + 250;
            }
            if (!GetCursorPos(out Point cursor)) throw new Win32Exception();
            bool active = _taskbarHeight > 0 && ClassifyForeground(_windows.ForegroundWindow) == FullscreenForegroundDisposition.Target;
            if (_taskbarHeight == 0 && _edge.Revealed)
            {
                _trial.Cover();
                _edge.Reset();
            }
            int transition = _edge.Update(cursor, _trial.Monitor, _taskbarHeight, active, now);
            if (transition > 0) _trial.Reveal();
            else if (transition < 0) _trial.Cover();
        }

        public void Restore() { using var dpi = new DpiScope(); _trial.Restore(); }
        public void RevealTaskbar() { using var dpi = new DpiScope(); _trial.Reveal(); }
        public void CoverTaskbar() { using var dpi = new DpiScope(); _trial.Cover(); }
        public void ReapplyShellPolicy()
        {
            using var dpi = new DpiScope();
            if (_edge.Revealed) _trial.Reveal(); else _trial.Cover();
            _nextTaskbarCheck = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorDetails
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private static MonitorDetails ReadMonitorDetails(nint monitor)
    {
        var details = new MonitorDetails { Size = (uint)Marshal.SizeOf<MonitorDetails>() };
        if (!GetMonitorDetails(monitor, ref details)) throw new Win32Exception();
        return details;
    }

    private static string ReadWindowTitle(nint window)
    {
        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        return title.ToString().Trim();
    }

    internal static bool IsShellSurface(string className, string processName)
        => className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Shell_NotificationOverflowWindow" or "NotifyIconOverflowWindow" or "XamlExplorerHostIslandWindow"
            or "#32768" or "#32769" or "MultitaskingViewFrame" or "TaskSwitcherWnd"
        || processName.ToLowerInvariant() is "startmenuexperiencehost" or "shellexperiencehost" or "shellhost"
            or "searchhost" or "searchapp" or "textinputhost" or "lockapp" or "widgets" or "widgetservice";

    // Secondary taskbars are independent Shell_SecondaryTrayWnd windows. A monitor with no
    // taskbar still supports fullscreen, but there is no local bar to reveal.
    internal static int ReadTaskbarHeight(nint monitorHandle, Rect monitor)
    {
        var name = new StringBuilder(64);
        var data = new Shell32.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<Shell32.APPBARDATA>(),
            uEdge = 3, // ABE_BOTTOM.
            rc = new User32.RECT { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom }
        };
        // An auto-hidden bar can lie in the next monitor's bounds when screens are stacked.
        // Query its registered monitor rather than inferring ownership from its current HWND.
        // https://learn.microsoft.com/en-us/windows/win32/shell/abm-getautohidebarex
        nint registeredBar = (nint)Shell32.SHAppBarMessage(Shell32.ABM_GETAUTOHIDEBAREX, ref data);
        uint dpi = GetDpiForMonitor(monitorHandle);
        int ReadHeight(nint window, bool registeredForMonitor = false)
        {
            name.Clear();
            GetClassName(window, name, name.Capacity);
            if (name.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return 0;
            return GetWindowRect(window, out Rect rect) ? TaskbarInteractionHeight(monitor, rect, dpi, registeredForMonitor) : 0;
        }
        if (registeredBar != 0)
        {
            int registeredHeight = ReadHeight(registeredBar, registeredForMonitor: true);
            if (registeredHeight > 0) return registeredHeight;
        }

        int height = 0;
        EnumWindows((window, _) =>
        {
            if (MonitorFromWindow(window, 2) != monitorHandle) return true;
            height = Math.Max(height, ReadHeight(window));
            return true;
        }, 0);
        return height;
    }

    internal static int TaskbarInteractionHeight(Rect monitor, Rect trayBounds, uint dpi, bool registeredForMonitor = false)
    {
        // Registration identifies the monitor; placement can be wholly outside it during
        // auto-hide. Use the bar's height, retaining its expanded interaction band while it slides.
        if (trayBounds.Height <= 0 || trayBounds.Height >= monitor.Height / 2) return 0;
        // The normal-bar fallback must be on this screen's bottom edge. Otherwise an upper
        // monitor's hidden bar, now geometrically inside this screen's top, would be a false match.
        if (!registeredForMonitor && (trayBounds.Top < monitor.Bottom - trayBounds.Height - 4
            || trayBounds.Top > monitor.Bottom + trayBounds.Height || trayBounds.Bottom < monitor.Bottom - 4)) return 0;
        // A collapsed touch bar can expose only a few pixels. Reserve its expanded interaction
        // band while it animates; apply the floor only when a local bar really exists.
        return Math.Max(trayBounds.Height, (int)(48 * (dpi == 0 ? 96 : dpi) / 96));
    }

    private static uint GetDpiForMonitor(nint monitor)
        => GetDpiForMonitorNative(monitor, 0, out uint x, out _) >= 0 ? x : 96;

    private sealed class DpiScope : IDisposable
    {
        private readonly nint _previous = EnterDpiContext();
        public void Dispose() => SetThreadDpiAwarenessContext(_previous);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_foregroundHook != 0) { UnhookWinEvent(_foregroundHook); _foregroundHook = 0; }
        UnhookWindows();
        _taskbar?.Dispose();
        _taskbar = null;
    }

    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumWindowsProc(nint window, nint param);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("shcore.dll", EntryPoint = "GetDpiForMonitor")] private static extern int GetDpiForMonitorNative(nint monitor, uint type, out uint x, out uint y);
    const int StyleIndex = -16, ExStyleIndex = -20;
    const uint FrameStyles = 0x00CF0000, MaximizedStyle = 0x01000000;
    const uint FrameExStyles = 0x00020301; // DLGMODALFRAME, WINDOWEDGE, CLIENTEDGE, STATICEDGE.
    const uint ExtendedFrameBoundsAttribute = 9, CornerPreferenceAttribute = 33;
    const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 0x10;
    const uint FrameChanged = 0x20, NoOwnerZOrder = 0x200;
    const uint MinimizedStyle = 0x20000000, VisibleStyle = 0x10000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool EqualsRect(Rect r)
        { return Left == r.Left && Top == r.Top && Right == r.Right && Bottom == r.Bottom; }
        public bool Contains(Point p)
        { return p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom; }
        public bool Covers(Rect rect) => Left <= rect.Left && Top <= rect.Top && Right >= rect.Right && Bottom >= rect.Bottom;
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
        ITaskbarList2? instance;
        public TaskbarPolicy()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Fullscreen is unavailable. Restart Llampec and try again.");
            instance = (ITaskbarList2)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), true)!)!;
            try { Marshal.ThrowExceptionForHR(instance.HrInit()); }
            catch { Dispose(); throw; }
        }
        public void Mark(IntPtr window, bool fullscreen)
        { Marshal.ThrowExceptionForHR(instance!.MarkFullscreenWindow(window, fullscreen)); }
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
        readonly Rect monitor, originalWork, originalBounds;
        readonly uint originalDpi;
        readonly TaskbarPolicy taskbar;
        readonly string property = "Llampec.Fullscreen." + Guid.NewGuid().ToString("N");
        readonly IntPtr token = new IntPtr(Process.GetCurrentProcess().Id);
        bool claimed, restored, markingAttempted, cornerPreferenceChanged, geometryCalibrated;
        nint originalRegion;
        Rect savedRegionBounds;
        uint savedRegionDpi;
        int originalRegionType;
        bool clippingApplied;
        Rect coverBounds;
        public Rect Monitor { get { return monitor; } }
        public IntPtr MonitorHandle { get { return monitorHandle; } }
        public IntPtr Window { get { return window; } }

        public Trial(IntPtr target, TaskbarPolicy policy)
        {
            window = target; taskbar = policy;
            if (!IsWindow(window) || !IsWindowVisible(window) || IsHungAppWindow(window))
                throw new InvalidOperationException("The window is unavailable or not responding.");
            threadId = GetWindowThreadProcessId(window, out processId);
            if (threadId == 0) throw new Win32Exception();
            originalStyle = ReadStyle(window, StyleIndex);
            originalExStyle = ReadStyle(window, ExStyleIndex);
            hasCornerPreference = TryReadDwmValue(window, CornerPreferenceAttribute, out originalCornerPreference);
            if (GetProp(window, "NonRudeHWND") != IntPtr.Zero)
                throw new InvalidOperationException("This window uses an incompatible taskbar policy.");
            originalPlacement = ReadPlacement(window);
            monitorHandle = MonitorFromWindow(window, 2);
            MonitorInfo originalMonitor = ReadMonitor(monitorHandle);
            monitor = originalMonitor.Monitor;
            originalWork = originalMonitor.Work;
            originalDpi = GetDpiForWindow(window);
            savedRegionDpi = originalDpi;
            Rect bounds;
            if (!GetWindowRect(window, out bounds)) throw new Win32Exception();
            originalBounds = bounds;
            savedRegionBounds = bounds;
            if (bounds.EqualsRect(monitor))
                throw new InvalidOperationException("This window already occupies the entire monitor. Exit its fullscreen mode first.");
            if (!SetProp(window, property, token)) throw new Win32Exception();
            claimed = true;
            originalRegion = CreateRectRgn(0, 0, 0, 0);
            if (originalRegion == 0)
            {
                RemoveProp(window, property); claimed = false;
                throw new Win32Exception();
            }
            originalRegionType = GetWindowRgn(window, originalRegion);
            if (originalRegionType == 0)
            {
                DeleteObject(originalRegion);
                originalRegion = 0;
            }
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
            if (!OwnsWindow()) throw new InvalidOperationException("This window is no longer available.");
        }
        public bool MonitorUnchanged()
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            return MonitorFromWindow(window, 2) == monitorHandle && GetMonitorInfo(monitorHandle, ref info) && info.Monitor.EqualsRect(monitor);
        }

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
            if (IsHungAppWindow(window)) throw new InvalidOperationException("The window is not responding. Try again when it responds.");
            if (!SetWindowPos(window, IntPtr.Zero, expected.Left, expected.Top,
                expected.Width, expected.Height,
                NoZOrder | NoOwnerZOrder | NoActivate | FrameChanged)) throw new Win32Exception();
            RequireOwnership();
            Rect actual;
            if (!GetWindowRect(window, out actual)) throw new Win32Exception();
            if (!actual.EqualsRect(expected))
                throw new InvalidOperationException("This window did not accept fullscreen bounds.");
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
                Rect client = ReadCoverageBounds(window);
                coverBounds = CompensateCoverageBounds(monitor, visible, client);
                geometryCalibrated = true;
            }
            SetRawBounds(ExpectedBounds(shortened));
            if (!shortened && (!ReadDwmVisibleBounds(window).Covers(monitor) || !ReadCoverageBounds(window).Covers(monitor)))
                throw new InvalidOperationException("This app did not cover the entire monitor.");
            ApplyMonitorClip(ExpectedBounds(shortened));
        }
        void ApplyMonitorClip(Rect raw)
        {
            RequireOwnership();
            // Client padding is outside the selected monitor after compensation. Clip that
            // overscan so it cannot paint a sliver over an adjacent physical display.
            bool mirrored = (ReadStyle(window, ExStyleIndex) & 0x00400000) != 0;
            int left = mirrored ? raw.Right - monitor.Right : monitor.Left - raw.Left;
            int right = mirrored ? raw.Right - monitor.Left : monitor.Right - raw.Left;
            nint region = CreateRectRgn(left, monitor.Top - raw.Top, right,
                Math.Min(monitor.Bottom, raw.Bottom) - raw.Top);
            if (region == 0) throw new Win32Exception();
            if (SetWindowRgn(window, region, true) == 0)
            {
                DeleteObject(region); // Windows only takes ownership after success.
                throw new Win32Exception();
            }
            clippingApplied = true;
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
                if (originalRegion != 0) { DeleteObject(originalRegion); originalRegion = 0; }
                claimed = false; restored = true;

                return false;
            }
            var failures = new List<string>();
            bool keepMinimized = IsIconic(window), keepVisible = IsWindowVisible(window);
            bool wasMaximized = (originalStyle & MaximizedStyle) != 0;
            Rect expectedNormal = originalPlacement.NormalPosition;
            MonitorInfo currentMonitor = ReadMonitor(MonitorFromWindow(window, 2));
            bool layoutChanged = !currentMonitor.Monitor.EqualsRect(monitor)
                || !currentMonitor.Work.EqualsRect(originalWork) || GetDpiForWindow(window) != originalDpi;
            bool clipClearedForLayout = false;
            if (layoutChanged && clippingApplied)
            {
                // Remove our crop before the app processes its restored size. Chromium
                // regenerates its own region on WM_SIZE, using the new physical frame metrics.
                if (SetWindowRgn(window, 0, false) == 0) failures.Add(new Win32Exception().Message);
                else clipClearedForLayout = true;
            }
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
                else if (layoutChanged) expectedNormal = ReadPlacement(window).NormalPosition;
            }
            if (OwnsWindow() && wasMaximized && !keepMinimized)
            {
                try { WriteStyle(window, keepVisible ? originalStyle : originalStyle & ~VisibleStyle); }
                catch (Exception error) { failures.Add(error.Message); }
                Rect maximizedBounds = originalBounds;
                if (layoutChanged && OwnsWindow())
                {
                    // Let SetWindowPlacement choose an available display first. Recreate the
                    // saved maximize state there without using obsolete physical coordinates.
                    currentMonitor = ReadMonitor(MonitorFromWindow(window, 2));
                    maximizedBounds = AdaptMaximizedBounds(originalBounds, originalWork,
                        currentMonitor.Work, originalDpi, GetDpiForWindow(window));
                }
                if (OwnsWindow() && !SetWindowPos(window, IntPtr.Zero,
                    maximizedBounds.Left, maximizedBounds.Top, maximizedBounds.Width, maximizedBounds.Height,
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
            if (OwnsWindow() && clippingApplied)
            {
                // An app can rewrite its region on WM_SIZE. Restore only after its final sizing.
                bool appRegeneratedRegion = false;
                if (clipClearedForLayout)
                {
                    nint currentRegion = CreateRectRgn(0, 0, 0, 0);
                    if (currentRegion != 0)
                    {
                        appRegeneratedRegion = GetWindowRgn(window, currentRegion) > 0;
                        DeleteObject(currentRegion);
                    }
                }
                if (!appRegeneratedRegion && layoutChanged && originalRegion != 0 && GetWindowRect(window, out Rect resized)
                    && (resized.Width != savedRegionBounds.Width || resized.Height != savedRegionBounds.Height))
                {
                    try
                    {
                        nint adapted;
                        if (originalRegionType == 2 && GetRgnBox(originalRegion, out Rect box) > 0)
                        {
                            Rect adaptedBox = AdaptRegionBounds(box, savedRegionBounds, resized, savedRegionDpi, GetDpiForWindow(window));
                            adapted = CreateRectRgn(adaptedBox.Left, adaptedBox.Top, adaptedBox.Right, adaptedBox.Bottom);
                            if (adapted == 0) throw new Win32Exception();
                        }
                        else adapted = ScaleRegion(originalRegion, savedRegionBounds, resized);
                        DeleteObject(originalRegion);
                        originalRegion = adapted;
                        savedRegionBounds = resized;
                        savedRegionDpi = GetDpiForWindow(window);
                    }
                    catch (Exception error) { failures.Add(error.Message); }
                }
                nint copy = 0;
                if (!appRegeneratedRegion && originalRegion != 0)
                {
                    copy = CreateRectRgn(0, 0, 0, 0);
                    if (copy == 0 || CombineRgn(copy, originalRegion, 0, 5) == 0)
                    {
                        if (copy != 0) DeleteObject(copy);
                        copy = 0;
                        failures.Add("Could not copy the saved window region.");
                    }
                }
                if (!appRegeneratedRegion && (originalRegion == 0 || copy != 0))
                {
                    if (SetWindowRgn(window, copy, true) == 0)
                    {
                        if (copy != 0) DeleteObject(copy);
                        failures.Add(new Win32Exception().Message);
                    }
                }
            }
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
                        || !actual.NormalPosition.EqualsRect(expectedNormal)
                        || ReadStyle(window, ExStyleIndex) != originalExStyle)
                        failures.Add("The app did not retain its original frame/window placement.");
                    uint restoredCorner;
                    if (cornerPreferenceChanged && (!TryReadDwmValue(window, CornerPreferenceAttribute, out restoredCorner)
                        || restoredCorner != originalCornerPreference))
                        failures.Add("The app did not retain its original corner preference.");
                }
                catch (Exception error) { failures.Add(error.Message); }
                if (failures.Count == 0) RemoveProp(window, property);
            }
            if (failures.Count != 0)
                throw new InvalidOperationException("Could not fully restore the window.",
                    new InvalidOperationException(string.Join("; ", failures.ToArray())));
            claimed = false; restored = true;
            if (originalRegion != 0) { DeleteObject(originalRegion); originalRegion = 0; }
            return stillOwned;
        }
    }

    // A short dwell avoids accidental edge hits; a hold allows the bar's animation.
    // Keep the whole projected taskbar band usable, even while it is sliding in.
    sealed class EdgeState
    {
        public bool Revealed { get; private set; }
        long edgeSince = -1, outsideSince = -1, revealedAt;
        public void Reset() { Revealed = false; edgeSince = outsideSince = -1; }
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

    internal static Rect CompensateCoverageBounds(Rect raw, Rect visible, Rect client)
    {
        int left = Math.Max(visible.Left, client.Left) - raw.Left;
        int top = Math.Max(visible.Top, client.Top) - raw.Top;
        int right = raw.Right - Math.Min(visible.Right, client.Right);
        int bottom = raw.Bottom - Math.Min(visible.Bottom, client.Bottom);
        // Only compensate a small, measured non-client frame. Larger gaps or
        // bounds outside the HWND indicate unsupported/custom geometry.
        if (left < 0 || top < 0 || right < 0 || bottom < 0
            || left > 32 || top > 32 || right > 32 || bottom > 32
            || visible.Width <= 0 || visible.Height <= 0)
            throw new InvalidOperationException("This app has unsupported frame margins.");
        return new Rect { Left = raw.Left - left, Top = raw.Top - top,
            Right = raw.Right + right, Bottom = raw.Bottom + bottom };
    }
    internal static Rect AdaptMaximizedBounds(Rect original, Rect originalWork, Rect currentWork, uint originalDpi, uint currentDpi)
    {
        double scale = originalDpi == 0 || currentDpi == 0 ? 1 : (double)currentDpi / originalDpi;
        int Inset(int pixels) => (int)Math.Round(Math.Clamp(pixels, 0, 32) * scale);
        return new Rect
        {
            Left = currentWork.Left - Inset(originalWork.Left - original.Left),
            Top = currentWork.Top - Inset(originalWork.Top - original.Top),
            Right = currentWork.Right + Inset(original.Right - originalWork.Right),
            Bottom = currentWork.Bottom + Inset(original.Bottom - originalWork.Bottom)
        };
    }
    internal static Rect AdaptRegionBounds(Rect region, Rect originalBounds, Rect currentBounds, uint originalDpi, uint currentDpi)
    {
        double scale = originalDpi == 0 || currentDpi == 0 ? 1 : (double)currentDpi / originalDpi;
        int Inset(int pixels) => (int)Math.Round(Math.Max(0, pixels) * scale);
        return new Rect
        {
            Left = Inset(region.Left), Top = Inset(region.Top),
            Right = currentBounds.Width - Inset(originalBounds.Width - region.Right),
            Bottom = currentBounds.Height - Inset(originalBounds.Height - region.Bottom)
        };
    }
    private static nint ScaleRegion(nint region, Rect oldBounds, Rect newBounds)
    {
        uint bytes = GetRegionData(region, 0, 0);
        if (bytes == 0) throw new Win32Exception();
        nint data = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (GetRegionData(region, bytes, data) != bytes) throw new Win32Exception();
            var transform = new RegionTransform { M11 = (float)newBounds.Width / oldBounds.Width,
                M22 = (float)newBounds.Height / oldBounds.Height };
            nint scaled = ExtCreateRegion(ref transform, bytes, data);
            if (scaled == 0) throw new Win32Exception();
            return scaled;
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RegionTransform
    {
        public float M11, M12, M21, M22, Dx, Dy;
    }
    private static Rect ReadClientBounds(nint window)
    {
        if (!GetClientRect(window, out Rect client)) throw new Win32Exception();
        SetLastError(0);
        if (MapWindowPoints(window, 0, ref client, 2) == 0 && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
        return client;
    }
    private static Rect ReadCoverageBounds(nint window)
    {
        Rect client = ReadClientBounds(window), visible = ReadDwmVisibleBounds(window);
        uint style = ReadStyle(window, StyleIndex);
        // Genuine nonclient menus and scrollbars are app controls, not transparent resize
        // padding. Keep those edges visible instead of cropping the control off-screen.
        if (GetMenu(window) != 0) client.Top = visible.Top;
        if ((style & 0x00100000) != 0) client.Bottom = visible.Bottom;
        if ((style & 0x00200000) != 0)
        {
            // The bar can move to the opposite side under RTL mirroring too.
            // Conservatively retain both edges when native scrollbars are present.
            client.Left = visible.Left;
            client.Right = visible.Right;
        }
        return client;
    }

    static IntPtr EnterDpiContext()
    {
        if (IntPtr.Size != 8) throw new InvalidOperationException("Fullscreen is unavailable. Restart Llampec and try again.");
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
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] static extern nint GetMenu(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern int MapWindowPoints(nint from, nint to, ref Rect rect, uint count);
    [DllImport("gdi32.dll")] static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] static extern int CombineRgn(nint destination, nint source, nint unused, int operation);
    [DllImport("gdi32.dll")] static extern int GetRgnBox(nint region, out Rect bounds);
    [DllImport("gdi32.dll")] static extern uint GetRegionData(nint region, uint count, nint data);
    [DllImport("gdi32.dll")] static extern nint ExtCreateRegion(ref RegionTransform transform, uint count, nint data);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowRgn(nint window, nint region, bool redraw);
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
    [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeValue(IntPtr window, uint attribute, out uint value, uint size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeBounds(IntPtr window, uint attribute, out Rect bounds, uint size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, uint size);
}
