using System.Runtime.InteropServices;
using Llampec.Diagnostics;
using Llampec.Interop;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WindowsComposition = Windows.UI.Composition;

namespace Llampec.Flyout;

/// <summary>
/// Transparent composition host. The visible acrylic, outline and shadow belong
/// to the moving XAML surface, rather than the stationary native window.
/// </summary>
internal sealed partial class TransparentPanelBackdrop : SystemBackdrop
{
    private readonly nint _hwnd;
    private readonly SubclassProc _subclass;
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, WindowsComposition.CompositionColorBrush> _brushes = [];
    private WindowsComposition.Compositor? _compositor;
    private Windows.System.DispatcherQueueController? _systemDispatcherController;
    private bool _hooked;
    private const nuint SubclassId = 0x4C4C4250;
    private const uint NativeFrameStyles = 0x00CF0000; // WS_OVERLAPPEDWINDOW
    private const uint ExtendedFrameStyles = 0x00020300; // WINDOWEDGE | CLIENTEDGE | STATICEDGE

    public TransparentPanelBackdrop(nint hwnd)
    {
        _hwnd = hwnd;
        _subclass = OnWindowMessage;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        EnsureSystemDispatcherQueue();
        _compositor ??= new WindowsComposition.Compositor();
        var brush = _compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        _brushes.Add(connectedTarget, brush);
        connectedTarget.SystemBackdrop = brush;

        if (!_hooked)
        {
            _hooked = SetWindowSubclass(_hwnd, Marshal.GetFunctionPointerForDelegate(_subclass), SubclassId, 0);
            if (!_hooked) Log.Warn("Could not attach transparent panel background handler.");
        }
        RemoveNativeFrame();
        ConfigureTransparency();
        nint dc = GetDC(_hwnd);
        if (dc != 0)
        {
            try { ClearBackground(dc); }
            finally { _ = ReleaseDC(_hwnd, dc); }
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        if (_brushes.Remove(disconnectedTarget, out var brush)) brush.Dispose();
        if (_brushes.Count == 0)
        {
            if (_hooked)
                _ = RemoveWindowSubclass(_hwnd, Marshal.GetFunctionPointerForDelegate(_subclass), SubclassId);
            _hooked = false;
            _compositor?.Dispose();
            _compositor = null;
            ShutdownSystemDispatcherQueue();
        }
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private void EnsureSystemDispatcherQueue()
    {
        // Microsoft.UI.Dispatching (WinUI's queue) and Windows.System (the OS
        // compositor's queue) are distinct. WinUI does not initialize the latter.
        // Reuse an existing system queue without taking ownership of it.
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2, // DQTYPE_THREAD_CURRENT: use the WinUI message pump.
            ApartmentType = 0, // DQTAT_COM_NONE: preserve the current STA.
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out nint controller));
        try
        {
            _systemDispatcherController = WinRT.MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(controller);
        }
        finally { _ = Marshal.Release(controller); }
    }

    private async void ShutdownSystemDispatcherQueue()
    {
        var controller = _systemDispatcherController;
        if (controller is null) return;
        _systemDispatcherController = null;
        try
        {
            // Keep the controller alive and let the UI pump drain its system
            // queue after all brushes and the compositor have been released.
            await controller.ShutdownQueueAsync();
        }
        catch (Exception ex) { Log.Error("Could not shut down the panel composition queue", ex); }
    }

    private void ConfigureTransparency()
    {
        // An empty blur region enables alpha composition without adding a second
        // backdrop. This is also the transparent-host setup used by WinUIEx and
        // Microsoft's PowerToys overlays; the HRGN is never assigned to the HWND.
        var margins = new Margins();
        _ = DwmExtendFrameIntoClientArea(_hwnd, in margins);
        nint region = CreateRectRgn(-2, -2, -1, -1);
        if (region == 0) return;
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            _ = DwmEnableBlurBehindWindow(_hwnd, in blur);
        }
        finally { _ = DeleteObject(region); }
    }

    private bool ClearBackground(nint dc)
    {
        if (!GetClientRect(_hwnd, out var rect)) return false;
        // Black is transparent in the extended DWM client surface. The stock
        // brush is owned by Windows and must not be deleted.
        return FillRect(dc, in rect, GetStockObject(4)) != 0;
    }

    private void RemoveNativeFrame()
    {
        // AppWindow can reapply frame bits on the first Show/Activate or a DPI
        // change. WM_STYLECHANGING below keeps this invariant after startup too.
        nint style = User32.GetWindowLongPtr(_hwnd, User32.GWL_STYLE);
        nint exStyle = User32.GetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE);
        _ = User32.SetWindowLongPtr(_hwnd, User32.GWL_STYLE, style & ~(nint)NativeFrameStyles);
        _ = User32.SetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE, exStyle & ~(nint)ExtendedFrameStyles);
        _ = User32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, User32.SWP_FRAMECHANGED
            | User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    private static void FilterNativeFrame(nuint styleIndex, nint styleStruct)
    {
        uint mask = unchecked((int)styleIndex) switch
        {
            User32.GWL_STYLE => NativeFrameStyles,
            User32.GWL_EXSTYLE => ExtendedFrameStyles,
            _ => 0,
        };
        if (mask == 0 || styleStruct == 0) return;
        // STYLESTRUCT contains two DWORDs on both x64 and ARM64; styleNew is
        // the second field. Preserve all non-frame flags (visibility, topmost,
        // RTL, input handling, and WinUI's composition-related flags).
        uint proposed = unchecked((uint)Marshal.ReadInt32(styleStruct, sizeof(uint)));
        Marshal.WriteInt32(styleStruct, sizeof(uint), unchecked((int)(proposed & ~mask)));
    }

    private nint OnWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x007C) // WM_STYLECHANGING
        {
            FilterNativeFrame(wParam, lParam);
            _ = DefSubclassProc(hwnd, message, wParam, lParam);
            // A framework handler can also modify the proposed styles. Apply
            // the invariant last, before USER32 commits any visible frame.
            FilterNativeFrame(wParam, lParam);
            return 0;
        }
        if (message == 0x0014 && ClearBackground((nint)wParam)) return 1; // WM_ERASEBKGND
        if (message == 0x031E) ConfigureTransparency(); // WM_DWMCOMPOSITIONCHANGED
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind { public uint Flags; public int Enable; public nint Region; public int TransitionOnMaximized; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, nint callback, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, nint callback, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, in Margins margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmEnableBlurBehindWindow(nint hwnd, in BlurBehind blur);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint dc, in Rect rect, nint brush);
}

/// <summary>
/// Desktop acrylic that retains its material during light dismissal. Standard
/// controller policy still handles high contrast, disabled transparency and
/// battery saver; only transient focus loss is excluded from the visual policy.
/// </summary>
internal sealed class PanelAcrylicBackdrop(FrameworkElement themeSource) : SystemBackdrop, IDisposable
{
    // A target projection owns a thread-affine ContentExternalBackdropLink.
    // Keep it rooted for the entire XAML connection, including hidden idle time;
    // otherwise C#/WinRT can release its input objects from the GC finalizer thread.
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, BackdropTarget?> _targets = [];
    private bool _suspended = true;
    private bool _disposed;
    private bool _themeSubscribed;

    /// <summary>Recreate acrylic controllers on the XAML thread without replacing their targets.</summary>
    public void Resume()
    {
        if (_disposed) return;
        _suspended = false;
        foreach (var (target, entry) in _targets)
        {
            if (entry is null) continue;
            UpdateConfiguration(entry.Configuration);
            ResumeTarget(target, entry);
        }
    }

    /// <summary>Release effect controllers on the XAML thread, retaining the connected native links.</summary>
    public void Suspend()
    {
        _suspended = true;
        foreach (var (target, entry) in _targets) SuspendTarget(target, entry);
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        // Root the projected target before constructing any more WinRT objects,
        // even if controller creation fails after the base class registered it.
        _targets[connectedTarget] = null;
        try
        {
            // XAML's configuration continues to carry accessibility/system policy.
            var configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
            UpdateConfiguration(configuration);
            var entry = new BackdropTarget(configuration);
            _targets[connectedTarget] = entry;
            if (!_themeSubscribed && !_disposed)
            {
                themeSource.ActualThemeChanged += OnActualThemeChanged;
                _themeSubscribed = true;
            }
            if (!_suspended && !_disposed) ResumeTarget(connectedTarget, entry);
        }
        catch (Exception ex)
        {
            // Keep the registered target alive so XAML can safely disconnect it.
            Log.Error("Could not connect panel acrylic", ex);
        }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (_targets.TryGetValue(target, out var entry) && entry is not null)
            UpdateConfiguration(entry.Configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try { base.OnTargetDisconnected(disconnectedTarget); }
        finally
        {
            if (_targets.Remove(disconnectedTarget, out var entry)) SuspendTarget(disconnectedTarget, entry);
            if (_targets.Count == 0) UnsubscribeTheme();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        foreach (var entry in _targets.Values)
            if (entry is not null) UpdateConfiguration(entry.Configuration);
    }

    private void UpdateConfiguration(SystemBackdropConfiguration configuration)
    {
        configuration.IsInputActive = true;
        configuration.Theme = themeSource.ActualTheme == ElementTheme.Dark
            ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Suspend();
        UnsubscribeTheme();
        // Do not clear _targets or SystemBackdropElement.SystemBackdrop here.
        // WinUI disconnects the rooted native links as the window shuts down.
    }

    private void UnsubscribeTheme()
    {
        if (!_themeSubscribed) return;
        themeSource.ActualThemeChanged -= OnActualThemeChanged;
        _themeSubscribed = false;
    }

    private static void ResumeTarget(ICompositionSupportsSystemBackdrop target, BackdropTarget entry)
    {
        try { entry.Resume(target); }
        catch (Exception ex)
        {
            SuspendTarget(target, entry);
            Log.Error("Could not resume panel acrylic", ex);
        }
    }

    private static void SuspendTarget(ICompositionSupportsSystemBackdrop target, BackdropTarget? entry)
    {
        try { entry?.Suspend(target); }
        catch (Exception ex) { Log.Error("Could not suspend panel acrylic", ex); }
    }

    private sealed class BackdropTarget(SystemBackdropConfiguration configuration)
    {
        public SystemBackdropConfiguration Configuration { get; } = configuration;
        private DesktopAcrylicController? _controller;
        private bool _attached;

        public void Resume(ICompositionSupportsSystemBackdrop target)
        {
            if (_controller is not null) return;
            var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            _controller = controller;
            controller.SetSystemBackdropConfiguration(Configuration);
            _attached = true;
            controller.AddSystemBackdropTarget(target);
        }

        public void Suspend(ICompositionSupportsSystemBackdrop target)
        {
            var controller = _controller;
            bool attached = _attached;
            _controller = null;
            _attached = false;
            if (controller is null) return;
            try
            {
                if (attached) controller.RemoveSystemBackdropTarget(target);
            }
            finally { controller.Dispose(); }
        }
    }
}
