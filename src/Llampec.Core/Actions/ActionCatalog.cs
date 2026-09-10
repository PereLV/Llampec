using Llampec.Platform;

namespace Llampec.Actions;

/// <summary>
/// The single place where actions are registered. To add a tile: implement <see cref="IQuickAction"/>
/// and add one line to <see cref="Create"/>. Order here is the default tile order.
/// </summary>
public static class ActionCatalog
{
    public static IReadOnlyList<IQuickAction> Create(SystemEvents systemEvents)
    {
        ArgumentNullException.ThrowIfNull(systemEvents);

        var actions = new List<IQuickAction>
        {
            // Real actions are added one per development step (default tile order):
            // new Hdr.HdrAction(systemEvents),
            new DisplayOff.DisplayOffAction(systemEvents),
            new Theme.ThemeAction(),
            // new Projection.ProjectionAction(systemEvents),
            new Taskbar.TaskbarAutoHideAction(),
        };

#if DEBUG
        // Placeholder tiles so the split/sub-page layout can be exercised until HDR and projection exist.
        actions.Add(new Sample.SampleToggleAction("sample.toggle", "Sample toggle", "\uE945"));
        actions.Add(new Sample.SampleSubpageAction());
#endif

        return actions;
    }
}
