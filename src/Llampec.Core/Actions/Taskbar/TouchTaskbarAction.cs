using Llampec.Platform;

namespace Llampec.Actions.Taskbar;

/// <summary>The touch-optimization preference; enabling it does not force tablet posture.</summary>
public sealed class TouchTaskbarAction : QuickActionBase
{
    private readonly Func<TouchTaskbarStatus> _read;
    private readonly Func<bool, CancellationToken, Task<TouchTaskbarChangeResult>> _setEnabled;
    private string? _message;
    private TouchTaskbarStatus? _messageState;

    public TouchTaskbarAction() : this(TabletTaskbar.ReadPreference, TabletTaskbar.SetEnabledAsync) { }

    public TouchTaskbarAction(Func<TouchTaskbarStatus> read,
        Func<bool, CancellationToken, Task<TouchTaskbarChangeResult>> setEnabled)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
    }

    public override string Id => "touch-taskbar";
    public override string Title => "Touch taskbar";
    public override string Glyph => "\uEBFC"; // TabletMode
    public override ActionKind Kind => ActionKind.Toggle;

    public override void Refresh()
    {
        var status = _read();
        if (_messageState is not null && status != _messageState)
        {
            _message = null;
            _messageState = null;
        }
        Apply(status);
    }

    protected override async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _message = null;
        _messageState = null;
        // The posture, setting, or capability may have changed after the last UI refresh.
        var before = _read();
        Apply(before);
        if (!IsAvailable || before.Enabled is null) return;

        try
        {
            var result = await _setEnabled(!before.Enabled.Value, cancellationToken).ConfigureAwait(false);
            var after = _read();
            _message = result.Message ?? (!result.Succeeded || after.Enabled != !before.Enabled.Value
                ? "Could not change touch taskbar." : null);
            _messageState = _message is null ? null : after;
            Apply(after);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log.Warn($"Touch taskbar failed: {ex.Message}");
            _message = "Could not change touch taskbar.";
            _messageState = _read();
            Apply(_messageState);
        }
    }

    private void Apply(TouchTaskbarStatus status)
    {
        State = status.Enabled switch { true => ActionState.On, false => ActionState.Off, _ => ActionState.None };
        IsAvailable = status.IsAvailable && status.Enabled is not null;
        Subtitle = _message ?? (!IsAvailable ? status.UnavailableReason ?? "Touch taskbar unavailable"
            : status.Enabled == true ? status.Mode == TaskbarMode.TabletOptimized ? "Tablet taskbar active" : "On in tablet posture"
            : null);
    }
}
