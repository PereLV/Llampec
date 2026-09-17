using Llampec.Platform;

namespace Llampec.Actions.Hdr;

/// <summary>
/// "HDR": a split tile whose primary switch turns HDR on/off for every HDR-capable display at once, and
/// whose sub-page (<see cref="SubActions"/>) lists each connected display so it can be switched on its
/// own -- the thing the native Windows 11 Quick Settings panel still doesn't offer for multi-monitor setups.
/// Built through <see cref="HdrDisplays"/>, which talks to the Windows 11 24H2 DisplayConfig HDR API.
///
/// The monitor list is captured once, from the displays active when this tile is constructed (at app
/// startup). A monitor plugged in afterwards won't appear until Llampec restarts -- <see cref="SystemEvents"/>
/// only tells us *that* the topology changed, and rebuilding <see cref="SubActions"/> live would leave the
/// already-open panel's sub-page bound to stale <see cref="IQuickAction"/> instances (see
/// <c>FlyoutViewModel</c>/<c>TileViewModel</c>, which snapshot the tile list once at startup). Each
/// individual monitor tile still re-reads its own real state on every <see cref="Refresh"/>, and quietly
/// greys itself out if unplugged.
/// </summary>
public sealed class HdrAction : QuickActionBase
{
    private readonly List<HdrMonitorAction> _monitors;

    public HdrAction()
    {
        _monitors = [.. HdrDisplays.GetDisplays().Select(d => new HdrMonitorAction(d))];
        foreach (var monitor in _monitors)
        {
            monitor.Changed += (_, _) => UpdateAggregate();
        }

        UpdateAggregate();
    }

    public override string Id => "hdr";
    public override string Title => "HDR";
    public override string Glyph => ""; // Contrast
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;
    public override IReadOnlyList<IQuickAction> SubActions => _monitors;

    public override void Refresh() => UpdateAggregate();

    protected override async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        bool turnOn = State != ActionState.On;
        foreach (var monitor in _monitors)
        {
            if (monitor.IsAvailable && (monitor.State == ActionState.On) != turnOn)
            {
                await monitor.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void UpdateAggregate()
    {
        var capable = _monitors.Where(m => m.IsAvailable).ToList();
        IsAvailable = capable.Count > 0;

        int on = capable.Count(m => m.State == ActionState.On);
        State = on == 0 ? ActionState.Off : on == capable.Count ? ActionState.On : ActionState.Mixed;
        Subtitle = capable.Count switch
        {
            0 => "Not supported",
            _ => State switch
            {
                ActionState.On => "All on",
                ActionState.Mixed => $"{on} of {capable.Count}",
                _ => "Off",
            },
        };
    }
}
