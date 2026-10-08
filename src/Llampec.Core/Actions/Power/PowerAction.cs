using Llampec.Platform;
using Llampec.Settings;

namespace Llampec.Actions.Power;

/// <summary>
/// Power mode as a tile: tapping cycles efficiency, balanced and performance for the current power
/// source, like Windows Settings. The sub-page adds plans, lid/button actions and battery details.
/// State is read when the panel opens; nothing runs while it is closed. The tile reads only
/// the power status, active plan and mode; the sub-page reads the rest.
/// </summary>
public sealed class PowerAction : QuickActionBase
{
    public override string Id => "power";
    public override string Title => "Power";
    public override string Glyph => "\uEC4A"; // SpeedHigh
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;
    public PowerSnapshot? Snapshot { get; private set; }

    public static string ModeName(PowerMode mode) => mode switch
    {
        PowerMode.BestEfficiency => "Best power efficiency",
        PowerMode.Balanced => "Balanced",
        PowerMode.BestPerformance => "Best performance",
        _ => "Custom power mode",
    };

    private static string ShortModeName(PowerMode mode) => mode switch
    {
        PowerMode.BestEfficiency => "Efficiency",
        PowerMode.Balanced => "Balanced",
        PowerMode.BestPerformance => "Performance",
        _ => "Custom",
    };

    public override void Refresh()
    {
        try { Snapshot = PowerOptions.Read(); }
        catch (Exception error)
        {
            Diagnostics.Log.Warn($"Power state is unavailable: {error.Message}");
            Snapshot = null;
        }
        var snapshot = Snapshot;
        IsAvailable = snapshot is { ModeSupported: true, Mode: not null };
        State = snapshot?.Mode is PowerMode.BestEfficiency or PowerMode.BestPerformance ? ActionState.On : ActionState.Off;
        string? mode = snapshot?.Mode is { } current ? UiText.Get(ShortModeName(current)) : null;
        string? battery = snapshot?.BatteryPercent is int percent ? UiText.Format("{0}%", percent) : null;
        Subtitle = mode is not null && battery is not null ? $"{mode} · {battery}" : mode ?? battery;
        OnChanged();
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var next = PowerOptions.CurrentMode() switch
        {
            PowerMode.BestEfficiency => PowerMode.Balanced,
            PowerMode.Balanced => PowerMode.BestPerformance,
            _ => PowerMode.BestEfficiency,
        };
        PowerOptions.SetMode(next);
        Diagnostics.Log.Info($"Power mode set: {next}");
        return Task.CompletedTask;
    }
}
