using Llampec.Devices.Logitech;
using Llampec.Settings;

namespace Llampec.Actions.Mouse;

/// <summary>
/// The optional Logitech MX mouse integration as a tile. The switch turns the saved mouse
/// settings on or off; the sub-page holds their configuration. The subtitle shows the battery
/// that the mouse reports itself, without polling.
/// </summary>
public sealed class MouseAction : QuickActionBase, IDisposable
{
    private readonly LogitechMouseService _service;
    private readonly Func<LogitechMouseSettings> _preferences;
    private readonly Func<bool, Task<bool>> _setEnabled;

    public MouseAction(LogitechMouseService service, Func<LogitechMouseSettings> preferences, Func<bool, Task<bool>> setEnabled)
    {
        _service = service;
        _preferences = preferences;
        _setEnabled = setEnabled;
        _service.Changed += OnServiceChanged;
        Refresh();
    }

    public override string Id => "mouse";
    public override string Title => "MX mouse";
    public override string Glyph => "\uE962"; // Mouse
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;

    public static string BatteryText(LogitechBattery battery)
    {
        string level = battery.Percent is int percent ? UiText.Format("{0}%", percent)
            : UiText.Get(battery.Level switch
            {
                "full" => "Battery full", "good" => "Battery good", "low" => "Battery low",
                "critical" => "Battery critical", _ => "Battery",
            });
        return battery.Full ? UiText.Format("{0} · charged", level)
            : battery.Charging ? UiText.Format("{0} · charging", level) : level;
    }

    public override void Refresh()
    {
        var preferences = _preferences();
        var status = _service.Status;
        IsAvailable = !string.IsNullOrEmpty(preferences.DevicePath);
        State = preferences.Enabled ? ActionState.On : ActionState.Off;
        Subtitle = !IsAvailable ? UiText.Get("Not set up")
            : !preferences.Enabled ? UiText.Get("Off")
            : status.State switch
            {
                LogitechMouseConnectionState.Connected => status.Battery is { } battery ? BatteryText(battery) : UiText.Get("Connected"),
                LogitechMouseConnectionState.Connecting => UiText.Get("Connecting…"),
                LogitechMouseConnectionState.Suspended => UiText.Get("Paused"),
                LogitechMouseConnectionState.Error => UiText.Get("Error"),
                _ => UiText.Get("Disconnected"),
            };
        OnChanged();
    }

    protected override async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _setEnabled(!_preferences().Enabled).ConfigureAwait(false))
            Diagnostics.Log.Warn("Logitech mouse settings could not be saved from the panel.");
    }

    private void OnServiceChanged(object? sender, EventArgs e) => Refresh();

    public void Dispose() => _service.Changed -= OnServiceChanged;
}
