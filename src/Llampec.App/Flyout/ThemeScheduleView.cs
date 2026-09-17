using Llampec.Platform;
using Llampec.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Llampec.Flyout;

/// <summary>Created on navigation, with draft values until Apply. No background location tracking.</summary>
public sealed class ThemeScheduleView : StackPanel, IDisposable
{
    private readonly ComboBox _mode = new() { Header = UiText.Get("Schedule"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TimePicker _light = new() { Header = UiText.Get("Light mode"), ClockIdentifier = "24HourClock", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TimePicker _dark = new() { Header = UiText.Get("Dark mode"), ClockIdentifier = "24HourClock", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _latitude = new() { Header = UiText.Get("Latitude"), Minimum = -90, Maximum = 90, PlaceholderText = "-90 … 90" };
    private readonly NumberBox _longitude = new() { Header = UiText.Get("Longitude"), Minimum = -180, Maximum = 180, PlaceholderText = "-180 … 180" };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public ThemeScheduleView(Action<bool> holdOpen)
    {
        Spacing = 12;
        var settings = App.Current.Settings;
        var schedule = settings.ThemeSchedule ?? new();
        _mode.Items.Add(UiText.Get("Off")); _mode.Items.Add(UiText.Get("Fixed hours")); _mode.Items.Add(UiText.Get("Sunrise / sunset"));
        _mode.SelectedIndex = Enum.IsDefined(schedule.Mode) ? (int)schedule.Mode : 0;
        _light.Time = schedule.LightAt.ToTimeSpan(); _dark.Time = schedule.DarkAt.ToTimeSpan();
        _latitude.Value = settings.LocationLatitude ?? double.NaN;
        _longitude.Value = settings.LocationLongitude ?? double.NaN;
        var hours = new StackPanel { Spacing = 8 };
        hours.Children.Add(_light); hours.Children.Add(_dark);
        var sun = new StackPanel { Spacing = 8 };
        sun.Children.Add(new TextBlock { Text = UiText.Get("Light at sunrise, dark at sunset. Coordinates are saved for offline calculations."), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        sun.Children.Add(_latitude); sun.Children.Add(_longitude);
        var locate = new Button { Content = UiText.Get("Use current location") };
        locate.Click += async (_, _) =>
        {
            locate.IsEnabled = false;
            holdOpen(true);
            try
            {
                var location = await SystemLocation.TryGetAsync(TimeSpan.FromSeconds(15), _lifetime.Token);
                if (_disposed) return;
                if (location is { } point) { _latitude.Value = point.Latitude; _longitude.Value = point.Longitude; _status.Text = UiText.Get("Location detected. Apply to save."); }
                else _status.Text = UiText.Get("Location unavailable or permission denied. Enter coordinates manually.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_disposed) _status.Text = UiText.Get("Location unavailable: ") + ex.Message; }
            finally { holdOpen(false); if (!_disposed) locate.IsEnabled = true; }
        };
        sun.Children.Add(locate);
        Children.Add(_mode); Children.Add(hours); Children.Add(sun);
        Children.Add(new TextBlock { Text = UiText.Get("Changes Windows and apps while Llampec is running. A manual toggle lasts until the next scheduled change."), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var apply = new Button { Content = UiText.Get("Apply"), HorizontalAlignment = HorizontalAlignment.Stretch };
        apply.Click += async (_, _) =>
        {
            var draft = new ThemeScheduleSettings { Mode = (ThemeScheduleMode)_mode.SelectedIndex,
                LightAt = TimeOnly.FromTimeSpan(_light.Time), DarkAt = TimeOnly.FromTimeSpan(_dark.Time) };
            double? lat = double.IsNaN(_latitude.Value) ? null : _latitude.Value;
            double? lon = double.IsNaN(_longitude.Value) ? null : _longitude.Value;
            try
            {
                var plan = ThemeSchedule.Calculate(draft, DateTimeOffset.Now, TimeZoneInfo.Local, lat, lon);
                var previous = settings.ThemeSchedule ?? new();
                var previousLat = settings.LocationLatitude;
                var previousLon = settings.LocationLongitude;
                settings.ThemeSchedule = draft;
                settings.LocationLatitude = lat; settings.LocationLongitude = lon;
                if (!SettingsStore.Save(settings))
                {
                    settings.ThemeSchedule = previous;
                    settings.LocationLatitude = previousLat; settings.LocationLongitude = previousLon;
                    _status.Text = UiText.Get("Could not save settings. Check access to the Llampec settings folder.");
                    return;
                }
                apply.IsEnabled = false;
                if (App.Current.Scheduler is { } scheduler) await scheduler.ApplyAsync();
                if (_disposed) return;
                _status.Text = (App.Current.Scheduler?.Error is { } error ? UiText.Get(error) : null) ?? Describe(plan);
            }
            catch (Exception ex) { _status.Text = UiText.Get(ex.Message); }
            finally { if (!_disposed) apply.IsEnabled = true; }
        };
        Children.Add(apply); Children.Add(_status);
        void UpdateMode()
        {
            hours.Visibility = _mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            sun.Visibility = _mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = UiText.Get("Apply to save changes.");
        }
        _mode.SelectionChanged += (_, _) => UpdateMode();
        _light.TimeChanged += (_, _) => _status.Text = UiText.Get("Apply to save changes.");
        _dark.TimeChanged += (_, _) => _status.Text = UiText.Get("Apply to save changes.");
        _latitude.ValueChanged += (_, _) => _status.Text = UiText.Get("Apply to save changes.");
        _longitude.ValueChanged += (_, _) => _status.Text = UiText.Get("Apply to save changes.");
        UpdateMode();
        try { _status.Text = (App.Current.Scheduler?.Error is { } error ? UiText.Get(error) : null) ?? Describe(ThemeSchedule.Calculate(schedule, DateTimeOffset.Now, TimeZoneInfo.Local, settings.LocationLatitude, settings.LocationLongitude)); }
        catch (ArgumentException ex) { _status.Text = UiText.Get(ex.Message); }
    }

    private static string Describe(ThemeSchedulePlan? plan) => plan is not { } next ? UiText.Get("Schedule is off.")
        : next.PolarDayOrNight ? UiText.Get("Polar day/night: no sunrise or sunset today. Checked again tomorrow.")
        : UiText.Format("Next: {0} · {1:g}", UiText.Get(next.NextLight ? "Light mode" : "Dark mode"), TimeZoneInfo.ConvertTime(next.NextCheck, TimeZoneInfo.Local));

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
