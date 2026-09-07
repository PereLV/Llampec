using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Llampec.Actions;
using Llampec.Diagnostics;
using Llampec.Flyout;
using Llampec.Platform;
using Llampec.Settings;
using Llampec.Tray;
using Llampec.ViewModels;

namespace Llampec;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Llampec.SingleInstance";
    private const string ShowPanelEventName = @"Local\Llampec.ShowPanel";
    private const int OpenPanelHotkeyId = 1;

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showPanelEvent;
    private RegisteredWaitHandle? _showPanelWait;
    private SystemEvents? _systemEvents;
    private TrayIcon? _trayIcon;
    private FlyoutWindow? _flyout;
    private ThemeManager? _themeManager;

    public static new App Current => (App)Application.Current;

    public AppSettings Settings { get; private set; } = new();

    public bool IsDarkTheme => _themeManager?.IsDark ?? true;

    /// <summary>Raised after the light/dark dictionary or accent colour changed.</summary>
    public event EventHandler? ThemeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!OsVersion.IsSupported)
        {
            MessageBox.Show(
                $"Llampec requires Windows 11 24H2 (build {OsVersion.MinimumBuild}) or later.\nThis PC runs build {OsVersion.Current}.",
                "Llampec", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // Single instance: a second launch just asks the running one to open the panel.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        _showPanelEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowPanelEventName);
        if (!isFirstInstance)
        {
            _showPanelEvent.Set();
            Shutdown(0);
            return;
        }

        Settings = SettingsStore.Load();
#if DEBUG
        Log.SetFile(SettingsStore.LogFilePath); // always log in debug builds
#else
        Log.SetFile(Settings.EnableLogFile ? SettingsStore.LogFilePath : null);
#endif
        Log.Info($"Llampec starting on Windows build {OsVersion.Current}, {RuntimeInformation.ProcessArchitecture}");

        if (Settings.SoftwareRendering)
        {
            // No Direct3D device / GPU driver in the process: the biggest single memory saving in WPF.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
#if DEBUG
            MessageBox.Show(args.Exception.ToString(), "Llampec — unhandled exception", MessageBoxButton.OK, MessageBoxImage.Error);
#endif
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        _systemEvents = new SystemEvents();
        _themeManager = new ThemeManager(this, _systemEvents, Settings);
        _themeManager.ThemeChanged += (_, _) => ThemeChanged?.Invoke(this, EventArgs.Empty);

        var actions = ActionCatalog.Create(_systemEvents);
        var viewModel = new FlyoutViewModel(actions, Settings);
        var flyout = new FlyoutWindow(viewModel, Settings);
        _flyout = flyout;

        _trayIcon = new TrayIcon(_systemEvents, "Llampec");
        _trayIcon.Activated += (_, _) => flyout.Toggle();
        _trayIcon.ContextMenuRequested += (_, pos) => flyout.ShowTrayMenu(pos);

        _systemEvents.HotkeyPressed += (_, id) =>
        {
            if (id == OpenPanelHotkeyId)
            {
                flyout.Toggle();
            }
        };
        RegisterOpenPanelHotkey();

        _showPanelWait = ThreadPool.RegisterWaitForSingleObject(
            _showPanelEvent, (_, _) => Dispatcher.BeginInvoke(flyout.ShowPanel), null, -1, executeOnlyOnce: false);
    }

    public void RegisterOpenPanelHotkey()
    {
        if (_systemEvents is null)
        {
            return;
        }

        _systemEvents.UnregisterHotkey(OpenPanelHotkeyId);
        if (Hotkey.TryParse(Settings.Hotkey, out Hotkey hotkey))
        {
            _systemEvents.RegisterHotkey(OpenPanelHotkeyId, hotkey.Modifiers, hotkey.VirtualKey);
        }
    }

    public void SaveSettings() => SettingsStore.Save(Settings);

    public void ExitApplication() => Shutdown(0);

    protected override void OnExit(ExitEventArgs e)
    {
        _showPanelWait?.Unregister(null);
        _trayIcon?.Dispose();
        _themeManager?.Dispose();
        _systemEvents?.Dispose();
        _showPanelEvent?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
