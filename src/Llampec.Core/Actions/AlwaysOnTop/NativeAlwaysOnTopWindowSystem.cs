// Independent implementation from Microsoft Win32 documentation; no injected hooks or polling thread.
using System.Diagnostics;
using System.Text;
using Llampec.Diagnostics;

namespace Llampec.Actions.AlwaysOnTop;

internal sealed class NativeAlwaysOnTopWindowSystem : IAlwaysOnTopWindowSystem
{
    private const uint ForegroundEvent = 0x0003;
    private const uint ObjectCreate = 0x8000, ObjectDestroy = 0x8001, ObjectShow = 0x8002,
        ObjectHide = 0x8003, ObjectReorder = 0x8004, ObjectStateChange = 0x800A,
        ObjectLocationChange = 0x800B, ObjectNameChange = 0x800C, ObjectCloaked = 0x8017, ObjectUncloaked = 0x8018;
    private const long TopmostStyle = 0x00000008, ToolWindowStyle = 0x00000080,
        NoActivateStyle = 0x08000000, ChildStyle = 0x40000000;
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private readonly string _marker = "Llampec.AlwaysOnTop." + Guid.NewGuid().ToString("N");
    private readonly uint _processId = (uint)Environment.ProcessId;
    private readonly WinEventProc _callback;
    private nint _foregroundHook, _windowHook;
    private bool _disposed;

    public event Action<WindowChange>? WindowChanged;
    public bool IsTrackingForeground => _foregroundHook != 0;
    public nint ForegroundWindow => GetForegroundWindow();
    public string? LastError { get; private set; }

    public ForegroundWindowResolution ResolveForeground(nint capturedHandle)
    {
        if (capturedHandle == 0 || !IsWindow(capturedHandle))
            return new(null, false, "No eligible window is active.");
        GetWindowThreadProcessId(capturedHandle, out uint process);
        if (process == _processId) return new(null, true);
        // The foreground HWND can be an owned dialog or a native host. Resolve its
        // root and visible owner chain without ever choosing an unrelated background app.
        nint root = GetAncestor(capturedHandle, 2 /* GA_ROOT */);
        var visited = new HashSet<nint>();
        WindowSnapshot? firstEligible = null;
        for (nint handle = root; handle != 0 && visited.Add(handle); handle = GetWindow(handle, 4 /* GW_OWNER */))
        {
            if (Read(handle) is not { IsEligible: true } window) continue;
            // Owned dialogs inherit a parent's TOPMOST state. Toggle the pin that
            // Llampec actually owns when one exists in the active owner group.
            if (HasClaim(window.Identity)) return new(window, false);
            firstEligible ??= window;
        }
        if (firstEligible is not null) return new(firstEligible, false);
        long style = GetWindowLongPtr(capturedHandle, -20).ToInt64();
        var className = new StringBuilder(256);
        GetClassName(capturedHandle, className, className.Capacity);
        Log.Info($"Always on Top skipped foreground hwnd=0x{capturedHandle:X}; pid={process}; exstyle=0x{style:X}; class={className}");
        return new(null, false, "No eligible window is active.");
    }

    public NativeAlwaysOnTopWindowSystem()
    {
        _callback = OnWinEvent;
        // WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS. Callbacks use this thread's message loop.
        _foregroundHook = SetWinEventHook(ForegroundEvent, ForegroundEvent, 0, _callback, 0, 0, 2);
        _windowHook = SetWinEventHook(ObjectCreate, ObjectUncloaked, 0, _callback, 0, 0, 2);
        if (_foregroundHook == 0 || _windowHook == 0)
            Log.Warn($"Always on Top window tracking hook unavailable: {Marshal.GetLastWin32Error()}");
    }

    public WindowSnapshot? Read(nint handle)
    {
        if (handle == 0 || !IsWindow(handle)) return null;
        uint threadId = GetWindowThreadProcessId(handle, out uint processId);
        if (threadId == 0 || processId == 0) return null;
        var identity = new WindowIdentity(handle, processId, threadId);
        long extendedStyle = GetWindowLongPtr(handle, -20).ToInt64();
        var title = new StringBuilder(Math.Clamp(GetWindowTextLength(handle) + 1, 2, 4096));
        _ = GetWindowText(handle, title, title.Capacity);
        string caption = title.ToString().Trim();
        bool eligible = processId != _processId && IsWindowVisible(handle)
            && !string.IsNullOrWhiteSpace(caption)
            && (extendedStyle & (ToolWindowStyle | NoActivateStyle)) == 0
            && (GetWindowLongPtr(handle, -16).ToInt64() & ChildStyle) == 0
            && GetAncestor(handle, 2 /* GA_ROOT */) == handle
            && (DwmGetWindowAttribute(handle, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) != 0 || cloaked == 0);
        if (eligible)
        {
            var className = new StringBuilder(256);
            _ = GetClassName(handle, className, className.Capacity);
            eligible = className.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW"
                or "Shell_NotificationOverflowWindow" or "NotifyIconOverflowWindow" or "XamlExplorerHostIslandWindow")
                && !IsShellSurfaceProcess(processId);
        }
        // Destruction/recreation during inspection must not yield a mixed snapshot.
        return Matches(identity) ? new(identity, caption, eligible, (extendedStyle & TopmostStyle) != 0) : null;
    }

    private static bool IsShellSurfaceProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName is "StartMenuExperienceHost" or "ShellExperienceHost" or "SearchHost"
                or "SearchApp" or "TextInputHost" or "LockApp" or "Widgets" or "WidgetService";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<WindowSnapshot> Enumerate()
    {
        var windows = new List<WindowSnapshot>();
        _ = EnumWindows((handle, _) =>
        {
            if (Read(handle) is { } window) windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    public bool TryClaim(WindowIdentity identity)
    {
        LastError = null;
        var current = Read(identity.Handle);
        if (current is not { IsEligible: true, IsTopmost: false } || current.Identity != identity) return false;
        // Windows propagates TOPMOST changes through owner/owned-window groups. Decline a group
        // containing a preexisting topmost palette rather than later demoting another app's pin.
        if (HasPreexistingTopmostOwnedWindow(identity.Handle))
        {
            LastError = "This window has an always-on-top companion window. Close it before pinning.";
            Log.Warn("Always on Top target has a visible topmost owned window.");
            return false;
        }
        if (!SetProp(identity.Handle, _marker, 1))
        {
            SetOperationError("Could not pin this window.");
            return false;
        }
        return HasClaim(identity);
    }

    private static bool HasPreexistingTopmostOwnedWindow(nint handle)
    {
        nint rootOwner = GetAncestor(handle, 3 /* GA_ROOTOWNER */);
        if (rootOwner == 0) return true;
        if (rootOwner != handle && IsTopmost(rootOwner)) return true;
        var handles = new List<nint>();
        if (!EnumWindows((window, _) => { handles.Add(window); return true; }, 0)) return true;
        // Hidden tooltips and menus are commonly topmost too; they are not active
        // companion windows and must not prevent pinning the application.
        return HasTopmostOwnedWindow(rootOwner, handles, window => GetWindow(window, 4 /* GW_OWNER */),
            window => IsWindowVisible(window) && IsTopmost(window));
    }

    internal static bool HasTopmostOwnedWindow(nint rootOwner, IEnumerable<nint> windows,
        Func<nint, nint> ownerOf, Func<nint, bool> isTopmost)
    {
        foreach (nint window in windows)
        {
            if (window == rootOwner || !isTopmost(window)) continue;
            var visited = new HashSet<nint>();
            for (nint owner = ownerOf(window); owner != 0 && visited.Add(owner); owner = ownerOf(owner))
                if (owner == rootOwner) return true;
        }
        return false;
    }

    public bool HasClaim(WindowIdentity identity) => Matches(identity) && GetProp(identity.Handle, _marker) == 1;

    public void ReleaseClaim(WindowIdentity identity)
    {
        if (HasClaim(identity)) _ = RemoveProp(identity.Handle, _marker);
    }

    public bool TrySetTopmost(WindowIdentity identity, bool topmost)
    {
        LastError = null;
        if (!HasClaim(identity)) return false;
        // Do not take ownership if another actor pinned it after our preflight check.
        if (topmost && IsTopmost(identity.Handle))
        {
            ReleaseClaim(identity);
            return false;
        }
        if (IsHungAppWindow(identity.Handle))
        {
            LastError = "The window is not responding. Try again when it responds.";
            return false;
        }
        bool result = SetWindowPos(identity.Handle, topmost ? -1 : -2, 0, 0, 0, 0,
            NoSize | NoMove | NoActivate | NoOwnerZOrder);
        if (!result) SetOperationError(topmost ? "Could not pin this window." : "Could not unpin this window.");
        return result && HasClaim(identity) && IsTopmost(identity.Handle) == topmost;
    }

    private void SetOperationError(string fallback)
    {
        int error = Marshal.GetLastWin32Error();
        LastError = ErrorForNativeFailure(error, fallback);
        Log.Warn($"Always on Top native operation failed: {error}; {LastError}");
    }

    internal static string ErrorForNativeFailure(int error, string fallback) => error == 5
        ? "This window requires administrator permissions. Restart Llampec as administrator to pin it."
        : fallback;

    private static bool IsTopmost(nint handle) => (GetWindowLongPtr(handle, -20).ToInt64() & TopmostStyle) != 0;

    private static bool Matches(WindowIdentity identity)
    {
        uint thread = GetWindowThreadProcessId(identity.Handle, out uint process);
        return thread != 0 && process == identity.ProcessId && thread == identity.ThreadId;
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || hwnd == 0 || (eventType != ForegroundEvent && (objectId != 0 || childId != 0))) return;
        if (eventType is not (ForegroundEvent or ObjectCreate or ObjectDestroy or ObjectShow or ObjectHide or ObjectReorder
            or ObjectStateChange or ObjectLocationChange or ObjectNameChange or ObjectCloaked or ObjectUncloaked)) return;
        try { WindowChanged?.Invoke(new(hwnd, eventType == ForegroundEvent, eventType == ObjectLocationChange)); }
        catch (Exception ex) { Log.Warn($"Always on Top window event failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_foregroundHook != 0) _ = UnhookWinEvent(_foregroundHook);
        if (_windowHook != 0) _ = UnhookWinEvent(_windowHook);
        _foregroundHook = _windowHook = 0;
        GC.KeepAlive(_callback);
    }

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumWindowsProc(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int size);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out int value, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProp(nint handle, string name, nint data);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint handle, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint handle, string name);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
