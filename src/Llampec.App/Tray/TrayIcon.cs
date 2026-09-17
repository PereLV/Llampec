using Windows.Foundation;
using Llampec.Diagnostics;
using Llampec.Interop;
using Llampec.Platform;

namespace Llampec.Tray;

/// <summary>
/// Notification-area icon built directly on Shell_NotifyIcon (no WinForms). Uses the hidden
/// <see cref="SystemEvents"/> window for callbacks and re-adds itself when Explorer restarts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;

    private readonly SystemEvents _events;
    private readonly string _tooltip;
    private nint _hIcon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(SystemEvents events, string tooltip)
    {
        _events = events;
        _tooltip = tooltip;
        _events.TrayIconEvent += OnTrayIconEvent;
        _events.TaskbarCreated += OnTaskbarCreated;
        Add();
        Log.Info($"Tray icon {(_added ? "added" : "NOT added")}");
    }

    /// <summary>Left click, Enter/Space on the icon.</summary>
    public event EventHandler? Activated;

    /// <summary>Right click or Shift+F10. Argument: screen position in physical pixels.</summary>
    public event EventHandler<Point>? ContextMenuRequested;

    private void OnTaskbarCreated(object? sender, EventArgs e) => Add();

    public bool IsPointerOverIcon()
    {
        var identifier = new Shell32.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<Shell32.NOTIFYICONIDENTIFIER>(), hWnd = _events.Handle, uID = IconId,
        };
        return Shell32.Shell_NotifyIconGetRect(in identifier, out var rect) == 0
            && User32.GetCursorPos(out var point)
            && point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    private void Add()
    {
        if (_disposed)
        {
            return;
        }

        if (_hIcon == 0)
        {
            _hIcon = LoadSmallIcon();
        }

        var data = CreateData();
        data.uFlags = Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP;
        data.uCallbackMessage = SystemEvents.WM_TRAYICON;
        data.hIcon = _hIcon;
        data.SetTip(_tooltip);

        if (!Shell32.Shell_NotifyIcon(Shell32.NIM_ADD, ref data))
        {
            Log.Warn($"Shell_NotifyIcon(NIM_ADD) failed (Win32 error {Marshal.GetLastPInvokeError()}, hIcon={_hIcon})");
            return;
        }

        data.uVersion = Shell32.NOTIFYICON_VERSION_4;
        Shell32.Shell_NotifyIcon(Shell32.NIM_SETVERSION, ref data);
        _added = true;
    }

    private Shell32.NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Shell32.NOTIFYICONDATAW>(),
        hWnd = _events.Handle,
        uID = IconId,
    };

    private static nint LoadSmallIcon()
    {
        int cx = User32.GetSystemMetrics(User32.SM_CXSMICON);
        int cy = User32.GetSystemMetrics(User32.SM_CYSMICON);
        nint hInstance = Kernel32.GetModuleHandle(null);
        nint icon = User32.LoadImage(hInstance, User32.IDI_APPLICATION_RESOURCE, User32.IMAGE_ICON, cx, cy, User32.LR_DEFAULTCOLOR | User32.LR_SHARED);
        if (icon == 0)
        {
            icon = User32.LoadIcon(0, User32.IDI_APPLICATION_RESOURCE); // stock application icon
        }

        return icon;
    }

    private void OnTrayIconEvent(object? sender, TrayIconEventArgs e)
    {
        switch (e.Message)
        {
            // With NOTIFYICON_VERSION_4 a left click yields both WM_LBUTTONUP and NIN_SELECT;
            // react to NIN_SELECT only, otherwise the panel toggles twice.
            case Shell32.NIN_SELECT:
            case Shell32.NIN_KEYSELECT:
                Activated?.Invoke(this, EventArgs.Empty);
                break;

            case User32.WM_CONTEXTMENU:
                ContextMenuRequested?.Invoke(this, new Point(e.X, e.Y));
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.TrayIconEvent -= OnTrayIconEvent;
        _events.TaskbarCreated -= OnTaskbarCreated;
        if (_added)
        {
            var data = CreateData();
            Shell32.Shell_NotifyIcon(Shell32.NIM_DELETE, ref data);
        }
        // _hIcon was loaded with LR_SHARED; the system owns it.
    }
}
