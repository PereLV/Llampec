using Llampec.Actions;
using Llampec.Actions.Caffeine;
using Llampec.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Llampec.Flyout;

/// <summary>Draft preferences; no countdown timer and no controls retained when hidden.</summary>
public sealed class CaffeineView : StackPanel, IDisposable
{
    private readonly CaffeineAction _action;
    private readonly ComboBox _duration = new() { Header = UiText.Get("Duration"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _minutes = new() { Header = UiText.Get("Minutes (1–1440)"), Minimum = 1, Maximum = 1440, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ToggleSwitch _display = new() { Header = UiText.Get("Keep the screen on"), OnContent = UiText.Get("On"), OffContent = UiText.Get("Off") };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new();
    private readonly Button _stop = new() { Content = UiText.Get("Deactivate") };
    private bool _disposed;

    public CaffeineView(CaffeineAction action)
    {
        _action = action;
        Spacing = 12;
        var prefs = App.Current.Settings.Caffeine ?? new();
        int minutes = CaffeineSettings.IsValid(prefs.Minutes) ? prefs.Minutes : 60;
        foreach (string key in new[] { "Indefinite", "1 hour", "2 hours", "3 hours", "Custom" }) _duration.Items.Add(UiText.Get(key));
        _duration.SelectedIndex = minutes switch { 0 => 0, 60 => 1, 120 => 2, 180 => 3, _ => 4 };
        _minutes.Value = minutes == 0 ? 60 : minutes;
        _minutes.Visibility = _duration.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        _duration.SelectionChanged += (_, _) => _minutes.Visibility = _duration.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        _display.IsOn = prefs.KeepDisplayOn;
        Children.Add(_status); Children.Add(_duration); Children.Add(_minutes); Children.Add(_display);
        Children.Add(new TextBlock { Text = UiText.Get("Prevents automatic sleep. Manual sleep ends the session. Windows may limit it on battery."), FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _apply.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _apply.Click += (_, _) => Apply();
        _stop.Click += (_, _) => _action.Stop();
        buttons.Children.Add(_apply); buttons.Children.Add(_stop); Children.Add(buttons);
        _action.Changed += OnChanged;
        Update();
    }

    private void Apply()
    {
        double minutes = _duration.SelectedIndex switch { 0 => 0, 1 => 60, 2 => 120, 3 => 180, _ => _minutes.Value };
        if (!double.IsFinite(minutes) || minutes != Math.Truncate(minutes) || !CaffeineSettings.IsValid((int)minutes)
            || (_duration.SelectedIndex == 4 && minutes == 0))
        {
            _status.Text = UiText.Get("Enter a whole number from 1 to 1440.");
            return;
        }
        var settings = App.Current.Settings;
        var previous = settings.Caffeine;
        var next = new CaffeineSettings { Minutes = (int)minutes, KeepDisplayOn = _display.IsOn };
        settings.Caffeine = next;
        if (!SettingsStore.Save(settings))
        {
            settings.Caffeine = previous;
            _status.Text = UiText.Get("Could not save settings.");
            return;
        }
        _action.Start(next);
        Update();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Update(); else DispatcherQueue.TryEnqueue(Update);
    }

    private void Update()
    {
        if (_disposed) return;
        bool active = _action.State == ActionState.On;
        _status.Text = _action.StatusText;
        _apply.Content = UiText.Get(active ? "Apply and restart" : "Activate");
        _stop.IsEnabled = active;
    }

    public void Dispose() { _disposed = true; _action.Changed -= OnChanged; }
}
