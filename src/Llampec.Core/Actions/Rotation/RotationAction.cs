using Llampec.Platform;

namespace Llampec.Actions.Rotation;

/// <summary>The primary tile controls the integrated display's system rotation lock.</summary>
public sealed class RotationAction : QuickActionBase, IDisposable
{
    public DisplayOrientationService Service { get; }
    public RotationAction(DisplayOrientationService service)
    {
        Service = service;
        Service.Changed += OnServiceChanged;
        Synchronize();
    }

    public override string Id => "rotation";
    public override string Title => "Rotation lock";
    public override string Glyph => "\uE755";
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;
    public override void Refresh() => Service.Refresh();
    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken) => Service.ToggleLockAsync(cancellationToken);
    private void OnServiceChanged(object? sender, EventArgs e) => Synchronize();
    private void Synchronize()
    {
        var rotation = Service.Snapshot.Rotation;
        State = rotation.IsKnown ? rotation.IsLocked ? ActionState.On : ActionState.Off : ActionState.None;
        IsAvailable = rotation.CanToggle && !Service.IsBusy && !Service.RecoveryPending;
        string? error = Service.MessageKey is "Orientation applied" or "Previous orientation restored" ? null : Service.MessageKey;
        Subtitle = Service.RecoveryPending ? "Restore the previous orientation" : error ?? rotation.ReasonKeys[0];
        OnChanged();
    }
    public void Dispose() => Service.Changed -= OnServiceChanged;
}
