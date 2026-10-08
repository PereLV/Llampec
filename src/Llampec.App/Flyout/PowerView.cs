using System.Globalization;
using Llampec.Actions.Power;
using Llampec.Platform;
using Llampec.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Llampec.Flyout;

/// <summary>Power mode, plan, lid/power-button actions and battery details. Changes apply immediately.</summary>
public sealed class PowerView : StackPanel, IDisposable
{
    private static readonly PowerMode[] Modes = [PowerMode.BestEfficiency, PowerMode.Balanced, PowerMode.BestPerformance];
    private readonly PowerAction _action;
    private readonly TextBlock _modeScope = Note("");
    private readonly List<RadioButton> _modes = [];
    private readonly TextBlock _modeNote = Note("");
    private readonly ComboBox _plan = new() { Header = T("Power plan"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _buttons = new() { Spacing = 8 };
    private readonly StackPanel _battery = new() { Spacing = 4 };
    private readonly TextBlock _energySaver = Note("");
    private readonly TextBlock _message = Note("");
    private bool _updating;
    private bool _disposed;

    public PowerView(PowerAction action)
    {
        _action = action;
        Spacing = 12;
        var modes = new StackPanel();
        modes.Children.Add(Section("Power mode"));
        modes.Children.Add(_modeScope);
        foreach (var mode in Modes)
        {
            var radio = new RadioButton { Content = T(PowerAction.ModeName(mode)), GroupName = "power-mode", MinHeight = 40 };
            radio.Click += (_, _) => Apply(() => PowerOptions.SetMode(mode));
            _modes.Add(radio);
            modes.Children.Add(radio);
        }
        modes.Children.Add(_modeNote);
        Children.Add(modes);
        _plan.SelectionChanged += (_, _) =>
        {
            if (!_updating && _plan.SelectedItem is ComboBoxItem { Tag: Guid plan }) Apply(() => PowerOptions.SetPlan(plan));
        };
        Children.Add(_plan);
        Children.Add(_buttons);
        var energySaver = new StackPanel { Spacing = 4 };
        energySaver.Children.Add(Section("Energy saver"));
        energySaver.Children.Add(_energySaver);
        var saverSettings = new HyperlinkButton { Content = T("Energy saver settings"), Padding = new Thickness(0) };
        saverSettings.Click += (_, _) => OpenSettings("ms-settings:batterysaver");
        energySaver.Children.Add(saverSettings);
        Children.Add(energySaver);
        Children.Add(_battery);
        var more = new HyperlinkButton { Content = T("More power settings"), Padding = new Thickness(0) };
        more.Click += (_, _) => OpenSettings("ms-settings:powersleep");
        Children.Add(more);
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        Children.Add(_message);
        SetMessage(null);
        _action.Changed += OnChanged;
        _action.Refresh();
        Update();
    }

    private static string T(string key) => UiText.Get(key);

    private static TextBlock Note(string key) => new() { Text = T(key), FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Section(string key) => new()
    {
        Text = T(key), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0),
    };

    private void Apply(Action change)
    {
        if (_disposed) return;
        try
        {
            change();
            SetMessage(null);
        }
        catch (Exception error)
        {
            Diagnostics.Log.Warn($"Power setting failed: {error.Message}");
            SetMessage("Windows did not accept the change.");
        }
        // Re-read after leaving the control's event handler; Update rebuilds these controls.
        DispatcherQueue.TryEnqueue(() => { if (!_disposed) _action.Refresh(); });
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Update(); else DispatcherQueue.TryEnqueue(Update);
    }

    private void Update()
    {
        if (_disposed || _action.Snapshot is not { } power) return;
        _updating = true;
        try
        {
            _modeScope.Text = T(!power.HasBattery ? "Applies to this PC." : power.OnBattery ? "Applies while on battery." : "Applies while plugged in.");
            for (int i = 0; i < Modes.Length; i++)
            {
                _modes[i].IsChecked = power.Mode == Modes[i];
                _modes[i].IsEnabled = power.ModeSupported;
            }
            string? note = !power.ModeSupported
                ? power.ActivePlan == PowerOptions.BalancedPlan ? "Windows does not offer power modes here." : "Power modes are available with the Balanced plan."
                : power.EnergySaverOn ? "While energy saver is on, Windows manages the power mode." : null;
            _modeNote.Text = note is null ? "" : T(note);
            _modeNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;

            PowerDetails details;
            try { details = PowerOptions.ReadDetails(power); }
            catch (Exception error)
            {
                Diagnostics.Log.Warn($"Power options are unavailable: {error.Message}");
                details = new(null, [], null, null);
            }
            _plan.Items.Clear();
            foreach (var plan in details.Plans)
            {
                _plan.Items.Add(new ComboBoxItem { Content = plan.Name, Tag = plan.Id });
                if (plan.Id == power.ActivePlan) _plan.SelectedIndex = _plan.Items.Count - 1;
            }
            // A single plan offers no choice; Windows 11 hides plans on modern standby devices.
            _plan.Visibility = details.Plans.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            _buttons.Children.Clear();
            if (details.Lid is not null || details.PowerButton is not null) _buttons.Children.Add(Section("Lid and power button"));
            if (details.Lid is { } lid) AddButton("When I close the lid", lid, power.HasBattery);
            if (details.PowerButton is { } button) AddButton("When I press the power button", button, power.HasBattery);
            _buttons.Visibility = _buttons.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            _energySaver.Text = EnergySaverText(power.EnergySaverOn, details.EnergySaverThreshold);
            UpdateBattery(power.HasBattery);
        }
        finally { _updating = false; }
    }

    private static string EnergySaverText(bool on, int? threshold)
    {
        string state = T(on ? "On" : "Off");
        return threshold switch
        {
            null => state,
            0 => $"{state} · {T("does not turn on automatically")}",
            >= 100 => $"{state} · {T("always on")}",
            _ => $"{state} · {UiText.Format("turns on at {0}% battery", threshold)}",
        };
    }

    private void AddButton(string title, PowerButtonState state, bool hasBattery)
    {
        _buttons.Children.Add(Note(title));
        var row = new Grid { ColumnSpacing = 8 };
        if (hasBattery)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Choice(state, battery: true));
        }
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var pluggedIn = Choice(state, battery: false);
        Grid.SetColumn(pluggedIn, row.ColumnDefinitions.Count - 1);
        row.Children.Add(pluggedIn);
        _buttons.Children.Add(row);
    }

    private ComboBox Choice(PowerButtonState state, bool battery)
    {
        var box = new ComboBox
        {
            Header = T(battery ? "On battery" : "Plugged in"), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        uint current = battery ? state.Battery : state.PluggedIn;
        foreach (var choice in state.Choices)
        {
            box.Items.Add(new ComboBoxItem { Content = ChoiceName(choice), Tag = choice.Value });
            if (choice.Value == current) box.SelectedIndex = box.Items.Count - 1;
        }
        AutomationProperties.SetName(box, $"{T(state.Setting == PowerButtonSetting.Lid ? "When I close the lid" : "When I press the power button")}: {box.Header}");
        box.SelectionChanged += (_, _) =>
        {
            if (!_updating && box.SelectedItem is ComboBoxItem { Tag: uint value } && value != current)
                Apply(() => PowerOptions.SetButtonAction(state.Setting, battery, value));
        };
        return box;
    }

    private static string ChoiceName(PowerActionChoice choice) => choice.Value switch
    {
        0 => T("Do nothing"),
        1 => T("Sleep"),
        2 => T("Hibernate"),
        3 => T("Shut down"),
        4 => T("Turn off the display"),
        _ => choice.SystemName ?? choice.Value.ToString(CultureInfo.CurrentCulture),
    };

    private void UpdateBattery(bool hasBattery)
    {
        _battery.Children.Clear();
        BatteryHealth? health = null;
        if (hasBattery)
        {
            try { health = PowerOptions.ReadBattery(); }
            catch (Exception error) { Diagnostics.Log.Warn($"Battery report is unavailable: {error.Message}"); }
        }
        if (health is null)
        {
            _battery.Visibility = Visibility.Collapsed;
            return;
        }
        _battery.Visibility = Visibility.Visible;
        _battery.Children.Add(Section("Battery"));
        string charge = health.Percent is int percent ? UiText.Format("{0}%", percent) : "";
        if (health.RateWatts is double watts)
            charge = UiText.Format(health.Charging ? "{0} · charging at {1} W" : "{0} · discharging at {1} W",
                charge, watts.ToString("0.0", CultureInfo.CurrentCulture));
        if (charge.Length != 0) _battery.Children.Add(Note(charge));
        if (health.FullWattHours is double full && health.DesignWattHours is double design && health.HealthPercent is int capacity)
            _battery.Children.Add(Note(UiText.Format("Capacity: {0} of {1} Wh ({2}% of the original)",
                full.ToString("0.0", CultureInfo.CurrentCulture), design.ToString("0.0", CultureInfo.CurrentCulture), capacity)));
    }

    private async void OpenSettings(string uri)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(uri))) throw new InvalidOperationException();
        }
        catch { if (!_disposed) SetMessage("Could not open Windows Settings."); }
    }

    private void SetMessage(string? key)
    {
        _message.Text = key is null ? "" : T(key);
        _message.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        _disposed = true;
        _action.Changed -= OnChanged;
    }
}
