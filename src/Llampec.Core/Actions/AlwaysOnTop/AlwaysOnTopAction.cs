using Llampec.Settings;

namespace Llampec.Actions.AlwaysOnTop;

/// <summary>The tile represents its target window; the separate count belongs to all session pins.</summary>
public sealed class AlwaysOnTopAction : QuickActionBase, IDisposable
{
    private readonly AlwaysOnTopService _service;
    private int _pinnedCount;
    public override string Id => "always-on-top";
    public override string Title => "Always on Top";
    public override string Glyph => "\uE718";
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;
    public int PinnedCount => _pinnedCount;

    public AlwaysOnTopAction(AlwaysOnTopService service)
    {
        _service = service;
        _service.Changed += OnServiceChanged;
        Refresh();
    }

    private void OnServiceChanged(object? sender, EventArgs e) => ReadServiceState();

    public override void Refresh()
    {
        _service.Refresh();
        ReadServiceState();
    }

    private void ReadServiceState()
    {
        Set(ref _pinnedCount, _service.PinnedCount);
        State = _service.IsTargetPinned ? ActionState.On : ActionState.Off;
        Subtitle = _service.Error is { } error ? UiText.Get(error)
            : !_service.HasTarget ? UiText.Get("Choose a window")
            : UiText.Format(_service.IsTargetPinned ? "Unpin: {0}" : "Pin: {0}", _service.TargetTitle);
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _service.ToggleTarget();
        return Task.CompletedTask;
    }

    public void Dispose() => _service.Changed -= OnServiceChanged;
}
