using Llampec.Actions;
using Llampec.Actions.DisplayOff;
using Llampec.Actions.Taskbar;
using Llampec.Actions.Theme;
using Llampec.Interop;
using Llampec.Platform;
using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public class ActionCatalogTests
{
    [Fact]
    public void Catalog_ids_are_unique_and_glyphs_are_single_code_points()
    {
        using var events = new SystemEvents();
        var actions = ActionCatalog.Create(events, new AppSettings(), () => false, () => { });

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

            Assert.Equal(action.Kind == ActionKind.ToggleWithSubpage, action.SubActions.Count > 0);
        }
    }

    [Fact]
    public void Catalog_contains_display_off_and_taskbar_actions()
    {
        using var events = new SystemEvents();
        var actions = ActionCatalog.Create(events, new AppSettings(), () => false, () => { });

        var displayOff = Assert.Single(actions.OfType<DisplayOffAction>());
        Assert.Equal(ActionKind.Button, displayOff.Kind);
        Assert.Equal(ActionState.None, displayOff.State);

        var taskbar = Assert.Single(actions.OfType<TaskbarAutoHideAction>());
        Assert.Equal(ActionKind.Toggle, taskbar.Kind);
    }

    [Fact]
    public async Task Theme_action_toggles_via_delegates_without_touching_real_settings()
    {
        // isDark/applyTheme stand in for ThemeManager here, so this never touches SettingsStore
        // (see TaskbarAutoHideAction's test for the same rationale re: real user state).
        using var events = new SystemEvents();
        var settings = new AppSettings();
        bool dark = false;
        int applyCount = 0;
        var actions = ActionCatalog.Create(events, settings, () => dark, () => applyCount++);

        var theme = Assert.Single(actions.OfType<ThemeAction>());
        Assert.Equal(ActionKind.Toggle, theme.Kind);

        theme.Refresh();
        Assert.Equal(ActionState.Off, theme.State);

        await theme.ExecuteAsync(CancellationToken.None);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(1, applyCount);
    }

    [Fact]
    public void Taskbar_state_is_readable()
    {
        // Only asserts that the shell answers; never changes the user's taskbar from a test.
        uint state = TaskbarAutoHideAction.GetState();
        Assert.Equal(0u, state & ~(Shell32.ABS_AUTOHIDE | Shell32.ABS_ALWAYSONTOP));
    }
}
