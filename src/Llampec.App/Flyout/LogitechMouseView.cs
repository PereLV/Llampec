using System.Globalization;
using Llampec.Devices.Logitech;
using Llampec.Platform;
using Llampec.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Llampec.Flyout;

/// <summary>A disposable settings draft. Only Apply writes preferences or changes the mouse.</summary>
public sealed class LogitechMouseView : StackPanel, IDisposable
{
    private readonly LogitechMouseService? _service;
    private readonly Action _reposition;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _status = Note("");
    private readonly TextBlock _message = Note("");
    private readonly ToggleSwitch _enabled = new()
    {
        Header = T("Enable Logitech mouse settings"), OnContent = T("On"), OffContent = T("Off"),
    };
    private readonly ComboBox _devices = new()
    {
        Header = T("Mouse"), PlaceholderText = T("Scan and choose a mouse"), HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly Button _scan = new() { Content = T("Find mice") };
    private readonly Button _reconnect = new() { Content = T("Reconnect") };
    private readonly StackPanel _configuration = new() { Spacing = 10 };
    private readonly ContentControl _configurationHost = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false,
    };
    private readonly StackPanel _buttonRows = new() { Spacing = 8 };
    private readonly Dictionary<ushort, TextBox> _shortcuts = [];
    private readonly CheckBox _overrideDpi = new() { Content = T("Set DPI") };
    private readonly NumberBox _dpi = new()
    {
        Header = "DPI", Minimum = 1, Maximum = ushort.MaxValue, SmallChange = 50, LargeChange = 100,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.Disabled,
    };
    private readonly TextBlock _dpiRange = Note("");
    private readonly ComboBox _wheel = new() { Header = T("Wheel mode"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _threshold = new()
    {
        Header = T("SmartShift threshold (1–254)"), Minimum = 1, Maximum = 254, SmallChange = 1, LargeChange = 5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.Disabled,
    };
    private readonly CheckBox _vertical = new() { Content = T("Invert vertical scrolling"), IsThreeState = true };
    private readonly CheckBox _horizontal = new() { Content = T("Invert horizontal scrolling"), IsThreeState = true };
    private readonly Button _apply = new() { Content = T("Apply"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private LogitechMouseSettings _draft;
    private LogitechMouseDevice? _selected;
    private bool _populating;
    private bool _busy;
    private bool _disposed;

    public LogitechMouseView(LogitechMouseService? service, Action reposition)
    {
        _service = service;
        _reposition = reposition;
        _draft = App.Current.Settings.Logitech.Clone();
        Spacing = 12;
        _enabled.IsOn = _draft.Enabled;
        _overrideDpi.IsChecked = _draft.Dpi.HasValue;
        _dpi.Value = _draft.Dpi ?? 1000;
        _threshold.Value = _draft.SmartShiftThreshold;
        _vertical.IsChecked = _draft.InvertVertical;
        _horizontal.IsChecked = _draft.InvertHorizontal;
        foreach (string key in new[] { "Keep current setting", "Free spin", "Ratchet", "Automatic (SmartShift)" })
            _wheel.Items.Add(T(key));
        _wheel.SelectedIndex = _draft.WheelMode switch { "free" => 1, "ratchet" => 2, "auto" => 3, _ => 0 };

        Children.Add(_status);
        Children.Add(_enabled);
        Children.Add(_devices);
        var discovery = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        discovery.Children.Add(_scan);
        discovery.Children.Add(_reconnect);
        Children.Add(discovery);
        Children.Add(Note("Changes are saved only when you press Apply. Reconnect uses the saved settings."));

        _configuration.Children.Add(Section("Mouse buttons"));
        _configuration.Children.Add(Note("Enter a shortcut such as Win+Tab or Ctrl+C. Leave empty to keep the original action."));
        _configuration.Children.Add(_buttonRows);
        _configuration.Children.Add(_overrideDpi);
        _configuration.Children.Add(_dpi);
        _configuration.Children.Add(_dpiRange);
        _configuration.Children.Add(_wheel);
        _configuration.Children.Add(_threshold);
        _configuration.Children.Add(_vertical);
        _configuration.Children.Add(_horizontal);
        _configuration.Children.Add(Note("Checked: inverted. Unchecked: normal. Dash: keep the current setting."));
        _configuration.Children.Add(Note("Unavailable controls keep their saved values. Find mice again after connecting the mouse."));
        _configurationHost.Content = _configuration;
        Children.Add(_configurationHost);
        _apply.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Children.Add(_apply);
        Children.Add(_message);

        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        _enabled.Toggled += (_, _) => UpdateEnabledControls();
        _overrideDpi.Checked += (_, _) => UpdateEnabledControls();
        _overrideDpi.Unchecked += (_, _) => UpdateEnabledControls();
        _wheel.SelectionChanged += (_, _) => UpdateEnabledControls();
        _devices.SelectionChanged += (_, _) => SelectMouse();
        _scan.Click += async (_, _) => await ScanAsync();
        _reconnect.Click += (_, _) =>
        {
            if (_disposed || _busy) return;
            _service?.NotifyDeviceChange();
            ShowMessage("Reconnecting with the saved settings.");
        };
        _apply.Click += async (_, _) => await ApplyAsync();
        if (_service is not null) _service.Changed += OnServiceChanged;
        var active = _service?.Status.Device;
        PopulateDevices(active is null ? [] : [active]);
        RefreshStatus();
        ShowMessage(null);
    }

    private static string T(string key) => UiText.Get(key);

    private static TextBlock Note(string key) => new()
    {
        Text = T(key), FontSize = 12, TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Section(string key) => new()
    {
        Text = T(key), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };

    private async Task ScanAsync()
    {
        if (_disposed || _busy || _service is null) return;
        SetBusy(true);
        ShowMessage("Looking for connected Logitech mice…");
        try
        {
            var devices = await _service.ScanAsync(_lifetime.Token);
            if (_disposed) return;
            CaptureButtonDraft();
            PopulateDevices(devices);
            ShowMessage(devices.Count == 0 ? "No connected mouse found. Saved settings are kept." : "Choose a mouse, adjust its settings, then press Apply.");
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception)
        {
            if (!_disposed) ShowMessage(T("Could not inspect the mouse.") + " " + exception.Message);
        }
        finally
        {
            if (!_disposed) SetBusy(false);
        }
    }

    private void PopulateDevices(IReadOnlyList<LogitechMouseDevice> devices)
    {
        _populating = true;
        try
        {
            _devices.Items.Clear();
            int match = -1;
            foreach (var device in devices)
            {
                if (MatchesDraft(device)) match = _devices.Items.Count;
                _devices.Items.Add(new ComboBoxItem { Content = DeviceLabel(device), Tag = device });
            }
            if (match < 0 && !string.IsNullOrEmpty(_draft.DevicePath))
            {
                match = _devices.Items.Count;
                _devices.Items.Add(new ComboBoxItem { Content = T("Saved mouse (not connected)") });
            }
            if (match < 0 && devices.Count == 1) match = 0;
            _devices.SelectedIndex = match;
        }
        finally { _populating = false; }
        SelectMouse();
    }

    private static string DeviceLabel(LogitechMouseDevice device) => device.DeviceIndex == 255
        ? device.Name : UiText.Format("{0} · receiver slot {1}", device.Name, device.DeviceIndex);

    private bool MatchesDraft(LogitechMouseDevice device) =>
        string.Equals(device.Path, _draft.DevicePath, StringComparison.OrdinalIgnoreCase)
        && device.ProductId == _draft.ProductId && device.DeviceIndex == _draft.DeviceIndex
        && (string.IsNullOrEmpty(_draft.SerialNumber) || string.Equals(device.SerialNumber, _draft.SerialNumber, StringComparison.Ordinal))
        && (string.IsNullOrEmpty(_draft.UnitId) || string.Equals(device.UnitId, _draft.UnitId, StringComparison.OrdinalIgnoreCase));

    private void SelectMouse()
    {
        if (_disposed || _populating) return;
        CaptureButtonDraft();
        _selected = (_devices.SelectedItem as ComboBoxItem)?.Tag as LogitechMouseDevice;
        if (_selected is not null)
        {
            _draft.DevicePath = _selected.Path;
            _draft.ProductId = _selected.ProductId;
            _draft.SerialNumber = _selected.SerialNumber;
            _draft.UnitId = _selected.UnitId;
            _draft.DeviceIndex = _selected.DeviceIndex;
        }
        RebuildButtonRows();
        UpdateCapabilities();
        _reposition();
    }

    private void CaptureButtonDraft()
    {
        foreach (var (id, editor) in _shortcuts)
        {
            string shortcut = editor.Text.Trim();
            if (shortcut.Length == 0) _draft.ButtonShortcuts.Remove(id);
            else _draft.ButtonShortcuts[id] = shortcut;
        }
    }

    private void RebuildButtonRows()
    {
        _buttonRows.Children.Clear();
        _shortcuts.Clear();
        var available = _selected?.Controls.Where(control => control.Divertable).Select(control => control.Id).ToHashSet() ?? [];
        if (available.Contains(0x00C3) && !_draft.ButtonShortcuts.ContainsKey(0x00D7))
            available.Remove(0x00D7); // Prefer the physical thumb control over its virtual companion.
        foreach (ushort id in available.Concat(_draft.ButtonShortcuts.Keys).Distinct().Order())
        {
            string label = ButtonLabel(id);
            if (!available.Contains(id)) label += " · " + T("Not currently available");
            var editor = new TextBox
            {
                Header = label, Text = _draft.ButtonShortcuts.GetValueOrDefault(id, ""),
                PlaceholderText = id is 0x00C3 or 0x00D7 ? "Win+Tab" : T("Original action"),
            };
            AutomationProperties.SetName(editor, label);
            _shortcuts.Add(id, editor);
            _buttonRows.Children.Add(editor);
        }
        if (_shortcuts.Count == 0)
            _buttonRows.Children.Add(Note(_selected is null ? "Find a mouse to discover its buttons." : "This mouse exposes no remappable buttons."));
    }

    private static string ButtonLabel(ushort id) => id switch
    {
        0x0052 => T("Middle button"), 0x0053 => T("Back button"), 0x0056 => T("Forward button"),
        0x00C3 => T("Thumb button"), 0x00C4 => T("Wheel mode button"),
        0x00D7 => T("Gesture button"), 0x00FD => T("DPI button"),
        _ => UiText.Format("Mouse button {0}", $"{id:X4}"),
    };

    private void UpdateCapabilities()
    {
        if (_selected?.Dpi is { } dpi)
        {
            string entered = _dpi.Text;
            _dpi.Minimum = 1;
            _dpi.Maximum = ushort.MaxValue;
            _dpi.Minimum = dpi.Range?.Minimum ?? (dpi.Values.Count > 0 ? dpi.Values.Min() : 1);
            _dpi.Maximum = dpi.Range?.Maximum ?? (dpi.Values.Count > 0 ? dpi.Values.Max() : ushort.MaxValue);
            if (_overrideDpi.IsChecked != true) _dpi.Value = dpi.Current;
            else _dpi.Text = entered; // Retain invalid drafts for explicit Apply validation.
            _dpi.SmallChange = dpi.Range?.Step ?? 1;
            _dpiRange.Text = dpi.Range is { } range
                ? UiText.Format("DPI: {0}–{1}, in steps of {2}. Current: {3}.", range.Minimum, range.Maximum, range.Step, dpi.Current)
                : UiText.Format("Supported DPI: {0}. Current: {1}.", string.Join(", ", dpi.Values), dpi.Current);
        }
        else _dpiRange.Text = T("DPI capability is unavailable. Saved DPI is kept.");
        UpdateEnabledControls();
    }

    private void UpdateEnabledControls()
    {
        if (_disposed) return;
        bool available = !_busy && _service is not null;
        _enabled.IsEnabled = available;
        _devices.IsEnabled = available;
        _scan.IsEnabled = available;
        _reconnect.IsEnabled = available && (App.Current.Settings.Logitech.Enabled || _service?.Status.RecoveryPending == true);
        _configurationHost.IsEnabled = available && _enabled.IsOn;
        _overrideDpi.IsEnabled = _selected?.Dpi is not null;
        _dpi.IsEnabled = _selected?.Dpi is not null && _overrideDpi.IsChecked == true;
        _wheel.IsEnabled = _selected?.SmartShift is not null;
        _threshold.Visibility = _wheel.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        _threshold.IsEnabled = _selected?.SmartShift is not null;
        _vertical.IsEnabled = _selected?.VerticalWheelCanInvert == true;
        _horizontal.IsEnabled = _selected?.HorizontalWheelState is not null;
        _apply.IsEnabled = available;
    }

    private async Task ApplyAsync()
    {
        if (_disposed || _busy) return;
        LogitechMouseSettings next;
        try
        {
            if (!_enabled.IsOn)
            {
                // Turning the feature off must work even while another draft field
                // contains invalid text. Keep the last saved assignments for later.
                next = App.Current.Settings.Logitech.Clone();
                next.Enabled = false;
            }
            else
            {
                CaptureButtonDraft();
                next = _draft.Clone();
                next.Enabled = true;
                if (string.IsNullOrEmpty(next.DevicePath)) throw new ArgumentException(T("Choose a mouse before enabling its settings."));
                next.Dpi = _overrideDpi.IsChecked == true ? (ushort)WholeNumber(_dpi.Text, 1, ushort.MaxValue, "Enter a supported whole-number DPI value.") : null;
                if (next.Dpi is { } dpi && _selected?.Dpi is { } capabilities && !capabilities.Supports(dpi))
                    throw new ArgumentException(T("Enter a DPI value from the supported range and step, or the listed values."));
                next.WheelMode = _wheel.SelectedIndex switch { 1 => "free", 2 => "ratchet", 3 => "auto", _ => null };
                if (next.WheelMode == "auto")
                    next.SmartShiftThreshold = (byte)WholeNumber(_threshold.Text, 1, 254, "Enter a whole number from 1 to 254.");
                next.InvertVertical = _vertical.IsChecked;
                next.InvertHorizontal = _horizontal.IsChecked;
                foreach (var (id, shortcut) in next.ButtonShortcuts.ToArray())
                {
                    try { next.ButtonShortcuts[id] = new KeyboardShortcut(shortcut).Text; }
                    catch (ArgumentException)
                    {
                        throw new ArgumentException(UiText.Format("Invalid shortcut for {0}. Use Win+Tab, Ctrl+C or F8.", ButtonLabel(id)));
                    }
                }
                next.Validate();
            }
        }
        catch (ArgumentException exception)
        {
            ShowMessage(T(exception.Message));
            return;
        }

        SetBusy(true);
        ShowMessage("Saving mouse settings…");
        try
        {
            bool saved = await App.Current.TryApplyLogitechSettingsAsync(next);
            if (_disposed) return;
            if (!saved)
            {
                ShowMessage("Could not save settings. Check access to the Llampec settings folder.");
                return;
            }
            _draft = next.Clone();
            // A successful Apply is the only time that controls are reloaded from
            // saved preferences. In particular, disabling discards invalid edits.
            _shortcuts.Clear();
            _enabled.IsOn = _draft.Enabled;
            _overrideDpi.IsChecked = _draft.Dpi.HasValue;
            _dpi.Value = _draft.Dpi ?? 1000;
            _threshold.Value = _draft.SmartShiftThreshold;
            _vertical.IsChecked = _draft.InvertVertical;
            _horizontal.IsChecked = _draft.InvertHorizontal;
            _wheel.SelectedIndex = _draft.WheelMode switch { "free" => 1, "ratchet" => 2, "auto" => 3, _ => 0 };
            var known = _devices.Items.OfType<ComboBoxItem>().Select(item => item.Tag).OfType<LogitechMouseDevice>().ToArray();
            if (_service?.Status.Device is { } current)
                known = known.Select(device => device.Path == current.Path && device.ProductId == current.ProductId
                    && device.DeviceIndex == current.DeviceIndex ? current : device).ToArray();
            PopulateDevices(known);
            ShowMessage("Mouse settings saved.");
            RefreshStatus();
        }
        catch (Exception exception)
        {
            if (!_disposed) ShowMessage(T("Could not apply mouse settings.") + " " + exception.Message);
        }
        finally
        {
            if (!_disposed) SetBusy(false);
        }
    }

    private static int WholeNumber(string text, int minimum, int maximum, string error)
    {
        if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double value)
            || !double.IsFinite(value) || value != Math.Truncate(value) || value < minimum || value > maximum)
            throw new ArgumentException(T(error));
        return (int)value;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateEnabledControls();
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) RefreshStatus();
        else DispatcherQueue.TryEnqueue(RefreshStatus);
    }

    private void RefreshStatus()
    {
        if (_disposed) return;
        if (_service is null)
        {
            _status.Text = T("Logitech mouse support is unavailable. Restart Llampec and try again.");
            UpdateEnabledControls();
            return;
        }
        var status = _service.Status;
        string message = status.State switch
        {
            LogitechMouseConnectionState.Disabled => T("Mouse settings are off."),
            LogitechMouseConnectionState.Connecting => T("Connecting to the saved mouse…"),
            LogitechMouseConnectionState.Connected => UiText.Format("Connected: {0}", status.Device?.Name ?? "Logitech"),
            LogitechMouseConnectionState.Suspended => T("Mouse settings are paused while Windows sleeps."),
            LogitechMouseConnectionState.Error => T("Mouse settings could not be applied."),
            _ => T("Waiting for the saved mouse. Settings will resume when it reconnects."),
        };
        if (status.Error is { Length: > 0 } error) message += "\n" + T(error);
        if (status.RecoveryPending) message += "\n" + T("Restoration is pending. Reconnect the same mouse before changing devices.");
        _status.Text = message;
        // Status changes never overwrite unsaved controls. Discovery remains an
        // explicit action, so rebuilding or reopening this page changes nothing.
        UpdateEnabledControls();
        _reposition();
    }

    private void ShowMessage(string? message)
    {
        if (_disposed) return;
        _message.Text = message is null ? "" : T(message);
        _message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        _reposition();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_service is not null) _service.Changed -= OnServiceChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
