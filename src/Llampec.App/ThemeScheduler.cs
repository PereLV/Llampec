using Llampec.Diagnostics;
using Llampec.Platform;
using Llampec.Settings;
using Microsoft.UI.Dispatching;

namespace Llampec;

/// <summary>One schedule-deadline timer plus a countdown timer that runs only while visible.</summary>
public sealed class ThemeScheduler : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _statusTimer;
    private readonly SystemEvents _events;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;
    private bool _statusVisible;
    public string? Error { get; private set; }
    public ThemeSchedulePlan? Plan { get; private set; }
    public event EventHandler? StatusChanged;

    public ThemeScheduler(DispatcherQueue dispatcher, SystemEvents events)
    {
        _dispatcher = dispatcher;
        _events = events;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += OnTick;
        _statusTimer = dispatcher.CreateTimer();
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += OnStatusTick;
        events.ClockChanged += OnClockChanged;
        events.SettingChanged += OnSettingChanged;
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => _ = ApplyAsync();
    private void OnStatusTick(DispatcherQueueTimer sender, object args) => UpdateStatus();

    /// <summary>The countdown only wakes while the panel is visible, never in the tray.</summary>
    public void SetStatusVisible(bool visible)
    {
        if (_disposed) return;
        _statusVisible = visible;
        _statusTimer.Stop();
        if (visible) UpdateStatus();
    }

    private void UpdateStatus()
    {
        _statusTimer.Stop();
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (!_disposed && _statusVisible && ThemeScheduleStatus.NextUpdate(Plan, DateTimeOffset.Now) is { } interval)
        {
            _statusTimer.Interval = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;
            _statusTimer.Start();
        }
    }
    private void OnClockChanged(object? sender, EventArgs args)
    {
        TimeZoneInfo.ClearCachedData();
        _dispatcher.TryEnqueue(() => _ = ApplyAsync());
    }
    private void OnSettingChanged(object? sender, string? section)
    {
        if (section == "TimeZoneInformation") OnClockChanged(sender, EventArgs.Empty);
        else if (section == "ImmersiveColorSet")
            // Reflect manual/external changes without reapplying the schedule or
            // updating XAML reentrantly from the synchronous Windows broadcast.
            _dispatcher.TryEnqueue(() => { if (!_disposed) UpdateStatus(); });
    }

    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _timer.Stop();
            _statusTimer.Stop();
            Error = null;
            Plan = null;
            var settings = App.Current.Settings;
            var plan = ThemeSchedule.Calculate(settings.ThemeSchedule ?? new(), DateTimeOffset.Now,
                TimeZoneInfo.Local, settings.LocationLatitude, settings.LocationLongitude);
            if (plan is not { } next) return;
            await Task.Run(() =>
            {
                if (SystemTheme.IsAppsLightTheme() != next.Light || SystemTheme.IsSystemLightTheme() != next.Light)
                    SystemTheme.SetLightTheme(next.Light);
            });
            if (_disposed) return;
            Plan = next;
            App.Current.RefreshTheme();
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, (next.NextCheck - DateTimeOffset.Now).TotalSeconds));
            _timer.Start();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error("Theme schedule failed", ex);
            // Invalid settings need correction; transient system failures may recover.
            if (!_disposed && ex is not ArgumentException)
            {
                _timer.Interval = TimeSpan.FromMinutes(5);
                _timer.Start();
            }
        }
        finally
        {
            _gate.Release();
            if (!_disposed) UpdateStatus();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _statusTimer.Stop();
        _timer.Tick -= OnTick;
        _statusTimer.Tick -= OnStatusTick;
        _events.ClockChanged -= OnClockChanged;
        _events.SettingChanged -= OnSettingChanged;
    }
}
