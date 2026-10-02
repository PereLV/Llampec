using Llampec.Actions;
using Llampec.Actions.Taskbar;
using Llampec.Interop;
using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public sealed class TaskbarAutoHideTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(Shell32.ABS_ALWAYSONTOP)]
    [InlineData(Shell32.ABS_AUTOHIDE)]
    [InlineData(Shell32.ABS_ALWAYSONTOP | Shell32.ABS_AUTOHIDE)]
    public async Task NormalTaskbarTogglesAutoHideWithoutChangingOtherFlags(uint initial)
    {
        uint flags = initial;
        var writes = new List<uint>();
        var action = new TaskbarAutoHideAction(() => TaskbarMode.Normal, () => flags,
            value => { writes.Add(value); flags = value; });
        action.Refresh();

        await action.ExecuteAsync(default);

        Assert.Equal(initial ^ Shell32.ABS_AUTOHIDE, Assert.Single(writes));
        Assert.Equal((flags & Shell32.ABS_AUTOHIDE) != 0 ? ActionState.On : ActionState.Off, action.State);
        Assert.True(action.IsAvailable);
        Assert.Null(action.Subtitle);

        await action.ExecuteAsync(default);
        Assert.Equal(initial, flags);
        Assert.Equal(2, writes.Count);
    }

    [Fact]
    public async Task IgnoredWriteDoesNotInventAChangedState()
    {
        uint? requested = null;
        var action = new TaskbarAutoHideAction(() => TaskbarMode.Normal, () => Shell32.ABS_ALWAYSONTOP,
            value => requested = value);
        action.Refresh();

        await action.ExecuteAsync(default);

        Assert.Equal(Shell32.ABS_ALWAYSONTOP | Shell32.ABS_AUTOHIDE, requested);
        Assert.Equal(ActionState.Off, action.State);
    }

    [Theory]
    [InlineData(TaskbarMode.TabletOptimized, "Tablet taskbar active")]
    [InlineData(TaskbarMode.Unknown, "Taskbar mode unavailable")]
    public async Task UnavailableModePreservesActualStateAndDoesNotWrite(TaskbarMode mode, string reason)
    {
        int writes = 0;
        var action = new TaskbarAutoHideAction(() => mode, () => Shell32.ABS_AUTOHIDE, _ => writes++);
        action.Refresh();

        await action.ExecuteAsync(default);

        Assert.False(action.IsAvailable);
        Assert.Equal(reason, action.Subtitle);
        Assert.Equal(ActionState.On, action.State);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(TaskbarMode.TabletOptimized)]
    [InlineData(TaskbarMode.Unknown)]
    public async Task ModeChangeAfterRefreshCannotWriteThroughAStaleEnabledTile(TaskbarMode nextMode)
    {
        TaskbarMode mode = TaskbarMode.Normal;
        int writes = 0;
        var action = new TaskbarAutoHideAction(() => mode, () => 0, _ => writes++);
        action.Refresh();
        Assert.True(action.IsAvailable);
        mode = nextMode;

        await action.ExecuteAsync(default);

        Assert.Equal(0, writes);
        Assert.False(action.IsAvailable);
        Assert.Equal(ActionState.Off, action.State);
    }

    [Fact]
    public async Task ReturningToNormalRestoresAvailabilityAndUsesTheCurrentFlags()
    {
        TaskbarMode mode = TaskbarMode.TabletOptimized;
        uint flags = 0;
        var action = new TaskbarAutoHideAction(() => mode, () => flags, value => flags = value);
        action.Refresh();
        Assert.False(action.IsAvailable);

        mode = TaskbarMode.Normal;
        action.Refresh();
        Assert.True(action.IsAvailable);
        Assert.Null(action.Subtitle);
        // Another application changes the native preference after our last refresh.
        flags = Shell32.ABS_AUTOHIDE | Shell32.ABS_ALWAYSONTOP;
        await action.ExecuteAsync(default);

        Assert.Equal(Shell32.ABS_ALWAYSONTOP, flags);
        Assert.Equal(ActionState.Off, action.State);
    }
}
