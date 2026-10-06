using Llampec.Actions.Fullscreen;
using Llampec.Platform;
using Llampec.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Llampec.Flyout;

/// <summary>Disposable status and shortcut controls for the application-owned fullscreen service.</summary>
public sealed class FullscreenView : StackPanel, IDisposable
{
    private readonly FullscreenService _service;
    private readonly TextBlock _status = Note("");
    private readonly TextBlock _target = Note("");
    private readonly TextBlock _monitor = Note("");
    private readonly TextBlock _windowError = Note("");
    private readonly Button _toggle = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _shortcut = new() { PlaceholderText = FullscreenSettings.DefaultHotkey };
    private readonly TextBlock _shortcutStatus = Note("");
    private bool _disposed;

    public FullscreenView(FullscreenService service)
    {
        _service = service;
        Spacing = 10;
        Children.Add(_status);
        Children.Add(_target);
        Children.Add(_monitor);
        _toggle.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _toggle.Click += (_, _) => _service.ToggleTarget();
        Children.Add(_toggle);
        Children.Add(_windowError);
        Children.Add(Note("Hover at the bottom edge or swipe up to show the taskbar. Switching to another window restores the previous window."));

        Children.Add(new TextBlock
        {
            Text = UiText.Get("Shortcut"), FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
        });
        _shortcut.Text = App.Current.Settings.Fullscreen.Hotkey;
        AutomationProperties.SetName(_shortcut, UiText.Get("Shortcut"));
        _shortcut.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            ApplyShortcut();
            e.Handled = true;
        };
        Children.Add(_shortcut);
        Children.Add(Note("Enters or exits full screen for the active window. Leave empty to disable."));
        var apply = new Button { Content = UiText.Get("Apply"), HorizontalAlignment = HorizontalAlignment.Stretch };
        apply.Click += (_, _) => ApplyShortcut();
        Children.Add(apply);
        Children.Add(_shortcutStatus);
        SetMessage(_shortcutStatus, App.Current.FullscreenHotkeyError);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_windowError, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_shortcutStatus, AutomationLiveSetting.Polite);
        _service.Changed += OnChanged;
        _service.Refresh();
        Update();
    }

    private static TextBlock Note(string key) => new()
    {
        Text = UiText.Get(key), FontSize = 12, TextWrapping = TextWrapping.Wrap,
    };

    private static void SetMessage(TextBlock control, string? key)
    {
        control.Text = key is null ? "" : UiText.Get(key);
        control.Visibility = string.IsNullOrEmpty(key) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void Update()
    {
        if (_disposed) return;
        bool active = _service.IsActive;
        _status.Text = UiText.Get(active ? "Full screen is on" : "Full screen is off");
        _target.Text = active ? _service.ActiveTitle
            : _service.HasTarget ? _service.TargetTitle : UiText.Get("No eligible window is active.");
        _monitor.Text = active ? UiText.Format("Active on {0}", _service.ActiveMonitor) : "";
        _monitor.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _toggle.Content = UiText.Get(active ? "Exit full screen" : "Enter full screen");
        _toggle.IsEnabled = active || _service.HasTarget;
        AutomationProperties.SetName(_toggle, (string)_toggle.Content);
        AutomationProperties.SetHelpText(_toggle, active ? $"{_target.Text}. {_monitor.Text}" : _target.Text);
        SetMessage(_windowError, _service.Error);
    }

    private void ApplyShortcut()
    {
        string text = _shortcut.Text.Trim();
        if (text.Length != 0 && !Hotkey.TryParse(text, out _))
        {
            SetMessage(_shortcutStatus, "Enter a valid shortcut or leave it empty to disable it.");
            return;
        }
        if (!App.Current.TrySetFullscreenHotkey(text, out string? error))
        {
            SetMessage(_shortcutStatus, error ?? "Could not save settings.");
            return;
        }
        _shortcut.Text = App.Current.Settings.Fullscreen.Hotkey;
        SetMessage(_shortcutStatus, text.Length == 0 ? "Shortcut disabled." : "Shortcut saved.");
    }

    public void Dispose()
    {
        _disposed = true;
        _service.Changed -= OnChanged;
    }
}
