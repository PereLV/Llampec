using System.ComponentModel;
using System.Runtime.InteropServices;
using Llampec.Actions.Fullscreen;
using Xunit;

namespace Llampec.Tests;

/// <summary>Opt-in native checks resize only windows created by this test; no injected input.</summary>
public sealed class FullscreenNativeTests
{
    private sealed class NativeFactAttribute : FactAttribute
    {
        public NativeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("LLAMPEC_FULLSCREEN_NATIVE_SMOKE") != "1")
                Skip = "Set LLAMPEC_FULLSCREEN_NATIVE_SMOKE=1 to test synthetic native windows on the current desktop.";
        }
    }

    [NativeFact]
    public void SyntheticWindowsCoverEveryMonitorAndRestoreStylesPlacementRegionAndClientInsets()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunSmoke(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Native smoke thread timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunSmoke()
    {
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        if (previousDpi == 0) throw new Win32Exception();
        string className = "LlampecFullscreenSmoke." + Guid.NewGuid().ToString("N");
        WindowProc callback = TestWindowProc;
        nint module = GetModuleHandle(null);
        var registration = new WindowClass { WindowProc = Marshal.GetFunctionPointerForDelegate(callback), Instance = module, ClassName = className };
        if (RegisterClass(ref registration) == 0) throw new Win32Exception();
        try
        {
            var monitors = new List<Rect>();
            Assert.True(EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect rect, nint param) => { monitors.Add(rect); return true; }, 0));
            Assert.NotEmpty(monitors);
            using var windows = new NativeFullscreenWindowSystem(allowOwnWindowsForTesting: true);
            foreach (Rect monitor in monitors)
            {
                foreach (var mode in new[] { (Maximized: false, Style: 0x10CF0000u), (Maximized: true, Style: 0x10CF0000u), (Maximized: false, Style: 0x90000000u) })
                {
                    nint window = CreateWindowEx(0x00000300, className, "Llampec synthetic Chromium inset smoke",
                        mode.Style, monitor.Left + 80, monitor.Top + 100, Math.Min(520, monitor.Width - 100), Math.Min(350, monitor.Height - 120),
                        0, 0, module, 0);
                    if (window == 0) throw new Win32Exception();
                    try
                    {
                        if (mode.Maximized) ShowWindow(window, 3);
                        else ShowWindow(window, 4);
                        uint style = ReadStyle(window, -16), exstyle = ReadStyle(window, -20);
                        var placement = ReadPlacement(window);
                        Assert.True(GetWindowRect(window, out Rect original));
                        bool savedRegion = (mode.Style & 0x00CF0000u) != 0;
                        if (savedRegion)
                        {
                            nint oldRegion = CreateRectRgn(0, 0, original.Width, original.Height);
                            Assert.NotEqual(0, oldRegion);
                            Assert.NotEqual(0, SetWindowRgn(window, oldRegion, false)); // Windows now owns it.
                        }
                        var snapshot = windows.Read(window)!;
                        Assert.True(snapshot.IsEligible);
                        var session = windows.Enter(snapshot.Identity);
                        try
                        {
                            Assert.True(session.OwnsWindow);
                            Assert.True(session.DisplayUnchanged);
                            Assert.Equal(style & ~(0x00CF0000u | 0x01000000u), ReadStyle(window, -16));
                            Assert.Equal(exstyle & ~0x00020301u, ReadStyle(window, -20));
                            AssertClientCovers(window, monitor);
                            Assert.True(GetWindowRect(window, out Rect cover));
                            Assert.True(cover.Left <= monitor.Left - 7, "Synthetic client left inset was not compensated.");
                            Assert.True(cover.Right >= monitor.Right + 7, "Synthetic client right inset was not compensated.");
                            AssertClipMatchesMonitor(window, cover, monitor);
                            session.RevealTaskbar();
                            Assert.True(GetWindowRect(window, out Rect revealed));
                            Assert.Equal(monitor.Bottom - 1, revealed.Bottom);
                            session.CoverTaskbar();
                            AssertClientCovers(window, monitor);
                        }
                        finally { session.Restore(); }
                        Assert.False(session.OwnsWindow);
                        Assert.Equal(style, ReadStyle(window, -16));
                        Assert.Equal(exstyle, ReadStyle(window, -20));
                        var restored = ReadPlacement(window);
                        Assert.Equal(placement.ShowCmd, restored.ShowCmd);
                        Assert.Equal(placement.NormalPosition, restored.NormalPosition);
                        Assert.True(GetWindowRect(window, out Rect restoredBounds));
                        Assert.Equal(original, restoredBounds);
                        nint region = CreateRectRgn(0, 0, 0, 0);
                        try
                        {
                            if (savedRegion)
                            {
                                Assert.NotEqual(0, GetWindowRgn(window, region));
                                Assert.NotEqual(0, GetRgnBox(region, out Rect box));
                                Assert.Equal(new Rect { Right = original.Width, Bottom = original.Height }, box);
                            }
                            else Assert.Equal(0, GetWindowRgn(window, region));
                        }
                        finally { DeleteObject(region); }
                    }
                    finally { DestroyWindow(window); }
                }
            }
        }
        finally
        {
            UnregisterClass(className, module);
            SetThreadDpiAwarenessContext(previousDpi);
            GC.KeepAlive(callback);
        }
    }

    private static nint TestWindowProc(nint window, uint message, nuint wparam, nint lparam)
    {
        nint result = DefWindowProc(window, message, wparam, lparam);
        if (message == 0x0083 && lparam != 0) // WM_NCCALCSIZE: emulate Chromium's persistent client resize margins.
        {
            var client = Marshal.PtrToStructure<Rect>(lparam);
            client.Left += 7; client.Right -= 7; client.Bottom -= 7;
            Marshal.StructureToPtr(client, lparam, false);
        }
        return result;
    }

    private static void AssertClientCovers(nint window, Rect monitor)
    {
        Assert.True(GetClientRect(window, out Rect client));
        MapWindowPoints(window, 0, ref client, 2);
        Assert.True(client.Left <= monitor.Left && client.Top <= monitor.Top && client.Right >= monitor.Right && client.Bottom >= monitor.Bottom,
            $"Client {client} does not cover monitor {monitor}.");
    }

    private static void AssertClipMatchesMonitor(nint window, Rect raw, Rect monitor)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Assert.NotEqual(0, GetWindowRgn(window, region));
            Assert.NotEqual(0, GetRgnBox(region, out Rect box));
            Assert.Equal(new Rect { Left = monitor.Left - raw.Left, Top = monitor.Top - raw.Top,
                Right = monitor.Right - raw.Left, Bottom = monitor.Bottom - raw.Top }, box);
        }
        finally { DeleteObject(region); }
    }

    private static uint ReadStyle(nint window, int index) => unchecked((uint)GetWindowLongPtr(window, index).ToInt64());
    private static Placement ReadPlacement(nint window)
    {
        var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        Assert.True(GetWindowPlacement(window, ref placement));
        return placement;
    }
    private delegate nint WindowProc(nint window, uint message, nuint wparam, nint lparam);
    private delegate bool MonitorProc(nint monitor, nint dc, ref Rect rect, nint param);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public uint Length, Flags, ShowCmd;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style;
        public nint WindowProc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass value);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exstyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint module, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint param);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(nint from, nint to, ref Rect rect, uint count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Rect box);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
