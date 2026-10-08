using Llampec.Actions;
using Llampec.Actions.AlwaysOnTop;
using Llampec.Actions.Fullscreen;
using Llampec.Actions.Rotation;
using Llampec.Diagnostics;
using Llampec.Platform;
using Llampec.Settings;
using Llampec.ViewModels;
using Microsoft.UI.Dispatching;

namespace Llampec;

public partial class App
{
    private readonly Dictionary<string, IQuickAction> _actions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _moduleSettingsGate = new(1, 1);
    private FlyoutViewModel? _model;
    private bool _modulesChanging;
    public string? ModuleChangeError { get; private set; }

    private IReadOnlyList<IQuickAction> CurrentActions() => ModuleCatalog.All
        .Where(module => _actions.ContainsKey(module.Id)).Select(module => _actions[module.Id]).ToArray();

    private void EnableModule(string id)
    {
        if (_actions.ContainsKey(id)) return;
        IQuickAction action;
        switch (id)
        {
            case "always-on-top":
                AlwaysOnTop = new AlwaysOnTopService(() => Settings.AlwaysOnTop);
                AlwaysOnTop.SelectionRequested += OnPinSelectionRequested;
                AlwaysOnTop.Changed += OnPinsChanged;
                action = _alwaysOnTopAction = new AlwaysOnTopAction(AlwaysOnTop);
                _alwaysOnTopHotkey = CreateModuleHotkey(2, 3, Settings.AlwaysOnTop.Hotkey);
                break;
            case "fullscreen":
                Fullscreen = new FullscreenService(device =>
                {
                    var display = Rotation?.Snapshot.Displays.FirstOrDefault(d =>
                        string.Equals(d.DeviceName, device, StringComparison.OrdinalIgnoreCase));
                    string identifier = device.StartsWith(@"\\.\", StringComparison.Ordinal) ? device[4..] : device;
                    return display is null ? identifier : $"{display.Name} ({identifier})";
                });
                Fullscreen.Changed += OnFullscreenStateChanged;
                action = _fullscreenAction = new FullscreenAction(Fullscreen);
                _fullscreenHotkey = CreateModuleHotkey(4, 5, Settings.Fullscreen.Hotkey);
                ConfigureFullscreenTimer();
                break;
            case "rotation":
                Rotation = new DisplayOrientationService();
                action = _rotationAction = new RotationAction(Rotation);
                break;
            case "caffeine":
                action = _caffeine = new Actions.Caffeine.CaffeineAction(() => Settings.Caffeine);
                break;
            case "theme":
                action = new Actions.Theme.ThemeAction();
                Scheduler = new ThemeScheduler(DispatcherQueue.GetForCurrentThread(), _events!);
                Scheduler.StatusChanged += OnThemeStatusChanged;
                break;
            case "hdr": action = new Actions.Hdr.HdrAction(); break;
            case "display-off": action = new Actions.DisplayOff.DisplayOffAction(_events!); break;
            case "projection": action = new Actions.Projection.ProjectionAction(); break;
            case "taskbar-autohide": action = new Actions.Taskbar.TaskbarAutoHideAction(); break;
            case "touch-taskbar":
                action = new Actions.Taskbar.TouchTaskbarAction();
                action.Changed += OnTouchTaskbarChanged;
                break;
            case "screenshot": action = new Actions.Screenshot.ScreenshotAction(); break;
            case "power": action = new Actions.Power.PowerAction(); break;
            case "mouse":
                LogitechMouse = new Devices.Logitech.LogitechMouseService(SendLogitechShortcut);
                action = _mouseAction = new Actions.Mouse.MouseAction(LogitechMouse, () => Settings.Logitech, SetLogitechEnabledAsync);
                _ = StartLogitechMouseAsync();
                break;
            case "camera":
                action = _cameraAction = new Actions.Camera.CameraAction();
                if (_window?.AppWindow.IsVisible == true) _cameraAction.SetObserving(true);
                break;
            default: throw new ArgumentException("Unknown module.", nameof(id));
        }
        _actions.Add(id, action);
    }

    private ConfigurableHotkey CreateModuleHotkey(int first, int second, string text)
    {
        var hotkey = new ConfigurableHotkey(first, second,
            (id, parsed) => _events!.RegisterHotkey(id, parsed.Modifiers, parsed.VirtualKey),
            id => _events!.UnregisterHotkey(id));
        hotkey.Initialize(text);
        return hotkey;
    }

    private void OnPinSelectionRequested(object? sender, EventArgs args) => _window?.OpenAlwaysOnTop();
    private void OnPinsChanged(object? sender, EventArgs args) => _window?.OnAlwaysOnTopChanged();
    private void OnThemeStatusChanged(object? sender, EventArgs args) =>
        _model?.Tiles.FirstOrDefault(tile => tile.Id == "theme")?.Refresh();
    private void OnTouchTaskbarChanged(object? sender, EventArgs args) => _window?.DispatcherQueue.TryEnqueue(() =>
    {
        if (!_exiting && !_modulesChanging)
            _model?.Tiles.FirstOrDefault(tile => tile.Id == "taskbar-autohide")?.Refresh();
    });

    private void ConfigureFullscreenTimer()
    {
        if (_window is null || Fullscreen is null || _fullscreenTimer is not null) return;
        _fullscreenTimer = _window.DispatcherQueue.CreateTimer();
        _fullscreenTimer.Interval = TimeSpan.FromMilliseconds(30);
        _fullscreenTimer.Tick += OnFullscreenTick;
    }
    private void OnFullscreenTick(DispatcherQueueTimer sender, object args) => Fullscreen?.Tick();
    private void OnFullscreenStateChanged(object? sender, EventArgs args)
    {
        if (_exiting) return;
        if (Fullscreen?.NeedsPolling == true) _fullscreenTimer?.Start();
        else _fullscreenTimer?.Stop();
        _window?.OnFullscreenChanged();
    }

    private async Task<bool> PrepareModuleRemovalAsync(string id)
    {
        return id switch
        {
            "always-on-top" => AlwaysOnTop?.TryDeactivate() != false,
            "fullscreen" => Fullscreen?.TryDeactivate() != false,
            "rotation" => Rotation is null || await Rotation.TryDeactivateAsync(),
            "mouse" => await ReleaseLogitechMouseAsync(),
            _ => true
        };
    }

    /// <summary>
    /// Restores the mouse's original settings before its module is removed. A failed
    /// restoration reapplies the saved preferences and keeps the module enabled.
    /// </summary>
    private async Task<bool> ReleaseLogitechMouseAsync()
    {
        if (LogitechMouse is not { } mouse) return true;
        var released = Settings.Logitech.Clone();
        released.Enabled = false;
        try { await mouse.ApplyAsync(released); }
        catch (Exception error) { Log.Warn($"Logitech mouse could not be released: {error.Message}"); }
        if (!mouse.Status.RecoveryPending) return true;
        await RestoreLogitechMouseAsync();
        return false;
    }

    private async Task RestoreLogitechMouseAsync()
    {
        try { if (!_exiting && LogitechMouse is { } mouse) await mouse.ApplyAsync(Settings.Logitech); }
        catch (Exception error) { Log.Warn($"Logitech mouse settings could not be reapplied: {error.Message}"); }
    }

    private async Task RemoveModuleAsync(string id)
    {
        _actions.Remove(id, out var action);
        switch (id)
        {
            case "always-on-top":
                _alwaysOnTopHotkey?.Dispose(); _alwaysOnTopHotkey = null;
                if (AlwaysOnTop is { } pins)
                {
                    pins.SelectionRequested -= OnPinSelectionRequested;
                    pins.Changed -= OnPinsChanged;
                    _alwaysOnTopAction?.Dispose(); _alwaysOnTopAction = null;
                    pins.Dispose(); AlwaysOnTop = null;
                }
                break;
            case "fullscreen":
                _fullscreenTimer?.Stop();
                if (_fullscreenTimer is not null) _fullscreenTimer.Tick -= OnFullscreenTick;
                _fullscreenTimer = null;
                _fullscreenHotkey?.Dispose(); _fullscreenHotkey = null;
                if (Fullscreen is { } fullscreen)
                {
                    fullscreen.Changed -= OnFullscreenStateChanged;
                    _fullscreenAction?.Dispose(); _fullscreenAction = null;
                    fullscreen.Dispose(); Fullscreen = null;
                }
                break;
            case "rotation":
                _rotationAction?.Dispose(); _rotationAction = null;
                if (Rotation is { } rotation) await rotation.DisposeAsync();
                Rotation = null;
                break;
            case "theme":
                if (Scheduler is { } scheduler)
                {
                    scheduler.StatusChanged -= OnThemeStatusChanged;
                    await scheduler.DeactivateAsync(); Scheduler = null;
                }
                break;
            case "caffeine": _caffeine?.Dispose(); _caffeine = null; break;
            case "mouse":
                _mouseAction?.Dispose(); _mouseAction = null;
                if (LogitechMouse is { } mouse)
                {
                    LogitechMouse = null;
                    try { await mouse.DisposeAsync(); }
                    catch (Exception error) { Log.Error("Logitech service could not stop cleanly; recovery is retained.", error); }
                }
                break;
            case "camera": _cameraAction?.Dispose(); _cameraAction = null; break;
            case "touch-taskbar":
                if (action is not null) action.Changed -= OnTouchTaskbarChanged;
                break;
        }
    }

    /// <summary>Apply one editor draft. Failed restoration retains live ownership and the draft.</summary>
    public async Task<bool> TryApplyPanelLayoutAsync(PanelLayoutDraft draft)
    {
        await _moduleSettingsGate.WaitAsync();
        var added = new List<string>();
        bool committed = false;
        bool startThemeSchedule = false;
        bool retryPinShortcut = false;
        bool retryFullscreenShortcut = false;
        bool mouseReleased = false;
        try
        {
            ModuleChangeError = null;
            if (_exiting) return false;
            if (_model?.Tiles.Any(tile => tile.IsBusy) == true || _actions.Values.Any(IsActionBusy))
            { ModuleChangeError = "Wait for the current operation to finish."; return false; }
            if (!draft.TryValidate(out var error)) { ModuleChangeError = error; return false; }
            _modulesChanging = true;
            var removed = _actions.Keys.Where(draft.DisabledModules.Contains).ToArray();
            foreach (string id in removed)
            {
                if (!await PrepareModuleRemovalAsync(id))
                {
                    ModuleChangeError = "Could not restore an active tool. Its module remains enabled; try again.";
                    return false;
                }
                mouseReleased |= id == "mouse";
                if (_exiting) return false;
            }
            var previous = new PanelLayoutDraft(Settings, ModuleCatalog.All.Select(module => module.Id));
            try
            {
                foreach (var module in ModuleCatalog.All)
                    if (!draft.DisabledModules.Contains(module.Id) && !_actions.ContainsKey(module.Id))
                    { added.Add(module.Id); EnableModule(module.Id); }
                draft.ApplyTo(Settings);
                if (!SettingsStore.Save(Settings))
                {
                    previous.ApplyTo(Settings);
                    ModuleChangeError = "Could not save settings. Check access to the Llampec settings folder.";
                    return false;
                }
                committed = true;
                startThemeSchedule = added.Contains("theme");
                retryPinShortcut = added.Contains("always-on-top");
                retryFullscreenShortcut = added.Contains("fullscreen");
                added.Clear();
            }
            catch
            {
                previous.ApplyTo(Settings);
                throw;
            }
            _model?.ReplaceActions(CurrentActions(), Settings);
            foreach (string id in removed) await RemoveModuleAsync(id);
            // A newly enabled module can reuse a shortcut held by one just
            // removed. Retry only failed registrations after that reservation ends.
            if (retryPinShortcut && _alwaysOnTopHotkey?.RegisteredId is null)
                _alwaysOnTopHotkey?.Initialize(Settings.AlwaysOnTop.Hotkey);
            if (retryFullscreenShortcut && _fullscreenHotkey?.RegisteredId is null)
                _fullscreenHotkey?.Initialize(Settings.Fullscreen.Hotkey);
            LogModuleRuntime();
            if (!_exiting && Scheduler is not null)
            {
                Scheduler.SetStatusVisible(_window?.AppWindow.IsVisible == true);
                if (startThemeSchedule) await Scheduler.ApplyAsync();
            }
            try { _model?.RefreshAll(); }
            catch (Exception refreshError) { Log.Warn($"Panel state refresh failed after saving: {refreshError.Message}"); }
            return true;
        }
        catch (Exception error)
        {
            Log.Error("Could not apply panel modules.", error);
            ModuleChangeError = "Could not apply the module changes. Try again.";
            return committed;
        }
        finally
        {
            if (mouseReleased && !committed) await RestoreLogitechMouseAsync();
            try
            {
                foreach (string id in added)
                {
                    try { await RemoveModuleAsync(id); }
                    catch (Exception error) { Log.Error("Could not release an uncommitted module.", error); }
                }
            }
            finally
            {
                _modulesChanging = false;
                _moduleSettingsGate.Release();
            }
        }
    }

    private static bool IsActionBusy(IQuickAction action) => action.IsBusy || action.SubActions.Any(IsActionBusy);

    [System.Diagnostics.Conditional("DEBUG")]
    private void LogModuleRuntime() => Log.Info($"Module runtime: active=[{string.Join(',', _actions.Keys)}]; "
        + $"pins={AlwaysOnTop is not null}; fullscreen={Fullscreen is not null}; rotation={Rotation is not null}; "
        + $"caffeine={_caffeine is not null}; mouse={LogitechMouse is not null}; camera={_cameraAction is not null}; scheduler={Scheduler is not null}; fullscreenTimer={_fullscreenTimer is not null}; "
        + $"pinShortcut={_alwaysOnTopHotkey?.RegisteredId ?? 0}; fullscreenShortcut={_fullscreenHotkey?.RegisteredId ?? 0}.");
}
