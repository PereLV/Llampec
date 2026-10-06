using System.Text;
using Llampec.Interop;

namespace Llampec.Platform;

/// <summary>Panel placement leaves room for a Windows taskbar even while it is hidden.</summary>
public static class PanelPlacement
{
    /// <summary>
    /// Read shell geometry only when positioning the panel. No taskbar preferences, shell
    /// window states, timers or hooks are changed. Rectangles use physical pixels; scale is DPI/96.
    /// </summary>
    public static User32.RECT ReserveTaskbarArea(nint monitorHandle, User32.RECT monitor,
        User32.RECT work, double scale)
    {
        if (monitorHandle == 0 || monitor.Width <= 0 || monitor.Height <= 0) return work;
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            var autoHideBars = new Dictionary<nint, TaskbarEdge>();
            // A hidden HWND can overlap the next monitor. Ask the documented per-monitor
            // appbar API for its owning edge instead of relying on MonitorFromWindow alone.
            // https://learn.microsoft.com/windows/win32/shell/abm-getautohidebarex
            for (uint edge = 0; edge < 4; edge++)
            {
                var data = new Shell32.APPBARDATA
                {
                    cbSize = (uint)Marshal.SizeOf<Shell32.APPBARDATA>(), uEdge = edge, rc = monitor
                };
                nint bar = (nint)Shell32.SHAppBarMessage(Shell32.ABM_GETAUTOHIDEBAREX, ref data);
                if (bar != 0) autoHideBars[bar] = (TaskbarEdge)edge;
            }
            var primary = new Shell32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Shell32.APPBARDATA>() };
            bool hasPrimaryBounds = Shell32.SHAppBarMessage(Shell32.ABM_GETTASKBARPOS, ref primary) != 0;
            var bars = new List<TaskbarBounds>();
            var className = new StringBuilder(64);
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                GetClassName(window, className, className.Capacity);
                string name = className.ToString();
                if (name is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return true;
                if (!GetWindowRect(window, out User32.RECT bounds)) return true;
                TaskbarEdge? owningEdge = null;
                User32.RECT? primaryBounds = null;
                if (autoHideBars.TryGetValue(window, out TaskbarEdge knownEdge))
                {
                    owningEdge = knownEdge;
                    if (name == "Shell_TrayWnd" && hasPrimaryBounds
                        && InferEdge(monitor, primary.rc) == knownEdge)
                        primaryBounds = primary.rc;
                }
                else if (name == "Shell_TrayWnd" && hasPrimaryBounds)
                {
                    // ABM_GETTASKBARPOS guarantees rc, not uEdge. Its rectangle can also
                    // describe the expanded bar when the visible HWND is collapsed.
                    // If it identifies another monitor, do not fall through to the hidden
                    // HWND's geometry: that HWND can lie wholly inside the next display.
                    // https://learn.microsoft.com/windows/win32/shell/abm-gettaskbarpos
                    if (SelectUnregisteredEdge(monitor, work, bounds, primary.rc) is { } primaryEdge)
                    {
                        owningEdge = primaryEdge;
                        primaryBounds = primary.rc;
                    }
                }
                else if (MonitorFromWindow(window, User32.MONITOR_DEFAULTTONEAREST) == monitorHandle
                    && SelectUnregisteredEdge(monitor, work, bounds) is { } edge)
                    owningEdge = edge;
                if (owningEdge is not { } selectedEdge) return true;
                User32.RECT? buttons = ReadTaskbarButtonBounds(window, name);
                TaskbarBounds selected = SelectTaskbarBounds(bounds, primaryBounds, buttons, selectedEdge, scale);
                bars.Add(selected);
#if DEBUG
                Llampec.Diagnostics.Log.Info($"Panel taskbar bounds: edge={selectedEdge}, host={FormatRect(bounds)}, "
                    + $"buttons={(buttons is { } buttonBounds ? FormatRect(buttonBounds) : "unavailable")}, selected={FormatRect(selected.Bounds)}.");
#endif
                return true;
            }, 0);
            // The tablet setting is a preference, not the current posture: Explorer can
            // still show a conventional bar while a keyboard is attached. Use shell
            // geometry instead of enlarging that bar from its configured mode.
            User32.RECT reserved = ReserveTaskbarArea(monitor, work, bars, scale);
#if DEBUG
            // No window titles or background sampling are needed.
            string barGeometry = string.Join("; ", bars.Select(bar =>
                $"{bar.Edge?.ToString() ?? "unknown"}:{bar.Bounds.Width}x{bar.Bounds.Height}@{FormatRect(bar.Bounds)}"));
            Llampec.Diagnostics.Log.Info($"Panel placement: monitor={FormatRect(monitor)}, "
                + $"nativeWork={FormatRect(work)}, primary={(hasPrimaryBounds ? FormatRect(primary.rc) : "unavailable")}, "
                + $"bars=[{barGeometry}], reserved={FormatRect(reserved)}, scale={scale.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}.");
#endif
            return reserved;
        }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
    }

    internal enum TaskbarEdge { Left, Top, Right, Bottom }
    internal readonly record struct TaskbarBounds(User32.RECT Bounds, TaskbarEdge? Edge = null);

    private static User32.RECT? ReadTaskbarButtonBounds(nint window, string kind)
    {
        // Explorer's tray host can include a transparent band that both GetWindowRect
        // and ABM_GETTASKBARPOS report. Read only the known native button container.
        // These shell class paths are implementation details: if Windows changes them,
        // missing or implausible geometry falls back to the full tray host.
        nint container = FindWindowEx(window, 0,
            kind == "Shell_TrayWnd" ? "ReBarWindow32" : "WorkerW", null);
        if (container == 0) return null;
        nint buttons = FindWindowEx(container, 0,
            kind == "Shell_TrayWnd" ? "MSTaskSwWClass" : "MSTaskListWClass", null);
        return buttons != 0 && IsWindowVisible(buttons) && GetWindowRect(buttons, out User32.RECT bounds)
            ? bounds : null;
    }

    internal static TaskbarBounds SelectTaskbarBounds(User32.RECT host, User32.RECT? primary,
        User32.RECT? buttons, TaskbarEdge edge, double scale)
    {
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        bool horizontal = edge is TaskbarEdge.Top or TaskbarEdge.Bottom;
        int hostThickness = horizontal ? host.Height : host.Width;
        if (buttons is { } content && host.Width > 0 && host.Height > 0
            && content.Width > 0 && content.Height > 0 && IsInsideMonitor(host, content))
        {
            int thickness = horizontal ? content.Height : content.Width;
            int length = horizontal ? content.Width : content.Height;
            long anchorDistance = edge switch
            {
                TaskbarEdge.Left => Math.Abs((long)content.Left - host.Left),
                TaskbarEdge.Top => Math.Abs((long)content.Top - host.Top),
                TaskbarEdge.Right => Math.Abs((long)content.Right - host.Right),
                _ => Math.Abs((long)content.Bottom - host.Bottom)
            };
            if (thickness >= Math.Ceiling(32 * scale) && thickness <= hostThickness
                && length >= thickness && anchorDistance <= Math.Ceiling(2 * scale))
            {
                // Preserve the full cross-axis span and the hidden host's owning edge.
                // Only its measured thickness changes, even when the child lies wholly
                // off the monitor while Windows has slid the auto-hidden bar away.
                switch (edge)
                {
                    case TaskbarEdge.Left: host.Right = host.Left + thickness; break;
                    case TaskbarEdge.Top: host.Bottom = host.Top + thickness; break;
                    case TaskbarEdge.Right: host.Left = host.Right - thickness; break;
                    case TaskbarEdge.Bottom: host.Top = host.Bottom - thickness; break;
                }
                // The validated child wins over both HWND and primary appbar geometry;
                // retaining either padded rectangle would reintroduce its extra space.
                return new(host, edge);
            }
        }
        if (primary is { } nominal && nominal.Width > 0 && nominal.Height > 0
            && (horizontal ? nominal.Height : nominal.Width) > hostThickness)
            return new(nominal, edge);
        return new(host, edge);
    }

    internal static TaskbarEdge? SelectUnregisteredEdge(User32.RECT monitor, User32.RECT work,
        User32.RECT tray, User32.RECT? primaryBounds = null)
    {
        // A successful primary-taskbar query is authoritative even when its rectangle is
        // on a different display. Never reinterpret that bar's hidden HWND as a local bar.
        if (primaryBounds is { } primary)
            return IsInsideMonitor(monitor, primary) ? InferEdge(monitor, primary) : null;
        if (!IsInsideMonitor(monitor, tray) || InferEdge(monitor, tray) is not { } edge) return null;
        // Normal bars on other edges already reserve work area. Without that evidence, an
        // HWND near this monitor's top could be the upper monitor's hidden bottom bar.
        // Registered autohide bars bypass this fallback and carry their actual owning edge.
        return edge switch
        {
            TaskbarEdge.Bottom => edge,
            TaskbarEdge.Top when work.Top > monitor.Top => edge,
            TaskbarEdge.Left when work.Left > monitor.Left => edge,
            TaskbarEdge.Right when work.Right < monitor.Right => edge,
            _ => null
        };
    }

    /// <summary>Project a hidden/collapsed bar onto its owning monitor, then intersect with rcWork.</summary>
    internal static User32.RECT ReserveTaskbarArea(User32.RECT monitor, User32.RECT work,
        IEnumerable<TaskbarBounds> bars, double scale)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0) return work;
        User32.RECT originalWork = work;
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var measuredThickness = new int[4];
        foreach (var bar in bars)
        {
            var edge = bar.Edge ?? InferEdge(monitor, bar.Bounds);
            if (edge is null || bar.Bounds.Width <= 0 || bar.Bounds.Height <= 0) continue;
            bool horizontal = edge is TaskbarEdge.Top or TaskbarEdge.Bottom;
            int overlap = horizontal
                ? Math.Min(monitor.Right, bar.Bounds.Right) - Math.Max(monitor.Left, bar.Bounds.Left)
                : Math.Min(monitor.Bottom, bar.Bounds.Bottom) - Math.Max(monitor.Top, bar.Bounds.Top);
            if (overlap <= 0) continue;
            int thickness = horizontal ? bar.Bounds.Height : bar.Bounds.Width;
            int extent = horizontal ? monitor.Height : monitor.Width;
            if (thickness <= 0 || thickness >= extent / 2) continue;
            int index = (int)edge.Value;
            measuredThickness[index] = Math.Max(measuredThickness[index], thickness);
        }
        for (int index = 0; index < measuredThickness.Length; index++)
        {
            int thickness = measuredThickness[index];
            if (thickness == 0) continue;
            var edge = (TaskbarEdge)index;
            bool horizontal = edge is TaskbarEdge.Top or TaskbarEdge.Bottom;
            // A thin HWND strip may accompany an accurate expanded appbar rectangle.
            // Apply the fallback only after combining both measurements for this edge,
            // so that strip cannot enlarge a real conventional or touch taskbar.
            if (thickness < Math.Ceiling(32 * scale))
                thickness = Math.Max(thickness, (int)Math.Ceiling(48 * scale));
            int extent = horizontal ? monitor.Height : monitor.Width;
            if (thickness >= extent / 2) continue;
            switch (edge)
            {
                case TaskbarEdge.Left: work.Left = Math.Max(work.Left, monitor.Left + thickness); break;
                case TaskbarEdge.Top: work.Top = Math.Max(work.Top, monitor.Top + thickness); break;
                case TaskbarEdge.Right: work.Right = Math.Min(work.Right, monitor.Right - thickness); break;
                case TaskbarEdge.Bottom: work.Bottom = Math.Min(work.Bottom, monitor.Bottom - thickness); break;
            }
        }
        return work.Width > 0 && work.Height > 0 ? work : originalWork;
    }

    internal static TaskbarEdge? InferEdge(User32.RECT monitor, User32.RECT bar)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0 || bar.Width <= 0 || bar.Height <= 0) return null;
        bool horizontal = bar.Width >= bar.Height;
        int overlap = horizontal
            ? Math.Min(monitor.Right, bar.Right) - Math.Max(monitor.Left, bar.Left)
            : Math.Min(monitor.Bottom, bar.Bottom) - Math.Max(monitor.Top, bar.Top);
        if (overlap <= 0) return null;
        long center = horizontal ? (long)bar.Top + bar.Bottom : (long)bar.Left + bar.Right;
        long near = 2L * (horizontal ? monitor.Top : monitor.Left);
        long far = 2L * (horizontal ? monitor.Bottom : monitor.Right);
        long nearDistance = Math.Abs(center - near), farDistance = Math.Abs(center - far);
        int thickness = horizontal ? bar.Height : bar.Width;
        if (Math.Min(nearDistance, farDistance) > 2L * thickness + 4) return null;
        return horizontal
            ? nearDistance < farDistance ? TaskbarEdge.Top : TaskbarEdge.Bottom
            : nearDistance < farDistance ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    private static bool IsInsideMonitor(User32.RECT monitor, User32.RECT bounds)
        => bounds.Left >= monitor.Left && bounds.Top >= monitor.Top
            && bounds.Right <= monitor.Right && bounds.Bottom <= monitor.Bottom;

#if DEBUG
    private static string FormatRect(User32.RECT bounds)
        => $"({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom})";

#endif

    private delegate bool EnumWindowsProc(nint window, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out User32.RECT bounds);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
