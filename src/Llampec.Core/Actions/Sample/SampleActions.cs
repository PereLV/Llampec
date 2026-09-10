#if DEBUG
namespace Llampec.Actions.Sample;

/// <summary>Debug-only tiles used to exercise the panel before real actions exist.</summary>
internal sealed class SampleToggleAction(string id, string title, string glyph) : QuickActionBase
{
    public override string Id => id;
    public override string Title => title;
    public override string Glyph => glyph;
    public override ActionKind Kind => ActionKind.Toggle;

    protected override async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken).ConfigureAwait(false); // simulate a slow driver call
        State = State == ActionState.On ? ActionState.Off : ActionState.On;
    }
}

internal sealed class SampleSubpageAction : QuickActionBase
{
    private readonly List<IQuickAction> _subActions =
    [
        new SampleToggleAction("sample.sub.1", "Monitor 1", "\uE7F8"), // TVMonitor
        new SampleToggleAction("sample.sub.2", "Monitor 2", "\uE7F8"),
    ];

    public SampleSubpageAction()
    {
        foreach (var sub in _subActions)
        {
            sub.Changed += (_, _) => UpdateAggregate();
        }

        UpdateAggregate();
    }

    public override string Id => "sample.subpage";
    public override string Title => "Sample split";
    public override string Glyph => "\uE7F4"; // Brightness (HDR placeholder)
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;
    public override IReadOnlyList<IQuickAction> SubActions => _subActions;

    public override void Refresh() => UpdateAggregate();

    private void UpdateAggregate()
    {
        int on = _subActions.Count(a => a.State == ActionState.On);
        State = on == 0 ? ActionState.Off : on == _subActions.Count ? ActionState.On : ActionState.Mixed;
        Subtitle = State switch
        {
            ActionState.On => "All on",
            ActionState.Mixed => $"{on} of {_subActions.Count}",
            _ => "Off",
        };
    }

    protected override async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        bool turnOn = State != ActionState.On;
        foreach (var sub in _subActions)
        {
            if ((sub.State == ActionState.On) != turnOn)
            {
                await sub.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
#endif
