using Llampec.Actions.Camera;
using Llampec.Actions.Mouse;
using Llampec.Devices.Logitech;
using Llampec.Platform;
using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public sealed class PowerCameraMouseTests
{
    [Fact]
    public void Camera_subtitle_names_the_camera_and_each_app_once()
    {
        Assert.Equal(UiText.Get("Not in use"), CameraAction.Describe([]));
        Assert.Equal("Surface Camera Front · Camera, Teams", CameraAction.Describe(
            [new CameraUse("Surface Camera Front", ["Camera", "Teams"])]));
        Assert.Equal(UiText.Format("{0} cameras", 2) + " · Teams, Camera", CameraAction.Describe(
            [new CameraUse("Front", ["Teams"]), new CameraUse("Rear", ["teams", "Camera"])]));
    }

    [Fact]
    public void Mouse_battery_text_prefers_percentage_and_shows_charging()
    {
        Assert.Equal(UiText.Format("{0}%", 70), MouseAction.BatteryText(new LogitechBattery(70, null, false, false)));
        Assert.Equal(UiText.Format("{0} · charging", UiText.Format("{0}%", 40)),
            MouseAction.BatteryText(new LogitechBattery(40, null, true, false)));
        Assert.Equal(UiText.Format("{0} · charged", UiText.Get("Battery full")),
            MouseAction.BatteryText(new LogitechBattery(null, "full", false, true)));
    }

    [Fact]
    public void Power_state_is_readable_without_changing_settings()
    {
        // Read-only: the active plan is listed and a balanced-plan system reports its mode.
        var power = PowerOptions.Read();
        if (power.ModeSupported) Assert.NotNull(power.Mode);
        var details = PowerOptions.ReadDetails(power);
        Assert.Contains(details.Plans, plan => plan.Id == power.ActivePlan);
        if (power.HasBattery) Assert.InRange(details.EnergySaverThreshold ?? 0, 0, 100);
        foreach (var button in new[] { details.Lid, details.PowerButton })
            if (button is not null) Assert.Contains(button.Choices, choice => choice.Value == button.PluggedIn);
    }
}
