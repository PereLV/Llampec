using Llampec.Platform;

namespace Llampec.Actions.Projection;

/// <summary>
/// One of the four fixed display topologies (see <see cref="ProjectionTopology"/>). A sub-tile of
/// <see cref="ProjectionAction"/>; never registered in <see cref="ActionCatalog"/> directly. Tapping it
/// always applies its own topology -- it behaves like a radio button, not a plain on/off switch.
/// </summary>
public sealed class ProjectionModeAction : QuickActionBase
{
    private readonly string _title;

    public ProjectionModeAction(ProjectionTopology topology, string title)
    {
        Topology = topology;
        _title = title;
    }

    public ProjectionTopology Topology { get; }

    public override string Id => $"projection.{Topology}";
    public override string Title => _title;
    public override string Glyph => ""; // TVMonitor
    public override ActionKind Kind => ActionKind.Toggle;

    public override void Refresh()
    {
        State = ProjectionModes.GetCurrent() == Topology ? ActionState.On : ActionState.Off;
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        ProjectionModes.Set(Topology);
        return Task.CompletedTask;
    }
}
