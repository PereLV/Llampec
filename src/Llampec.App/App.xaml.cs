using Llampec.Actions;
using Llampec.Diagnostics;
using Llampec.Flyout;
using Llampec.Platform;
using Llampec.Settings;
using Llampec.Tray;
using Llampec.ViewModels;
using Microsoft.UI.Xaml;

namespace Llampec;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private SystemEvents? _events;
    private TrayIcon? _tray;
    private FlyoutWindow? _window;
    private bool _exiting;
    private Actions.Caffeine.CaffeineAction? _caffeine;
    public static new App Current => (App)Application.Current;
    public AppSettings Settings { get; private set; } = new();
    public AppTheme? PreviewTheme { get; private set; }
    public ThemeScheduler? Scheduler { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Error("Unhandled WinUI exception", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        string? themeArgument = arguments
            .FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase));
        if (themeArgument is not null && Enum.TryParse<AppTheme>(themeArgument[8..], true, out var previewTheme)
            && Enum.IsDefined(previewTheme))
            PreviewTheme = previewTheme;
        _mutex = new Mutex(true, @"Local\Llampec.SingleInstance", out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Llampec.ShowPanel");
        if (!first)
        {
            _showEvent.Set();
            ExitApplication();
            return;
        }

        Settings = SettingsStore.Load();
        UiText.SetLanguage(Settings.Language);
        string logPath = SettingsStore.LogFilePath;
        Log.SetFile(Settings.EnableLogFile || arguments.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase) ? logPath : null);
        try
        {
            _events = new SystemEvents();
            _caffeine = new Actions.Caffeine.CaffeineAction(() => Settings.Caffeine ?? new());
            _events.Suspending += (_, _) => _caffeine.Stop();
            _events.ClockChanged += (_, _) => _caffeine.Refresh();
            var model = new FlyoutViewModel(ActionCatalog.Create(_events, _caffeine), Settings);
            var window = new FlyoutWindow(model);
            _window = window;
            Scheduler = new ThemeScheduler(window.DispatcherQueue, _events);
            Scheduler.StatusChanged += (_, _) => model.Tiles.FirstOrDefault(t => t.Id == "theme")?.Refresh();
            window.DispatcherQueue.TryEnqueue(() => _ = Scheduler.ApplyAsync());
            _tray = new TrayIcon(_events, "Llampec");
            _tray.Activated += (_, _) =>
            {
                Log.Info($"Tray activation: panel visible={window.AppWindow.IsVisible}");
                window.Toggle();
            };
            _tray.ContextMenuRequested += (_, _) => window.ShowTrayMenu();
            _events.HotkeyPressed += (_, id) => { if (id == 1) window.Toggle(); };
            RegisterOpenPanelHotkey();
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => window.DispatcherQueue.TryEnqueue(window.ShowPanel), null, -1, false);
            Log.Info($"Llampec started: {RuntimeInformation.ProcessArchitecture}");
            // A direct launch should visibly open the app. Reserve silent tray startup
            // for an explicit startup/background invocation.
            if (!arguments.Contains("--background", StringComparer.OrdinalIgnoreCase))
                window.DispatcherQueue.TryEnqueue(window.ShowPanel);
            else window.ScheduleIdleRelease();
        }
        catch (Exception ex)
        {
            Log.SetFile(logPath);
            Log.Error("Startup failed", ex);
            ExitApplication();
            throw;
        }
    }

    public void RegisterOpenPanelHotkey()
    {
        if (_events is null) return;
        _events.UnregisterHotkey(1);
        if (Hotkey.TryParse(Settings.Hotkey, out var hotkey))
            _events.RegisterHotkey(1, hotkey.Modifiers, hotkey.VirtualKey);
    }

    public void RefreshTheme()
    {
        if (_window?.AppWindow.IsVisible == true) _window.ApplyTheme();
        else _window?.ScheduleIdleRelease();
    }
    public bool IsPointerPressOnTrayIcon() => _tray?.IsPointerOverIcon() == true
        && (Llampec.Interop.User32.GetAsyncKeyState(0x01) < 0 || Llampec.Interop.User32.GetAsyncKeyState(0x02) < 0);
    public void SaveSettings() => SettingsStore.Save(Settings);

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _showWait?.Unregister(null);
        Scheduler?.Dispose();
        _caffeine?.Dispose();
        _tray?.Dispose();
        _events?.Dispose();
        _showEvent?.Dispose();
        _mutex?.Dispose();
        _window?.Dispose();
        Exit();
    }
}
