using Llampec.Actions.AlwaysOnTop;
using Llampec.Platform;
using Llampec.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Llampec.Flyout;

/// <summary>Disposable controls for the application-owned pinning service.</summary>
public sealed class AlwaysOnTopView : StackPanel, IDisposable
{
    private readonly AlwaysOnTopService _service;
    private readonly StackPanel _windows = new() { Spacing = 4 };
    private readonly Dictionary<nint, WindowRow> _rows = [];
    private readonly TextBlock _count = Note("");
    private readonly TextBlock _windowError = Note("");
    private readonly Button _restartAdministrator = new()
    {
        Content = UiText.Get("Restart as administrator"),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _empty = Note("No windows available.");
    private readonly Button _unpinAll = new() { Content = UiText.Get("Unpin all") };
    private readonly ToggleSwitch _border = new()
    {
        Header = UiText.Get("Show border"), OnContent = UiText.Get("On"), OffContent = UiText.Get("Off"),
    };
    private readonly NumberBox _thickness = new()
    {
        Header = UiText.Get("Border thickness"), Minimum = AlwaysOnTopSettings.MinimumBorderThickness,
        Maximum = AlwaysOnTopSettings.MaximumBorderThickness, SmallChange = 1, LargeChange = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _appearanceError = Note("");
    private readonly TextBox _shortcut = new() { PlaceholderText = "Ctrl+Alt+T" };
    private readonly TextBlock _shortcutStatus = Note("");
    private readonly DispatcherQueueTimer _refreshTimer;
    private bool _updatingAppearance;
    private bool _disposed;
    private string? _restartError;

    public AlwaysOnTopView(AlwaysOnTopService service, Action<bool>? holdOpen = null)
    {
        _service = service;
        Spacing = 10;
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(150);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += OnRefreshTick;

        Children.Add(Section("Choose a window"));
        Children.Add(_count);
        var windowScroll = new ScrollViewer
        {
            Content = _windows, MaxHeight = 208,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(windowScroll, UiText.Get("Choose a window"));
        Children.Add(windowScroll);
        Children.Add(_empty);
        Children.Add(_windowError);
        _restartAdministrator.Click += (_, _) =>
        {
            _restartError = null;
            holdOpen?.Invoke(true);
            try
            {
                if (!App.Current.TryRestartAsAdministrator(out string? error))
                {
                    _restartError = error;
                    SetMessage(_windowError, error);
                }
            }
            finally { holdOpen?.Invoke(false); }
        };
        Children.Add(_restartAdministrator);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var refresh = new Button
        {
            Content = new FontIcon { Glyph = "\uE72C", FontSize = 16 },
        };
        AutomationProperties.SetName(refresh, UiText.Get("Refresh windows"));
        ToolTipService.SetToolTip(refresh, UiText.Get("Refresh windows"));
        refresh.Click += (_, _) => RefreshWindows();
        _unpinAll.Click += (_, _) => { _restartError = null; _service.UnpinAll(); UpdateWindows(); };
        buttons.Children.Add(refresh);
        buttons.Children.Add(_unpinAll);
        Children.Add(buttons);

        Children.Add(Section("Appearance", topMargin: 6));
        var preferences = App.Current.Settings.AlwaysOnTop ?? new();
        _border.IsOn = preferences.ShowBorder;
        _thickness.Value = preferences.BorderThickness;
        _thickness.IsEnabled = _border.IsOn;
        AutomationProperties.SetName(_border, UiText.Get("Show border"));
        AutomationProperties.SetName(_thickness, UiText.Get("Border thickness"));
        AutomationProperties.SetHelpText(_thickness, UiText.Get("Enter a whole number from 1 to 8."));
        _border.Toggled += (_, _) => SaveAppearance(borderChanged: true);
        _thickness.ValueChanged += (_, _) => SaveAppearance(borderChanged: false);
        Children.Add(_border);
        Children.Add(_thickness);
        Children.Add(_appearanceError);

        Children.Add(Section("Shortcut", topMargin: 6));
        _shortcut.Text = preferences.Hotkey;
        AutomationProperties.SetName(_shortcut, UiText.Get("Shortcut"));
        _shortcut.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            ApplyShortcut();
            e.Handled = true;
        };
        Children.Add(_shortcut);
        Children.Add(Note("Pins or unpins the active window. Leave empty to disable."));
        var apply = new Button { Content = UiText.Get("Apply"), HorizontalAlignment = HorizontalAlignment.Stretch };
        apply.Click += (_, _) => ApplyShortcut();
        Children.Add(apply);
        Children.Add(_shortcutStatus);
        SetMessage(_appearanceError, null);
        SetMessage(_shortcutStatus, App.Current.AlwaysOnTopHotkeyError);
        AutomationProperties.SetLiveSetting(_windowError, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_appearanceError, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_shortcutStatus, AutomationLiveSetting.Polite);

        _service.Changed += OnChanged;
        RefreshWindows();
    }

    private static TextBlock Note(string key) => new()
    {
        Text = UiText.Get(key), FontSize = 12, TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Section(string key, double topMargin = 0) => new()
    {
        Text = UiText.Get(key), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, topMargin, 0, 0),
    };

    private static void SetMessage(TextBlock control, string? key)
    {
        control.Text = key is null ? "" : UiText.Get(key);
        control.Visibility = string.IsNullOrEmpty(key) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshWindows()
    {
        if (_disposed) return;
        _restartError = null;
        _service.Refresh();
        _refreshTimer.Stop();
        UpdateWindows();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) ScheduleRefresh();
        else DispatcherQueue.TryEnqueue(ScheduleRefresh);
    }

    private void ScheduleRefresh()
    {
        if (_disposed) return;
        // Coalesce native events. Existing controls are retained when titles or pin states change.
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnRefreshTick(DispatcherQueueTimer sender, object args) => UpdateWindows();

    private void UpdateWindows()
    {
        if (_disposed) return;
        var available = _service.GetWindows().OrderByDescending(window => window.IsPinned)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var present = available.Select(window => window.Handle).ToHashSet();
        foreach (var handle in _rows.Keys.Where(handle => !present.Contains(handle)).ToArray())
        {
            _windows.Children.Remove(_rows[handle].Button);
            _rows.Remove(handle);
        }

        for (int index = 0; index < available.Length; index++)
        {
            var window = available[index];
            if (!_rows.TryGetValue(window.Handle, out var row))
            {
                row = new WindowRow();
                nint handle = window.Handle;
                row.Button.Click += (_, _) => { _restartError = null; _service.Toggle(handle); UpdateWindows(); };
                _rows.Add(handle, row);
            }
            row.Title.Text = window.Title;
            row.Button.IsChecked = window.IsPinned;
            row.Pin.Opacity = window.IsPinned ? 1 : .35;
            string operation = UiText.Format(window.IsPinned ? "Unpin: {0}" : "Pin: {0}", window.Title);
            AutomationProperties.SetName(row.Button, operation);
            ToolTipService.SetToolTip(row.Button, operation);
            int oldIndex = _windows.Children.IndexOf(row.Button);
            if (oldIndex == index) continue;
            var focus = row.Button.FocusState;
            if (oldIndex >= 0) _windows.Children.RemoveAt(oldIndex);
            _windows.Children.Insert(index, row.Button);
            if (focus != FocusState.Unfocused) row.Button.Focus(focus);
        }

        _count.Text = UiText.Format("{0} pinned", _service.PinnedCount);
        _unpinAll.IsEnabled = _service.PinnedCount > 0;
        _empty.Visibility = available.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_service.Error != AdministratorRestart.PermissionError) _restartError = null;
        SetMessage(_windowError, _restartError ?? _service.Error);
        _restartAdministrator.Visibility = _service.Error == AdministratorRestart.PermissionError
            && !AdministratorRestart.IsAdministrator ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveAppearance(bool borderChanged)
    {
        if (_disposed || _updatingAppearance) return;
        _thickness.IsEnabled = _border.IsOn;
        var settings = App.Current.Settings;
        var previous = settings.AlwaysOnTop;
        double thickness = _thickness.Value;
        if (!double.IsFinite(thickness) || thickness != Math.Truncate(thickness)
            || thickness is < AlwaysOnTopSettings.MinimumBorderThickness or > AlwaysOnTopSettings.MaximumBorderThickness)
        {
            if (!borderChanged)
            {
                SetMessage(_appearanceError, "Enter a whole number from 1 to 8.");
                return;
            }
            // A cleared number field must not prevent switching the border off.
            thickness = previous?.BorderThickness ?? AlwaysOnTopSettings.DefaultBorderThickness;
            _updatingAppearance = true;
            try { _thickness.Value = thickness; }
            finally { _updatingAppearance = false; }
        }

        var next = new AlwaysOnTopSettings
        {
            ShowBorder = _border.IsOn, BorderThickness = (int)thickness,
            Hotkey = previous?.Hotkey ?? AlwaysOnTopSettings.DefaultHotkey,
        };
        settings.AlwaysOnTop = next;
        if (!SettingsStore.Save(settings))
        {
            settings.AlwaysOnTop = previous!;
            _updatingAppearance = true;
            try
            {
                _border.IsOn = previous?.ShowBorder ?? true;
                _thickness.Value = previous?.BorderThickness ?? AlwaysOnTopSettings.DefaultBorderThickness;
                _thickness.IsEnabled = _border.IsOn;
            }
            finally { _updatingAppearance = false; }
            SetMessage(_appearanceError, "Could not save settings.");
            return;
        }
        SetMessage(_appearanceError, null);
        _service.ApplyAppearance();
        UpdateWindows();
    }

    private void ApplyShortcut()
    {
        string text = _shortcut.Text.Trim();
        if (text.Length != 0 && !Hotkey.TryParse(text, out _))
        {
            SetMessage(_shortcutStatus, "Enter a valid shortcut or leave it empty to disable it.");
            return;
        }
        if (!App.Current.TrySetAlwaysOnTopHotkey(text, out string? error))
        {
            SetMessage(_shortcutStatus, error ?? "Could not save settings.");
            return;
        }
        _shortcut.Text = App.Current.Settings.AlwaysOnTop.Hotkey;
        SetMessage(_shortcutStatus, text.Length == 0 ? "Shortcut disabled." : "Shortcut saved.");
    }

    public void Dispose()
    {
        _disposed = true;
        _service.Changed -= OnChanged;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
    }

    private sealed class WindowRow
    {
        public ToggleButton Button { get; } = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40, Padding = new Thickness(10, 6, 10, 6),
        };
        public TextBlock Title { get; } = new()
        {
            FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        public FontIcon Pin { get; } = new()
        {
            Glyph = "\uE718", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14,
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };

        public WindowRow()
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Pin, 1);
            content.Children.Add(Title);
            content.Children.Add(Pin);
            Button.Content = content;
        }
    }
}
