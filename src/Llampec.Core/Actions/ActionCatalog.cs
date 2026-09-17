using Llampec.Platform;

namespace Llampec.Actions;

/// <summary>
/// The single place where actions are registered. To add a tile: implement <see cref="IQuickAction"/>
/// and add one line to <see cref="Create"/>. Order here is the default tile order.
/// </summary>
public static class ActionCatalog
{
    public static IReadOnlyList<IQuickAction> Create(SystemEvents systemEvents, Caffeine.CaffeineAction caffeine)
    {
        ArgumentNullException.ThrowIfNull(systemEvents);

        return
        [
            // Real actions are added one per development step (default tile order):
            new Hdr.HdrAction(),
            new DisplayOff.DisplayOffAction(systemEvents),
            new Theme.ThemeAction(),
            new Projection.ProjectionAction(),
            new Taskbar.TaskbarAutoHideAction(),
            caffeine,
        ];
    }
}
