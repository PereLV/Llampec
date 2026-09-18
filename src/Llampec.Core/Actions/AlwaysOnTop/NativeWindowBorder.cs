using System.ComponentModel;
using Llampec.Diagnostics;
using Llampec.Interop;

namespace Llampec.Actions.AlwaysOnTop;

/// <summary>
/// A hollow, mouse-transparent native outline. Create, update and dispose on the same
/// thread with a message loop. Target WinEvents drive updates; there is no idle timer.
/// </summary>
public sealed partial class NativeWindowBorder : IAlwaysOnTopBorder
{
    private const uint RefreshMessage = 0x8000 + 81;
    private readonly nint _target;
    private readonly uint _threadId;
    private readonly string _className = "Llampec.AlwaysOnTop.Border." + Guid.NewGuid().ToString("N");
    private readonly User32.WndProc _windowProcedure;
    private readonly WinEventProcedure _eventProcedure;
    private readonly List<nint> _hooks = [];
    private nint _handle;
    private nint _brush;
    private bool _classRegistered;
    private bool _disposed;
    private bool _targetDestroyed;
    private bool _refreshQueued;
    private int _thickness = 3;
    private (int Width, int Height, int Thickness, int CornerRadius) _regionSize;

    public NativeWindowBorder(nint target)
    {
        _target = target;
        _threadId = Native.GetCurrentThreadId();
        _windowProcedure = WindowProcedure;
        _eventProcedure = OnWindowEvent;

        try
        {
            uint targetThread = Native.GetWindowThreadProcessId(target, out uint targetProcess);
            if (targetThread == 0 || targetProcess == 0)
                throw new ArgumentException("The border target is no longer a window.", nameof(target));

            RefreshBrush();
            var windowClass = new User32.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                hInstance = Kernel32.GetModuleHandle(null),
                lpszClassName = Marshal.StringToHGlobalUni(_className),
            };
            try
            {
                if (User32.RegisterClassEx(in windowClass) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not register the border window.");
                _classRegistered = true;
            }
            finally
            {
                Marshal.FreeHGlobal(windowClass.lpszClassName);
            }

            // Layered + transparent makes mouse input pass through across process/thread
            // boundaries, unlike HTTRANSPARENT alone. Ownership keeps the outline above
            // its target and on that window's desktop without changing target activation.
            const uint extendedStyle = 0x00080000 /* LAYERED */ | 0x00000020 /* TRANSPARENT */
                | 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
            _handle = User32.CreateWindowEx(extendedStyle, _className, "Llampec window border",
                0x80000000 /* WS_POPUP */, 0, 0, 0, 0, target, 0, windowClass.hInstance, 0);
            if (_handle == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the border window.");
            if (!Native.SetLayeredWindowAttributes(_handle, 0, 255, 0x00000002 /* LWA_ALPHA */))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not make the border mouse-transparent.");

            // The hollow window region supplies the shape, not DWM's non-client border.
            Dwm.SetInt(_handle, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, Dwm.DWMWCP_DONOTROUND);
            Dwm.SetUInt(_handle, Dwm.DWMWA_BORDER_COLOR, Dwm.DWMWA_COLOR_NONE);

            AddHook(0x8001, 0x800B, targetProcess, targetThread); // destroy/show/hide, reorder/state/location
            AddHook(0x8017, 0x8018, targetProcess, targetThread); // cloak/uncloak (virtual desktops)
            AddHook(0x0016, 0x0017, targetProcess, targetThread); // minimize/restore
            AddHook(0x000A, 0x000B, targetProcess, targetThread); // move/size start/end
            RefreshGeometry();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Update(int thickness)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thickness = Math.Clamp(thickness, 1, 8);
        RefreshBrush();
        RefreshGeometry();
    }

    private void AddHook(uint first, uint last, uint process, uint thread)
    {
        nint hook = Native.SetWinEventHook(first, last, 0,
            Marshal.GetFunctionPointerForDelegate(_eventProcedure), process, thread, 0 /* OUTOFCONTEXT */);
        if (hook == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not track the pinned window's border.");
        _hooks.Add(hook);
    }

    private void OnWindowEvent(nint hook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        // Ignore child accessibility objects and our own outline, including events
        // generated while refreshing it. Posting also coalesces move/resize bursts
        // and avoids recursively changing windows from a reentrant WinEvent callback.
        if (_disposed || window != _target || objectId != 0 || childId != 0)
            return;
        if (eventType == 0x8001)
            _targetDestroyed = true;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_disposed || _handle == 0 || _refreshQueued)
            return;
        _refreshQueued = User32.PostMessage(_handle, RefreshMessage, 0, 0);
    }

    private void RefreshGeometry()
    {
        if (_disposed || _handle == 0)
            return;
        if (_targetDestroyed || !Native.IsWindow(_target) || !Native.IsWindowVisible(_target)
            || (User32.GetWindowLongPtr(_target, User32.GWL_EXSTYLE) & 0x00000008 /* TOPMOST */) == 0
            || Native.IsIconic(_target)
            || (Native.DwmGetWindowAttribute(_target, 14 /* CLOAKED */, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0))
        {
            Native.ShowWindow(_handle, 0 /* SW_HIDE */);
            return;
        }

        bool hasDwmFrame = Native.DwmGetWindowAttribute(_target, 9 /* EXTENDED_FRAME_BOUNDS */, out User32.RECT frame,
            (uint)Marshal.SizeOf<User32.RECT>()) == 0;
        if (!hasDwmFrame && !Native.GetWindowRect(_target, out frame))
        {
            Native.ShowWindow(_handle, 0);
            return;
        }

        nint monitor = Native.MonitorFromWindow(_target, User32.MONITOR_DEFAULTTONEAREST);
        double scale = User32.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        int pixels = Math.Max(1, (int)Math.Round(_thickness * scale));
        bool maximized = Native.IsZoomed(_target);
        bool arranged = Native.IsWindowArranged(_target);
        int cornerRadius = GetTargetCornerRadius(scale, maximized || arranged);
        bool inset = maximized || arranged;
        if (inset)
        {
            // An external outline would disappear beyond the screen on maximized
            // or snapped windows. Keep the thin perimeter inside their visible frame.
            var info = new User32.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEXW>() };
            if (User32.GetMonitorInfo(monitor, ref info))
            {
                frame.Left = Math.Max(frame.Left, info.rcMonitor.Left);
                frame.Top = Math.Max(frame.Top, info.rcMonitor.Top);
                frame.Right = Math.Min(frame.Right, info.rcMonitor.Right);
                frame.Bottom = Math.Min(frame.Bottom, info.rcMonitor.Bottom);
            }
        }

        uint nativeBorder = 0;
        if (hasDwmFrame && !inset && Native.DwmGetWindowAttribute(_target,
            37 /* VISIBLE_FRAME_BORDER_THICKNESS */, out uint measuredBorder, sizeof(uint)) == 0)
            nativeBorder = measuredBorder;
        (frame, cornerRadius) = CalculateOutlineGeometry(frame, pixels, cornerRadius, nativeBorder, inset);

        if (frame.Width <= pixels * 2 || frame.Height <= pixels * 2)
        {
            Native.ShowWindow(_handle, 0);
            return;
        }

        var size = (frame.Width, frame.Height, pixels, cornerRadius);
        if (_regionSize != size)
        {
            if (!SetHollowRegion(frame.Width, frame.Height, pixels, cornerRadius))
            {
                // Never show a solid rectangle if shape creation fails.
                Native.ShowWindow(_handle, 0);
                return;
            }
            _regionSize = size;
        }

        // NOZORDER + NOOWNERZORDER is intentional: moving/redrawing the outline must
        // never bring its target (or its outline) above another pinned window.
        if (!User32.SetWindowPos(_handle, 0, frame.Left, frame.Top, frame.Width, frame.Height,
            User32.SWP_NOACTIVATE | User32.SWP_NOZORDER | 0x0200 /* NOOWNERZORDER */ | 0x0040 /* SHOWWINDOW */))
        {
            Native.ShowWindow(_handle, 0);
            return;
        }
        Native.InvalidateRect(_handle, 0, false);
    }

    /// <summary>Positions the stroke against the inside edge of DWM's native border, in physical pixels.</summary>
    internal static (User32.RECT Bounds, int InnerCornerRadius) CalculateOutlineGeometry(
        User32.RECT visibleFrame, int thickness, int targetCornerRadius, uint nativeBorderThickness, bool inset)
    {
        if (inset)
            return (visibleFrame, 0);

        // Extended frame bounds include the native DWM border. Leaving that border
        // inside our hollow cutout produces a differently colored seam, especially
        // at scaled DPI. Cover that measured margin while retaining the selected
        // stroke width: inner = inset(frame, B); outer = inflate(frame, T - B).
        // DWM already reports B in physical pixels; scaling it again is incorrect.
        // If no native border is reported, keep direct adjacency without guessed padding.
        // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
        int maximumInset = Math.Max(0, Math.Min(visibleFrame.Width, visibleFrame.Height) / 2 - 1);
        int overlap = (int)Math.Min(nativeBorderThickness, (uint)maximumInset);
        int expansion = thickness - overlap;
        visibleFrame.Left -= expansion;
        visibleFrame.Top -= expansion;
        visibleFrame.Right += expansion;
        visibleFrame.Bottom += expansion;
        return (visibleFrame, Math.Max(0, targetCornerRadius - overlap));
    }

    private int GetTargetCornerRadius(double scale, bool fillsLayout)
    {
        // Windows exposes the requested corner policy, not the rendered radius.
        // Use its documented Windows 11 geometry: 8 DIP, or 4 DIP for ROUNDSMALL.
        // Maximized/snapped windows and apps with custom regions are not DWM-rounded.
        // https://learn.microsoft.com/windows/apps/desktop/modernize/ui/apply-rounded-corners
        if (fillsLayout || Native.GetWindowRgnBox(_target, out _) != 0
            || User32.GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0)
            return 0;

        _ = Native.DwmGetWindowAttribute(_target, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE,
            out uint preference, sizeof(uint));
        if (preference == 1 /* DONOTROUND */)
            return 0;

        // Layered application windows may draw their own nonrectangular alpha mask;
        // that shape cannot be inferred from a DWM rounding preference.
        if ((User32.GetWindowLongPtr(_target, User32.GWL_EXSTYLE) & 0x00080000 /* LAYERED */) != 0)
            return 0;

        int radiusDip = preference switch
        {
            2 => 8, // ROUND
            3 => 4, // ROUNDSMALL
            _ => 0,
        };
        if (preference == 0 /* DEFAULT */)
        {
            long style = User32.GetWindowLongPtr(_target, User32.GWL_STYLE).ToInt64();
            bool hasCaption = (style & 0x00C00000 /* CAPTION */) == 0x00C00000;
            bool hasResizeFrame = (style & 0x00040000 /* THICKFRAME */) != 0;
            // DWM rounds ordinary framed windows by default, including modern apps
            // that draw their title bar themselves but retain these frame styles.
            // Borderless/custom-composited windows remain a best-effort square fallback.
            if (hasCaption || hasResizeFrame)
                radiusDip = 8;
        }
        return (int)Math.Round(radiusDip * scale);
    }

    private bool SetHollowRegion(int width, int height, int thickness, int cornerRadius)
    {
        nint region = CreateHollowRegion(width, height, thickness, cornerRadius);
        if (region == 0)
            return false;
        if (Native.SetWindowRgn(_handle, region, true) != 0)
            return true; // Ownership transfers ONLY on success; Windows frees replacements.
        Native.DeleteObject(region);
        return false;
    }

    /// <summary>Creates a hollow outline in physical pixels. The caller owns the returned region.</summary>
    internal static nint CreateHollowRegion(int width, int height, int thickness, int cornerRadius)
    {
        if (thickness <= 0 || width <= 2 * thickness || height <= 2 * thickness)
            return 0;
        int innerRadius = Math.Clamp(cornerRadius, 0, Math.Min(width, height) / 2 - thickness);
        int outerRadius = innerRadius == 0 ? 0 : innerRadius + thickness;

        // Both contours share their corner centers. Enlarging the outer radius by
        // exactly the stroke thickness gives an even arc around the target's rounded
        // cutout. A square inner cutout leaves the visible border square at its joins.
        nint outer = CreateContour(0, 0, width, height, outerRadius);
        nint inner = CreateContour(thickness, thickness, width - thickness, height - thickness, innerRadius);
        try
        {
            if (outer == 0 || inner == 0 || Native.CombineRgn(outer, outer, inner, 4 /* RGN_DIFF */) == 0)
                return 0;
            nint result = outer;
            outer = 0;
            return result;
        }
        finally
        {
            if (outer != 0) Native.DeleteObject(outer);
            if (inner != 0) Native.DeleteObject(inner);
        }
    }

    private static nint CreateContour(int left, int top, int right, int bottom, int radius) => radius == 0
        ? Native.CreateRectRgn(left, top, right, bottom)
        // GDI's rounded rectangle omits its lower/right endpoint. Compensate for that
        // on both contours so left/right and top/bottom strips have equal thickness.
        : Native.CreateRoundRectRgn(left, top, right + 1, bottom + 1, radius * 2, radius * 2);

    private void RefreshBrush()
    {
        uint color = Native.GetSysColor(13 /* COLOR_HIGHLIGHT */);
        var contrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        bool highContrast = Native.SystemParametersInfo(0x0042 /* SPI_GETHIGHCONTRAST */,
            contrast.Size, ref contrast, 0) && (contrast.Flags & 1) != 0;
        if (!highContrast && Dwm.DwmGetColorizationColor(out uint argb, out _) == 0)
            color = ((argb >> 16) & 0xFF) | (argb & 0xFF00) | ((argb & 0xFF) << 16); // COLORREF is BGR

        nint brush = Native.CreateSolidBrush(color);
        if (brush == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the border brush.");
        nint previous = _brush;
        _brush = brush;
        if (previous != 0) Native.DeleteObject(previous);
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case RefreshMessage:
                    _refreshQueued = false;
                    RefreshGeometry();
                    return 0;
                case 0x000F: // WM_PAINT: BeginPaint's DC is clipped to our hollow region.
                    nint dc = Native.BeginPaint(window, out PaintStruct paint);
                    try
                    {
                        if (dc != 0 && _brush != 0)
                            Native.FillRect(dc, in paint.Paint, _brush);
                    }
                    finally
                    {
                        Native.EndPaint(window, in paint);
                    }
                    return 0;
                case 0x0014: // WM_ERASEBKGND
                    return 1;
                case 0x0084: // WM_NCHITTEST
                    return -1; // HTTRANSPARENT
                case 0x0021: // WM_MOUSEACTIVATE
                    return 3; // MA_NOACTIVATE
                case 0x001A: // WM_SETTINGCHANGE
                case 0x031A: // WM_THEMECHANGED
                case 0x0320: // WM_DWMCOLORIZATIONCOLORCHANGED
                    if (!_disposed)
                        RefreshBrush();
                    QueueRefresh();
                    return 0;
                case 0x02E0: // WM_DPICHANGED
                case 0x007E: // WM_DISPLAYCHANGE
                    QueueRefresh();
                    return 0;
                case 0x0047: // WM_WINDOWPOSCHANGED
                    // Removing TOPMOST from an owner also changes its owned windows'
                    // stacking, without necessarily emitting a target WinEvent. Hide
                    // on that notification too. The visibility guard avoids reposting
                    // forever when RefreshGeometry itself hides this window.
                    if (!_disposed && Native.IsWindowVisible(window)
                        && (User32.GetWindowLongPtr(_target, User32.GWL_EXSTYLE) & 0x00000008) == 0)
                        QueueRefresh();
                    break;
                case 0x0082: // WM_NCDESTROY: an owner can also destroy its owned windows.
                    if (_handle == window)
                        _handle = 0;
                    break;
            }
        }
        catch (Exception ex)
        {
            // No managed exception may cross a native callback boundary.
            Log.Error("Could not update the Always on Top border", ex);
        }
        return User32.DefWindowProc(window, message, wParam, lParam);
    }

    private void VerifyThread()
    {
        if (Native.GetCurrentThreadId() != _threadId)
            throw new InvalidOperationException("Native window borders must be accessed on their creating thread.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        VerifyThread();
        _disposed = true;
        foreach (nint hook in _hooks)
            Native.UnhookWinEvent(hook);
        _hooks.Clear();
        if (_handle != 0)
        {
            User32.DestroyWindow(_handle);
            _handle = 0;
        }
        if (_classRegistered)
        {
            User32.UnregisterClass(_className, Kernel32.GetModuleHandle(null));
            _classRegistered = false;
        }
        if (_brush != 0)
        {
            Native.DeleteObject(_brush);
            _brush = 0;
        }
        GC.KeepAlive(_windowProcedure);
        GC.KeepAlive(_eventProcedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProcedure(nint hook, uint eventType, nint window,
        int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PaintStruct
    {
        public nint DeviceContext;
        public int Erase;
        public User32.RECT Paint;
        public int Restore;
        public int IncrementalUpdate;
        public fixed byte Reserved[32];
    }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll")]
        public static partial uint GetCurrentThreadId();
        [LibraryImport("user32.dll")]
        public static partial uint GetWindowThreadProcessId(nint window, out uint process);
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint SetWinEventHook(uint first, uint last, nint module, nint callback, uint process, uint thread, uint flags);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnhookWinEvent(nint hook);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindow(nint window);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindowVisible(nint window);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsIconic(nint window);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsZoomed(nint window);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindowArranged(nint window);
        [LibraryImport("user32.dll")]
        public static partial int GetWindowRgnBox(nint window, out User32.RECT rectangle);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShowWindow(nint window, int command);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetWindowRect(nint window, out User32.RECT rectangle);
        [LibraryImport("user32.dll")]
        public static partial nint MonitorFromWindow(nint window, uint flags);
        [LibraryImport("dwmapi.dll")]
        public static partial int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);
        [LibraryImport("dwmapi.dll")]
        public static partial int DwmGetWindowAttribute(nint window, uint attribute, out User32.RECT value, uint size);
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
        [LibraryImport("user32.dll")]
        public static partial int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
        [LibraryImport("gdi32.dll")]
        public static partial nint CreateRectRgn(int left, int top, int right, int bottom);
        [LibraryImport("gdi32.dll")]
        public static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
        [LibraryImport("gdi32.dll")]
        public static partial int CombineRgn(nint destination, nint first, nint second, int mode);
        [LibraryImport("gdi32.dll", SetLastError = true)]
        public static partial nint CreateSolidBrush(uint color);
        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DeleteObject(nint item);
        [LibraryImport("user32.dll")]
        public static partial uint GetSysColor(int index);
        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SystemParametersInfo(uint action, uint parameter, ref HighContrast contrast, uint flags);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool InvalidateRect(nint window, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);
        [LibraryImport("user32.dll")]
        public static partial nint BeginPaint(nint window, out PaintStruct paint);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool EndPaint(nint window, in PaintStruct paint);
        [LibraryImport("user32.dll")]
        public static partial int FillRect(nint deviceContext, in User32.RECT rectangle, nint brush);
    }
}
