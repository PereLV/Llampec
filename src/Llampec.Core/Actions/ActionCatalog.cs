using Llampec.Platform;

namespace Llampec.Actions;

/// <summary>
/// The single place where actions are registered. To add a tile: implement <see cref="IQuickAction"/>
/// and add one line to <see cref="Create"/>. Order here is the default tile order.
/// </summary>
public static class ActionCatalog
{
    public static IReadOnlyList<IQuickAction> Create(SystemEvents systemEvents, Caffeine.CaffeineAction caffeine,
        AlwaysOnTop.AlwaysOnTopAction? alwaysOnTop = null)
    {
        ArgumentNullException.ThrowIfNull(systemEvents);

        List<IQuickAction> actions =
        [
            new Hdr.HdrAction(),
            new DisplayOff.DisplayOffAction(systemEvents),
            new Theme.ThemeAction(),
            new Projection.ProjectionAction(),
            new Taskbar.TaskbarAutoHideAction(),
            caffeine,
        ];
        if (alwaysOnTop is not null) actions.Add(alwaysOnTop);
        actions.Add(new Screenshot.ScreenshotAction());
        return actions;
    }
}
