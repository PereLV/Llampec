using Llampec.Settings;

namespace Llampec.Actions.Fullscreen;

/// <summary>The state belongs to the app lifetime, not to the disposable panel tile.</summary>
public sealed class FullscreenAction : QuickActionBase, IDisposable
{
    private readonly FullscreenService _service;
    public override string Id => "fullscreen";
    public override string Title => "Full screen";
    public override string Glyph => "\uE740";
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;

    public FullscreenAction(FullscreenService service)
    {
        _service = service;
        _service.Changed += OnServiceChanged;
        Refresh();
    }

    private void OnServiceChanged(object? sender, EventArgs e) => ReadState();
    public override void Refresh() { _service.Refresh(); ReadState(); }
    private void ReadState()
    {
        State = _service.IsActive ? ActionState.On : ActionState.Off;
        Subtitle = _service.Error is { } error ? UiText.Get(error)
            : _service.IsActive ? UiText.Format("Active on {0}: {1}", _service.ActiveMonitor, _service.ActiveTitle)
            : _service.HasTarget ? UiText.Format("Fullscreen: {0}", _service.TargetTitle)
            : UiText.Get("No eligible window is active.");
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _service.ToggleTarget();
        return Task.CompletedTask;
    }

    public void Dispose() => _service.Changed -= OnServiceChanged;
}
