using Llampec.Actions;
using Llampec.Actions.DisplayOff;
using Llampec.Actions.Hdr;
using Llampec.Actions.Projection;
using Llampec.Actions.Taskbar;
using Llampec.Actions.Theme;
using Llampec.Interop;
using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public class ActionCatalogTests
{
    [Fact]
    public void Catalog_ids_are_unique_and_glyphs_are_single_code_points()
    {
        using var events = new SystemEvents();
        using var caffeine = new Llampec.Actions.Caffeine.CaffeineAction(() => new());
        var actions = ActionCatalog.Create(events, caffeine);

        Assert.NotEmpty(actions);
        Assert.Equal(actions.Count, actions.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var action in actions.Concat(actions.SelectMany(a => a.SubActions)))
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Id), $"{action.GetType().Name} has no id");
            Assert.False(string.IsNullOrWhiteSpace(action.Title), $"{action.Id} has no title");
            Assert.Equal(1, action.Glyph.Length);
            Assert.InRange(action.Glyph[0], '\uE000', '\uF8FF'); // Segoe Fluent Icons private use area
            if (action.GlyphBadge is not null)
            {
                Assert.Equal(1, action.GlyphBadge.Length);
                Assert.InRange(action.GlyphBadge[0], '\uE000', '\uF8FF');
            }

            if (action.SubActions.Count > 0) Assert.Equal(ActionKind.ToggleWithSubpage, action.Kind);
        }
    }

    [Fact]
    public void Catalog_contains_display_off_and_taskbar_actions()
    {
        using var events = new SystemEvents();
        using var caffeine = new Llampec.Actions.Caffeine.CaffeineAction(() => new());
        var actions = ActionCatalog.Create(events, caffeine);

        var displayOff = Assert.Single(actions.OfType<DisplayOffAction>());
        Assert.Equal(ActionKind.Button, displayOff.Kind);
        Assert.Equal(ActionState.None, displayOff.State);

        var taskbar = Assert.Single(actions.OfType<TaskbarAutoHideAction>());
        Assert.Equal(ActionKind.Toggle, taskbar.Kind);
    }

    [Fact]
    public void Theme_state_matches_the_real_system_theme()
    {
        // Only asserts that the tile's state matches the registry, the same way Taskbar_state_is_readable
        // checks the shell -- never calls ExecuteAsync, which would really flip the developer's Windows
        // theme (dark mode is a system-wide setting, not something scoped to a test process).
        using var events = new SystemEvents();
        using var caffeine = new Llampec.Actions.Caffeine.CaffeineAction(() => new());
        var actions = ActionCatalog.Create(events, caffeine);

        var theme = Assert.Single(actions.OfType<ThemeAction>());
        Assert.Equal(ActionKind.Toggle, theme.Kind);

        theme.Refresh();
        bool expectedOn = !SystemTheme.IsAppsLightTheme();
        Assert.Equal(expectedOn ? ActionState.On : ActionState.Off, theme.State);
    }

    [Fact]
    public void Taskbar_state_is_readable()
    {
        // Only asserts that the shell answers; never changes the user's taskbar from a test.
        uint state = TaskbarAutoHideAction.GetState();
        Assert.Equal(0u, state & ~(Shell32.ABS_AUTOHIDE | Shell32.ABS_ALWAYSONTOP));
    }

    [Fact]
    public void Hdr_subpage_has_one_tile_per_active_display()
    {
        // Read-only: builds the tile the same way the app does and checks it matches the real display
        // topology; never calls ExecuteAsync, which would really flip HDR on a monitor.
        using var events = new SystemEvents();
        using var caffeine = new Llampec.Actions.Caffeine.CaffeineAction(() => new());
        var actions = ActionCatalog.Create(events, caffeine);

        var hdr = Assert.Single(actions.OfType<HdrAction>());
        Assert.Equal(ActionKind.ToggleWithSubpage, hdr.Kind);

        var displays = HdrDisplays.GetDisplays();
        Assert.Equal(displays.Count, hdr.SubActions.Count);
        Assert.Equal(displays.Any(d => d.Supported), hdr.IsAvailable);
    }

    [Fact]
    public void Projection_subpage_has_exactly_one_active_mode()
    {
        // Read-only: never calls ExecuteAsync, which would really switch the developer's display topology.
        using var events = new SystemEvents();
        using var caffeine = new Llampec.Actions.Caffeine.CaffeineAction(() => new());
        var actions = ActionCatalog.Create(events, caffeine);

        var projection = Assert.Single(actions.OfType<ProjectionAction>());
        Assert.Equal(ActionKind.ToggleWithSubpage, projection.Kind);
        Assert.Equal(4, projection.SubActions.Count);
        Assert.Equal(ActionState.On, projection.State);
        Assert.Single(projection.SubActions, a => a.State == ActionState.On);
    }
}
