using Llampec.Diagnostics;
using Llampec.Interop;

namespace Llampec.Platform;

/// <summary>
/// A hidden top-level window that receives system broadcasts (display/theme/clock changes,
/// hotkeys, tray icon callbacks) and exposes them as events. The WinUI panel also stays alive while
/// hidden. This message receiver performs no polling.
/// Events are raised on the thread that created this object (the UI thread).
/// </summary>
public sealed class SystemEvents : IDisposable
{
    private const string ClassName = "Llampec.MessageWindow";

    /// <summary>Message id used by the tray icon callback.</summary>
    public const uint WM_TRAYICON = User32.WM_APP + 1;

    private readonly User32.WndProc _wndProc; // kept alive: the OS holds a raw pointer to it
    private readonly uint _taskbarCreatedMessage;
    private bool _disposed;

    public SystemEvents()
    {
        _wndProc = WindowProcedure;
        nint hInstance = Kernel32.GetModuleHandle(null);

        var wc = new User32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };

        try
        {
            if (User32.RegisterClassEx(in wc) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(wc.lpszClassName);
        }

        // NOT a message-only window (HWND_MESSAGE): those never receive messages Windows sends via
        // SendMessageTimeout(HWND_BROADCAST, ...) -- WM_SETTINGCHANGE (theme/colour changes) included --
        // because broadcast delivery only walks the normal top-level window list, which a message-only
        // window is deliberately excluded from. A real top-level window still costs nothing at idle (no
        // timers, no polling); WS_EX_TOOLWINDOW plus never calling ShowWindow keeps it invisible and out
        // of the taskbar/Alt+Tab, same as how FlyoutWindow hides itself.
        Handle = User32.CreateWindowEx((uint)User32.WS_EX_TOOLWINDOW, ClassName, "Llampec", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);
        if (Handle == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        }

        // Explorer broadcasts this after it restarts; the tray icon must be re-added.
        _taskbarCreatedMessage = User32.RegisterWindowMessage("TaskbarCreated");
    }

    public nint Handle { get; }

    /// <summary>A display was added/removed or its mode changed (WM_DISPLAYCHANGE).</summary>
    public event EventHandler? DisplayChanged;
    public event EventHandler? ClockChanged;
    public event EventHandler? Suspending;

    /// <summary>A system setting changed (WM_SETTINGCHANGE). The argument is the section name, e.g. "ImmersiveColorSet".</summary>
    public event EventHandler<string?>? SettingChanged;

    /// <summary>A registered hotkey was pressed. The argument is the hotkey id.</summary>
    public event EventHandler<int>? HotkeyPressed;

    /// <summary>Explorer restarted; tray icons must be re-created.</summary>
    public event EventHandler? TaskbarCreated;

    /// <summary>Tray icon callback (see <see cref="WM_TRAYICON"/>).</summary>
    public event EventHandler<TrayIconEventArgs>? TrayIconEvent;

    public bool RegisterHotkey(int id, uint modifiers, uint virtualKey)
    {
        bool ok = User32.RegisterHotKey(Handle, id, modifiers | User32.MOD_NOREPEAT, virtualKey);
        if (!ok)
        {
            Log.Warn($"RegisterHotKey({id}) failed: {Marshal.GetLastPInvokeError()} (already in use by another app?)");
        }

        return ok;
    }

    public void UnregisterHotkey(int id) => User32.UnregisterHotKey(Handle, id);

    private nint WindowProcedure(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case 0x001E: // WM_TIMECHANGE
                    ClockChanged?.Invoke(this, EventArgs.Empty);
                    return 0;
                case 0x0218: // WM_POWERBROADCAST: resume from sleep/hibernate
                    if (wParam == 0x0004) Suspending?.Invoke(this, EventArgs.Empty);
                    if (wParam is 0x0007 or 0x0012) ClockChanged?.Invoke(this, EventArgs.Empty);
                    return 1;
                case User32.WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke(this, EventArgs.Empty);
                    return 0;

                case User32.WM_SETTINGCHANGE:
                    SettingChanged?.Invoke(this, lParam == 0 ? null : Marshal.PtrToStringUni(lParam));
                    return 0;

                case User32.WM_HOTKEY:
                    HotkeyPressed?.Invoke(this, (int)wParam);
                    return 0;

                case WM_TRAYICON:
                    TrayIconEvent?.Invoke(this, new TrayIconEventArgs(
                        (int)(lParam & 0xFFFF),
                        (short)(wParam & 0xFFFF),
                        (short)((wParam >> 16) & 0xFFFF)));
                    return 0;
            }

            if (msg == _taskbarCreatedMessage)
            {
                TaskbarCreated?.Invoke(this, EventArgs.Empty);
                return 0;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind through the native window procedure.
            Log.Error("Unhandled exception in message window", ex);
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        User32.DestroyWindow(Handle);
        // Allow a later instance (tests, or a future restart-in-place) to register the class again.
        User32.UnregisterClass(ClassName, Kernel32.GetModuleHandle(null));
        GC.KeepAlive(_wndProc);
    }
}

/// <summary>Tray icon callback data. <see cref="Message"/> is a WM_* mouse message or NIN_* value.</summary>
public readonly record struct TrayIconEventArgs(int Message, int X, int Y);
