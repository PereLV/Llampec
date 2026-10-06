using Llampec.Platform;

namespace Llampec.Actions;

/// <summary>
/// The single place where actions are registered. To add a tile: implement <see cref="IQuickAction"/>
/// and add one line to <see cref="Create"/>. Order here is the default tile order.
/// </summary>
public static class ActionCatalog
{
    public static IReadOnlyList<IQuickAction> Create(SystemEvents systemEvents, Caffeine.CaffeineAction caffeine,
        AlwaysOnTop.AlwaysOnTopAction? alwaysOnTop = null, Rotation.RotationAction? rotation = null,
        Fullscreen.FullscreenAction? fullscreen = null)
    {
        ArgumentNullException.ThrowIfNull(systemEvents);

        List<IQuickAction> actions =
        [
            new Hdr.HdrAction(),
            new DisplayOff.DisplayOffAction(systemEvents),
            new Theme.ThemeAction(),
            new Projection.ProjectionAction(),
            new Taskbar.TaskbarAutoHideAction(),
            new Taskbar.TouchTaskbarAction(),
            caffeine,
        ];
        if (alwaysOnTop is not null) actions.Add(alwaysOnTop);
        if (rotation is not null) actions.Add(rotation);
        actions.Add(new Screenshot.ScreenshotAction());
        if (fullscreen is not null) actions.Add(fullscreen);
        return actions;
    }
}
