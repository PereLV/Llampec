using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Llampec.Actions.AlwaysOnTop;
using Llampec.Settings;

internal static class Program
{
    private const uint NoActivate = 0x10, NoZOrder = 0x4;
    private const int ExStyle = -20;
    private static int _checks;
    private static nint _hostMainWindow;
    private static readonly Native.WndProc HostProcedure = HostWindowProcedure;

    [STAThread]
    public static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(-4);
        if (args.Length == 2 && args[0] == "--host") return RunHost(int.Parse(args[1]));
        try { RunTests(); Console.WriteLine($"PASS: {_checks} native checks"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void RunTests()
    {
        using var first = Host.Start(1);
        using var second = Host.Start(2);
        var preferences = new AlwaysOnTopSettings();
        using var service = new AlwaysOnTopService(() => preferences);
        Check(first.ProcessId != Environment.ProcessId && second.ProcessId != Environment.ProcessId,
            "Targets belong to external helper processes");
        var windows = service.GetWindows();
        Check(windows.Any(w => w.Handle == first.Handle) && windows.Any(w => w.Handle == second.Handle),
            "Native enumeration finds both external visible windows");
        Check(!IsTopmost(first.Handle) && !IsTopmost(second.Handle), "Helpers initially are not topmost");

        // Windows propagates TOPMOST changes through owner groups. Preserve a palette
        // that was already topmost before Llampec was asked to pin its owner.
        first.VerifyOwned();
        nint palette = Native.CreateWindowEx(0x08000088, "STATIC", "Llampec synthetic palette",
            0x90000000, 160, 160, 120, 60, first.Handle, 0, Native.GetModuleHandle(null), 0);
        Check(palette != 0 && IsTopmost(palette), "Synthetic owned palette starts topmost");
        try
        {
            service.GetWindows();
            service.Toggle(first.Handle);
            Check(service.Error is not null && service.PinnedCount == 0 && !IsTopmost(first.Handle),
                "Owner with preexisting topmost palette is declined");
            Check(IsTopmost(palette), "Declined owner pin preserves palette topmost state");
        }
        finally { if (palette != 0) Native.DestroyWindow(palette); }

        // Real apps retain hidden topmost tooltips/menus. Their existence must not
        // make every attempt to pin the owning application fail.
        first.VerifyOwned();
        nint hiddenPalette = Native.CreateWindowEx(0x08000088, "STATIC", "Llampec synthetic hidden tooltip",
            0x80000000, 160, 160, 120, 60, first.Handle, 0, Native.GetModuleHandle(null), 0);
        Check(hiddenPalette != 0 && IsTopmost(hiddenPalette) && !Native.IsWindowVisible(hiddenPalette),
            "Synthetic hidden tooltip starts topmost but invisible");
        try
        {
            service.GetWindows();
            service.Toggle(first.Handle);
            Check(service.Error is null && service.PinnedCount == 1 && IsTopmost(first.Handle),
                "Hidden topmost tooltip does not block its owner from being pinned");
            service.Toggle(first.Handle);
            Check(service.Error is null && service.PinnedCount == 0 && !IsTopmost(first.Handle),
                "Owner with hidden tooltip can be unpinned normally");
            Check(IsTopmost(hiddenPalette), "Unpin preserves the hidden tooltip's preexisting topmost state");
        }
        finally { if (hiddenPalette != 0) Native.DestroyWindow(hiddenPalette); }

        ValidateOwnedDialogResolution(first);
        service.GetWindows();

        first.VerifyOwned();
        nint foreground = Native.GetForegroundWindow();
        service.Toggle(first.Handle);
        Check(Native.GetForegroundWindow() == foreground, "Pin does not change the foreground window");
        Check(service.Error is null && service.PinnedCount == 1 && IsTopmost(first.Handle),
            "First window is pinned and tracked");
        WaitUntil(() => FindBorder(first.Handle) != 0, "Default border exists");
        nint border = FindBorder(first.Handle);
        ValidateBorder(first.Handle, border, preferences.BorderThickness);

        second.VerifyOwned();
        service.GetWindows();
        service.Toggle(second.Handle);
        Check(service.Error is null && service.PinnedCount == 2 && IsTopmost(second.Handle),
            "Multiple windows are pinned independently");
        WaitUntil(() => FindBorder(second.Handle) != 0, "Second pinned window has its own border");
        Check(service.GetWindows().Take(2).All(w => w.IsPinned), "Window selector puts owned pins first");

        preferences.ShowBorder = false;
        service.ApplyAppearance();
        Check(FindBorder(first.Handle) == 0 && FindBorder(second.Handle) == 0,
            "Disabling borders destroys both overlays");
        Check(IsTopmost(first.Handle) && IsTopmost(second.Handle), "Disabling borders preserves pins");
        preferences.ShowBorder = true;
        service.ApplyAppearance();
        WaitUntil(() => FindBorder(first.Handle) != 0 && FindBorder(second.Handle) != 0,
            "Re-enabling borders recreates both overlays");

        preferences.BorderThickness = 8;
        service.ApplyAppearance();
        border = FindBorder(first.Handle);
        ValidateBorder(first.Handle, border, 8);
        preferences.BorderThickness = 1;
        service.ApplyAppearance();
        ValidateBorder(first.Handle, border, 1);

        first.VerifyOwned();
        Check(Native.SetWindowPos(first.Handle, 0, 130, 190, 480, 290, NoActivate | NoZOrder),
            "Synthetic target moves and resizes");
        WaitUntil(() => Native.GetWindowRect(first.Handle, out var r) && r.Left == 130 && r.Top == 190
            && r.Width == 480 && r.Height == 290 && GeometryMatches(first.Handle, border, 1),
            "Native location events reposition the border after move and resize");
        first.VerifyOwned();
        Native.ShowWindowAsync(first.Handle, 7); // SW_SHOWMINNOACTIVE
        WaitUntil(() => Native.IsIconic(first.Handle) && !Native.IsWindowVisible(border),
            "Minimizing target hides its border through native events");
        Check(service.PinnedCount == 2, "Minimization preserves pin ownership");
        first.VerifyOwned();
        Native.ShowWindowAsync(first.Handle, 4); // SW_SHOWNOACTIVATE
        WaitUntil(() => !Native.IsIconic(first.Handle) && Native.IsWindowVisible(border)
            && GeometryMatches(first.Handle, border, 1), "Restoring target restores border geometry");

        second.Close();
        WaitUntil(() => !Native.IsWindow(second.Handle) && service.PinnedCount == 1,
            "Destroy notification automatically removes the closed window pin");
        Check(FindBorder(second.Handle) == 0, "Destroying a pinned target cleans up its border");

        first.VerifyOwned();
        service.GetWindows();
        foreground = Native.GetForegroundWindow();
        service.Toggle(first.Handle);
        Check(Native.GetForegroundWindow() == foreground, "Unpin does not change the foreground window");
        Check(service.PinnedCount == 0 && !IsTopmost(first.Handle) && FindBorder(first.Handle) == 0,
            "Unpin restores original z-order status and removes border");

        first.VerifyOwned();
        service.GetWindows();
        service.Toggle(first.Handle);
        Check(service.PinnedCount == 1 && IsTopmost(first.Handle), "Window can be pinned again");
        service.Dispose();
        Check(service.PinnedCount == 0 && !IsTopmost(first.Handle) && FindBorder(first.Handle) == 0,
            "Normal disposal restores owned topmost state and destroys borders");
    }

    private static void ValidateOwnedDialogResolution(Host host)
    {
        host.VerifyOwned(host.DialogHandle);
        host.VerifyOwned(host.DialogChildHandle);
        nint foreground = Native.GetForegroundWindow();
        Native.ShowWindowAsync(host.DialogHandle, 4 /* SW_SHOWNOACTIVATE */);
        try
        {
            WaitUntil(() => Native.IsWindowVisible(host.DialogHandle), "Synthetic external owned dialog is visible");
            Check(Native.GetForegroundWindow() == foreground, "Showing synthetic dialog does not request activation");
            Check(Native.GetWindow(host.DialogHandle, 4) == host.Handle
                && (Native.GetWindowLongPtr(host.DialogHandle, ExStyle).ToInt64() & 0x40000) == 0,
                "Dialog is ordinarily owned without WS_EX_APPWINDOW");

            using var native = new NativeAlwaysOnTopWindowSystem();
            Check(native.Read(host.DialogHandle) is { IsEligible: true },
                "Captioned external owned dialog is eligible for pinning");
            Check(native.ResolveForeground(host.DialogHandle).Target?.Identity.Handle == host.DialogHandle,
                "Captured owned-dialog foreground resolves to that dialog");
            Check(native.ResolveForeground(host.DialogChildHandle).Target?.Identity.Handle == host.DialogHandle,
                "Captured native child resolves to its top-level dialog");

            host.VerifyOwned();
            var owner = native.Read(host.Handle) ?? throw new InvalidOperationException("Synthetic owner disappeared");
            Check(native.TryClaim(owner.Identity), "Synthetic dialog owner can be claimed");
            try
            {
                Check(native.TrySetTopmost(owner.Identity, true), "Synthetic dialog owner can be pinned");
                Check(native.ResolveForeground(host.DialogHandle).Target?.Identity.Handle == host.Handle,
                    "Dialog inheriting TOPMOST resolves to the pin actually owned by Llampec");
                Check(native.ResolveForeground(host.DialogChildHandle).Target?.Identity.Handle == host.Handle,
                    "Child of inheriting dialog also resolves to the claimed owner");
            }
            finally
            {
                if (native.HasClaim(owner.Identity))
                {
                    if (!native.TrySetTopmost(owner.Identity, false))
                        throw new InvalidOperationException("Could not restore synthetic owner after dialog resolution test");
                    native.ReleaseClaim(owner.Identity);
                }
            }
            Check(!IsTopmost(host.Handle) && !IsTopmost(host.DialogHandle),
                "Native owner resolution test restores both owner and inherited dialog state");
        }
        finally { Native.ShowWindowAsync(host.DialogHandle, 0 /* SW_HIDE */); }
        WaitUntil(() => !Native.IsWindowVisible(host.DialogHandle), "Synthetic dialog is hidden after regression checks");
    }

    private static void ValidateBorder(nint target, nint border, int thickness)
    {
        long styles = Native.GetWindowLongPtr(border, ExStyle).ToInt64();
        const long required = 0x80000 | 0x20 | 0x08000000 | 0x80 | 0x8;
        Check((styles & required) == required, $"Border {thickness}: layered, transparent, no-activate, tool and topmost styles");
        Check(Native.GetWindow(border, 4) == target, $"Border {thickness}: target owns its overlay");
        Check(Native.SendMessage(border, 0x84, 0, 0) == -1 && Native.SendMessage(border, 0x21, 0, 0) == 3,
            $"Border {thickness}: hit test passes through and mouse activation is refused");
        WaitUntil(() => GeometryMatches(target, border, thickness), $"Border {thickness}: DPI-scaled geometry matches visible frame");
        Native.GetWindowRect(border, out var rectangle);
        int pixels = Pixels(target, thickness);
        nint region = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            Check(Native.GetWindowRgn(border, region) == 3, $"Border {thickness}: native region has complex hollow shape");
            Check(!Native.PtInRegion(region, rectangle.Width / 2, rectangle.Height / 2)
                && Native.PtInRegion(region, pixels - 1, rectangle.Height / 2)
                && !Native.PtInRegion(region, pixels, rectangle.Height / 2),
                $"Border {thickness}: center is empty and strip has expected pixel thickness");
            ValidateBorderPerimeter(target, region, rectangle, pixels, thickness);
        }
        finally { Native.DeleteObject(region); }
    }

    private static void ValidateBorderPerimeter(nint target, nint region, Native.Rectangle border, int pixels, int thickness)
    {
        if (Native.DwmGetWindowAttribute(target, 9, out var frame, Marshal.SizeOf<Native.Rectangle>()) != 0)
            throw new InvalidOperationException("Could not read the synthetic window's visible frame");
        int middleX = (frame.Left + frame.Right) / 2 - border.Left;
        int middleY = (frame.Top + frame.Bottom) / 2 - border.Top;
        int nativeBorder = VisibleFrameBorderPixels(target);
        int innerLeft = frame.Left + nativeBorder - border.Left;
        int innerRight = frame.Right - nativeBorder - border.Left;
        int innerTop = frame.Top + nativeBorder - border.Top;
        int innerBottom = frame.Bottom - nativeBorder - border.Top;

        // The visible DWM frame includes its own neutral border. Check the two
        // pixels straddling that border's content-side perimeter on every edge:
        // the outline reaches it with no gap, and its hollow center starts there.
        // This also handles B=0 and a chosen stroke narrower than DWM's border.
        Check(Native.PtInRegion(region, innerLeft - 1, middleY) && !Native.PtInRegion(region, innerLeft, middleY),
            $"Border {thickness}: left stroke meets the visible frame with no gap");
        Check(Native.PtInRegion(region, innerRight, middleY) && !Native.PtInRegion(region, innerRight - 1, middleY),
            $"Border {thickness}: right stroke meets the visible frame with no gap");
        Check(Native.PtInRegion(region, middleX, innerTop - 1) && !Native.PtInRegion(region, middleX, innerTop),
            $"Border {thickness}: top stroke meets the visible frame with no gap");
        Check(Native.PtInRegion(region, middleX, innerBottom) && !Native.PtInRegion(region, middleX, innerBottom - 1),
            $"Border {thickness}: bottom stroke meets the visible frame with no gap");

        int left = StripPixels(region, 0, border.Height / 2, 1, 0, border.Width);
        int right = StripPixels(region, border.Width - 1, border.Height / 2, -1, 0, border.Width);
        int top = StripPixels(region, border.Width / 2, 0, 0, 1, border.Height);
        int bottom = StripPixels(region, border.Width / 2, border.Height - 1, 0, -1, border.Height);
        Check(left == pixels && right == pixels && top == pixels && bottom == pixels,
            $"Border {thickness}: all four painted strips retain the selected thickness ({pixels} physical pixels)");
    }

    private static int StripPixels(nint region, int x, int y, int dx, int dy, int limit)
    {
        int count = 0;
        while (count < limit && Native.PtInRegion(region, x + count * dx, y + count * dy)) count++;
        return count;
    }

    private static int Pixels(nint target, int thickness)
    {
        nint monitor = Native.MonitorFromWindow(target, 2);
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        return Math.Max(1, (int)Math.Round(thickness * scale));
    }

    private static int VisibleFrameBorderPixels(nint target)
        => Native.DwmGetWindowAttributeValue(target, 37 /* DWMWA_VISIBLE_FRAME_BORDER_THICKNESS */, out uint pixels, sizeof(uint)) == 0
            ? checked((int)pixels) : 0;

    private static bool GeometryMatches(nint target, nint border, int thickness)
    {
        if (Native.DwmGetWindowAttribute(target, 9, out var targetRectangle, Marshal.SizeOf<Native.Rectangle>()) != 0
            || !Native.GetWindowRect(border, out var borderRectangle)) return false;
        int pixels = Pixels(target, thickness);
        int extension = pixels - VisibleFrameBorderPixels(target);
        return borderRectangle.Left == targetRectangle.Left - extension && borderRectangle.Top == targetRectangle.Top - extension
            && borderRectangle.Right == targetRectangle.Right + extension && borderRectangle.Bottom == targetRectangle.Bottom + extension;
    }

    private static bool IsTopmost(nint handle) => (Native.GetWindowLongPtr(handle, ExStyle).ToInt64() & 8) != 0;

    private static nint FindBorder(nint owner)
    {
        nint found = 0;
        Native.EnumWindows((handle, _) =>
        {
            if (Native.GetWindow(handle, 4) != owner) return true;
            Native.GetWindowThreadProcessId(handle, out uint process);
            if (process != Environment.ProcessId) return true;
            var name = new StringBuilder(160);
            Native.GetClassName(handle, name, name.Capacity);
            if (!name.ToString().StartsWith("Llampec.AlwaysOnTop.Border.", StringComparison.Ordinal)) return true;
            found = handle;
            return false;
        }, 0);
        return found;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }

    private static void WaitUntil(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            Pump();
            if (condition()) { Check(true, description); return; }
            Thread.Sleep(10);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(4));
        Check(false, description);
    }

    private static void Pump()
    {
        while (Native.PeekMessage(out var message, 0, 0, 0, 1))
        {
            Native.TranslateMessage(in message);
            Native.DispatchMessage(in message);
        }
    }

    private static int RunHost(int number)
    {
        string className = "Llampec.Smoke.Host." + Environment.ProcessId;
        var windowClass = new Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Native.WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(HostProcedure),
            Instance = Native.GetModuleHandle(null), ClassName = className,
        };
        if (Native.RegisterClassEx(in windowClass) == 0) return 2;
        nint handle = Native.CreateWindowEx(0x40000, className, "Llampec synthetic smoke window " + number,
            0x00CF0000, 80 + number * 40, 100 + number * 40, 400, 240, 0, 0, windowClass.Instance, 0);
        if (handle == 0) return 3;
        _hostMainWindow = handle;
        nint dialog = Native.CreateWindowEx(0, className, "Llampec synthetic owned dialog " + number,
            0x80C80000 /* WS_POPUP | WS_CAPTION | WS_SYSMENU */, 170, 170, 260, 150, handle, 0, windowClass.Instance, 0);
        if (dialog == 0) return 4;
        nint child = Native.CreateWindowEx(0, "STATIC", "Synthetic dialog child",
            0x50000000 /* WS_CHILD | WS_VISIBLE */, 10, 10, 160, 30, dialog, 0, windowClass.Instance, 0);
        if (child == 0) return 5;
        Native.ShowWindow(handle, 4);
        Console.WriteLine($"{handle.ToInt64()} {dialog.ToInt64()} {child.ToInt64()}");
        Console.Out.Flush();
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(in message);
            Native.DispatchMessage(in message);
        }
        Native.UnregisterClass(className, windowClass.Instance);
        GC.KeepAlive(HostProcedure);
        return 0;
    }

    private static nint HostWindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 2 && window == _hostMainWindow) { Native.PostQuitMessage(0); return 0; }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private sealed class Host(Process process, nint handle, nint dialog, nint child) : IDisposable
    {
        public nint Handle { get; } = handle;
        public nint DialogHandle { get; } = dialog;
        public nint DialogChildHandle { get; } = child;
        public int ProcessId { get; } = process.Id;
        public static Host Start(int number)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("--host");
            start.ArgumentList.Add(number.ToString());
            var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start synthetic window host");
            try
            {
                string line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("Synthetic window host exited early: " + process.StandardError.ReadToEnd());
                string[] handles = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (handles.Length != 3) throw new InvalidOperationException("Invalid synthetic window host response");
                return new Host(process, (nint)long.Parse(handles[0]), (nint)long.Parse(handles[1]), (nint)long.Parse(handles[2]));
            }
            catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
        }
        public void VerifyOwned() => VerifyOwned(Handle);
        public void VerifyOwned(nint target)
        {
            Native.GetWindowThreadProcessId(target, out uint actual);
            if (actual != ProcessId || !Native.IsWindow(target)) throw new InvalidOperationException("Synthetic window no longer belongs to test helper");
        }
        public void Close()
        {
            if (process.HasExited) return;
            VerifyOwned();
            Native.PostMessage(Handle, 0x10, 0, 0);
        }
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    Close();
                    if (!process.WaitForExit(2000)) process.Kill();
                }
            }
            finally { process.Dispose(); }
        }
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate nint WndProc(nint window, uint message, nuint wParam, nint lParam);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] public delegate bool EnumProcedure(nint window, nint data);
        [StructLayout(LayoutKind.Sequential)] public struct Rectangle { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct WindowClass
        {
            public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Brush; public string? MenuName; public string ClassName; public nint SmallIcon;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(in WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool UnregisterClass(string className, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] public static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(nint window, int command);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMessage(out Message message, nint window, uint first, uint last);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint flags);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(in Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DispatchMessage(in Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProcedure callback, nint data);
        [DllImport("user32.dll")] public static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint window, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(nint window, out Rectangle rectangle);
        [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor, int mode, out uint x, out uint y);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rectangle rectangle, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetWindowAttributeValue(nint window, uint attribute, out uint value, int size);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] public static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] public static extern int GetWindowRgn(nint window, nint region);
        [DllImport("gdi32.dll")] public static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] public static extern bool PtInRegion(nint region, int x, int y);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint value);
    }
}
