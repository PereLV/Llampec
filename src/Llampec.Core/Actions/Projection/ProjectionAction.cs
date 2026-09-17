using Llampec.Platform;

namespace Llampec.Actions.Projection;

/// <summary>
/// "Multiple displays": the Win+P menu (Settings &gt; System &gt; Display's "Multiple displays" section) as a
/// tile. The sub-page lists the four fixed topologies Windows itself offers (PC screen only / Duplicate /
/// Extend / Second screen only); tapping the tile body cycles to the next one, the same as pressing the
/// physical Win+P key repeatedly without opening its own flyout. Built on <see cref="ProjectionModes"/>,
/// which calls the exact same SetDisplayConfig/QueryDisplayConfig the OS menu does -- no attempt to
/// reimplement topology switching per-monitor.
/// </summary>
public sealed class ProjectionAction : QuickActionBase
{
    private readonly List<ProjectionModeAction> _modes;
    private bool _syncing;

    public ProjectionAction()
    {
        _modes =
        [
            new ProjectionModeAction(ProjectionTopology.Internal, "PC screen only"),
            new ProjectionModeAction(ProjectionTopology.Clone, "Duplicate"),
            new ProjectionModeAction(ProjectionTopology.Extend, "Extend"),
            new ProjectionModeAction(ProjectionTopology.External, "Second screen only"),
        ];

        foreach (var mode in _modes)
        {
            // Any mode's tap needs every sibling re-read too (radio-button semantics: exactly one is On),
            // not just its own tile -- QuickActionBase only re-reads the tile that was actually executed.
            mode.Changed += (_, _) => OnModeChanged();
        }

        RefreshModes();
    }

    public override string Id => "projection";
    public override string Title => "Multiple displays";
    public override string Glyph => ""; // Project
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;
    public override IReadOnlyList<IQuickAction> SubActions => _modes;
    public override bool SubActionsAreExclusive => true;

    public override void Refresh() => RefreshModes();

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        int index = _modes.FindIndex(m => m.State == ActionState.On);
        return _modes[(index + 1) % _modes.Count].ExecuteAsync(cancellationToken);
    }

    private void OnModeChanged()
    {
        if (!_syncing)
        {
            RefreshModes();
        }
    }

    private void RefreshModes()
    {
        _syncing = true;
        try
        {
            foreach (var mode in _modes)
            {
                mode.Refresh();
            }
        }
        finally
        {
            _syncing = false;
        }

        var active = _modes.Find(m => m.State == ActionState.On);
        State = active is not null ? ActionState.On : ActionState.Off;
        Subtitle = active?.Title;
    }
}
