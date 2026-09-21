using Llampec.Actions;
using Llampec.Actions.AlwaysOnTop;
using Llampec.Diagnostics;
using Llampec.Devices.Logitech;
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
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _showWait;
    private RegisteredWaitHandle? _exitWait;
    private SystemEvents? _events;
    private TrayIcon? _tray;
    private FlyoutWindow? _window;
    private bool _exiting;
    private Actions.Caffeine.CaffeineAction? _caffeine;
    private AlwaysOnTopAction? _alwaysOnTopAction;
    private ConfigurableHotkey? _alwaysOnTopHotkey;
    private readonly SemaphoreSlim _logitechSettingsGate = new(1, 1);
    public static new App Current => (App)Application.Current;
    public AppSettings Settings { get; private set; } = new();
    public AppTheme? PreviewTheme { get; private set; }
    public ThemeScheduler? Scheduler { get; private set; }
    public AlwaysOnTopService? AlwaysOnTop { get; private set; }
    public LogitechMouseService? LogitechMouse { get; private set; }
    public string? AlwaysOnTopHotkeyError => _alwaysOnTopHotkey?.Error;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Error("Unhandled WinUI exception", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (!AdministratorRestart.WaitForPreviousInstance(arguments)) { ExitApplication(); return; }
        string? themeArgument = arguments
            .FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase));
        if (themeArgument is not null && Enum.TryParse<AppTheme>(themeArgument[8..], true, out var previewTheme)
            && Enum.IsDefined(previewTheme))
            PreviewTheme = previewTheme;
        bool exitRequested = arguments.Contains("--exit", StringComparer.OrdinalIgnoreCase);
        bool background = arguments.Contains("--background", StringComparer.OrdinalIgnoreCase);
        try
        {
            _mutex = new Mutex(true, @"Local\Llampec.SingleInstance", out bool first);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Llampec.ShowPanel");
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Llampec.Exit");
            if (!first)
            {
                if (exitRequested) _exitEvent.Set(); else if (!background) _showEvent.Set();
                ExitApplication();
                return;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // A running elevated instance can own synchronization objects that this
            // token cannot open. Leave it running instead of crashing the new launch.
            Log.SetFile(SettingsStore.LogFilePath);
            Log.Error("Cannot signal the running Llampec instance with these permissions.", ex);
            ExitApplication();
            return;
        }
        if (exitRequested) { ExitApplication(); return; }

        Settings = SettingsStore.Load();
        UiText.SetLanguage(Settings.Language);
        string logPath = SettingsStore.LogFilePath;
        Log.SetFile(Settings.EnableLogFile || arguments.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase) ? logPath : null);
        try
        {
            _events = new SystemEvents();
            Settings.AlwaysOnTop ??= new();
            AlwaysOnTop = new AlwaysOnTopService(() => Settings.AlwaysOnTop);
            _alwaysOnTopAction = new AlwaysOnTopAction(AlwaysOnTop);
            _caffeine = new Actions.Caffeine.CaffeineAction(() => Settings.Caffeine ?? new());
            _events.Suspending += (_, _) => _caffeine.Stop();
            _events.ClockChanged += (_, _) => _caffeine.Refresh();
            var model = new FlyoutViewModel(ActionCatalog.Create(_events, _caffeine, _alwaysOnTopAction), Settings);
            var window = new FlyoutWindow(model);
            _window = window;
            LogitechMouse = new LogitechMouseService(shortcut =>
            {
                if (new KeyboardShortcut(shortcut).Send())
                    Log.Info($"Logitech shortcut executed: {shortcut}");
            });
            _events.Suspending += (_, _) => LogitechMouse.Suspend();
            _events.Resumed += (_, _) => LogitechMouse.Resume();
            _events.SessionEnding += (_, _) => ExitApplication();
            _events.HidDeviceChanged += (_, path) =>
            {
                if (LogitechMouse.Status.RecoveryPending || (path is null && !LogitechMouse.Status.Connected)
                    || string.Equals(path, Settings.Logitech.DevicePath, StringComparison.OrdinalIgnoreCase))
                    LogitechMouse.NotifyDeviceChange();
            };
            _ = StartLogitechMouseAsync();
            AlwaysOnTop.SelectionRequested += (_, _) => window.OpenAlwaysOnTop();
            AlwaysOnTop.Changed += (_, _) => window.OnAlwaysOnTopChanged();
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
            _events.HotkeyPressed += (_, id) =>
            {
                if (id == 1) window.Toggle();
                else if (id == _alwaysOnTopHotkey?.RegisteredId)
                {
                    AlwaysOnTop.ToggleForeground();
                    if (AlwaysOnTop.Error is not null) window.OpenAlwaysOnTop();
                }
            };
            RegisterOpenPanelHotkey();
            _alwaysOnTopHotkey = new ConfigurableHotkey(2, 3,
                (id, hotkey) => _events.RegisterHotkey(id, hotkey.Modifiers, hotkey.VirtualKey),
                _events.UnregisterHotkey);
            _alwaysOnTopHotkey.Initialize(Settings.AlwaysOnTop.Hotkey);
            _events.SettingChanged += (_, _) => AlwaysOnTop.ApplyAppearance();
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => window.DispatcherQueue.TryEnqueue(window.ShowPanel), null, -1, false);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
                (_, _) => window.DispatcherQueue.TryEnqueue(ExitApplication), null, -1, true);
            Log.Info($"Llampec started: {RuntimeInformation.ProcessArchitecture}");
            // A direct launch should visibly open the app. Reserve silent tray startup
            // for an explicit startup/background invocation.
            if (!background)
            {
                RefreshStartupRegistration();
                window.DispatcherQueue.TryEnqueue(window.ShowPanel);
            }
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

    private static void RefreshStartupRegistration()
    {
        try
        {
            if (Environment.ProcessPath is { } path
                && new StartupRegistration(path).RefreshExistingRegistration())
                Log.Info("Updated Windows startup registration to the current application location.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            // Failure to repair an old registration must not prevent a manual launch.
            Log.Error("Could not update Windows startup registration to the current application location.", ex);
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

    private async Task StartLogitechMouseAsync()
    {
        try { if (LogitechMouse is not null) await LogitechMouse.ApplyAsync(Settings.Logitech); }
        catch (ArgumentException ex)
        {
            Log.Error("Logitech settings could not be activated; the rest of Llampec remains available.", ex);
            // Invalid preferences must not prevent recovery of a previous session.
            try { if (!_exiting && LogitechMouse is not null) await LogitechMouse.ApplyAsync(new()); }
            catch (Exception recovery) { Log.Error("Logitech recovery remains pending.", recovery); }
        }
        catch (OperationCanceledException) when (_exiting) { }
        catch (Exception ex) { Log.Error("Logitech settings could not be activated; saved preferences are retained.", ex); }
    }

    public async Task<bool> TryApplyLogitechSettingsAsync(LogitechMouseSettings preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var next = preferences.Clone();
        next.Validate();
        await _logitechSettingsGate.WaitAsync();
        try
        {
            if (_exiting || LogitechMouse is null) return false;
            var previous = Settings.Logitech;
            Settings.Logitech = next;
            if (!SettingsStore.Save(Settings)) { Settings.Logitech = previous; return false; }
            // Desired settings remain saved if the selected mouse is offline. The
            // service reports status and reapplies them when that same mouse returns.
            await LogitechMouse.ApplyAsync(next);
            return true;
        }
        finally { _logitechSettingsGate.Release(); }
    }

    public bool TrySetAlwaysOnTopHotkey(string text, out string? error)
    {
        if (_alwaysOnTopHotkey is null)
        {
            error = "Shortcut is already in use.";
            return false;
        }
        bool success = _alwaysOnTopHotkey.TrySet(text, normalized =>
        {
            var preferences = Settings.AlwaysOnTop;
            string previous = preferences.Hotkey;
            preferences.Hotkey = normalized;
            if (SettingsStore.Save(Settings)) return true;
            preferences.Hotkey = previous;
            return false;
        });
        error = _alwaysOnTopHotkey.Error;
        return success;
    }

    public bool TryRestartAsAdministrator(out string? error)
    {
        if (!AdministratorRestart.TryStart(out error)) return false;
        ExitApplication();
        return true;
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _showWait?.Unregister(null);
        _exitWait?.Unregister(null);
        try { LogitechMouse?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Error("Logitech shutdown could not complete restoration; recovery is retained for the next launch.", ex); }
        Scheduler?.Dispose();
        _alwaysOnTopHotkey?.Dispose();
        _alwaysOnTopAction?.Dispose();
        AlwaysOnTop?.Dispose();
        Log.Info("Llampec exiting: session pins and global shortcuts released.");
        _caffeine?.Dispose();
        _tray?.Dispose();
        _events?.Dispose();
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        _mutex?.Dispose();
        _window?.Dispose();
        Exit();
    }
}
