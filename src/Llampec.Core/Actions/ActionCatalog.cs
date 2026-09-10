using Llampec.Platform;
using Llampec.Settings;

namespace Llampec.Actions;

/// <summary>
/// The single place where actions are registered. To add a tile: implement <see cref="IQuickAction"/>
/// and add one line to <see cref="Create"/>. Order here is the default tile order.
/// </summary>
public static class ActionCatalog
{
    /// <param name="isDark">Reports the theme currently painted on screen. Used by <see cref="Theme.ThemeAction"/>.</param>
    /// <param name="applyTheme">Repaints and persists the theme after <see cref="Theme.ThemeAction"/> flips it.</param>
    public static IReadOnlyList<IQuickAction> Create(SystemEvents systemEvents, AppSettings settings, Func<bool> isDark, Action applyTheme)
    {
        ArgumentNullException.ThrowIfNull(systemEvents);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(isDark);
        ArgumentNullException.ThrowIfNull(applyTheme);

        var actions = new List<IQuickAction>
        {
            // Real actions are added one per development step (default tile order):
            // new Hdr.HdrAction(systemEvents),
            new DisplayOff.DisplayOffAction(systemEvents),
            new Theme.ThemeAction(settings, isDark, applyTheme),
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
