using Llampec.Actions;
using Llampec.Actions.Taskbar;
using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public sealed class TouchTaskbarTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreferenceRemainsWritableWithTheKeyboardAttached(bool enabled)
    {
        var status = TabletTaskbar.ClassifyPreference(false, true, 8, enabled ? 1 : 0, false, convertibilityEnabled: 1);

        Assert.Equal(enabled, status.Enabled);
        Assert.True(status.IsAvailable);
        Assert.Equal(TaskbarMode.Normal, status.Mode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TileDistinguishesEnabledPreferenceFromEffectivePosture(bool slate)
    {
        var status = TabletTaskbar.ClassifyPreference(slate, true, 8, 1, false, convertibilityEnabled: 1);
        var action = NewAction(() => status);

        action.Refresh();

        Assert.Equal(ActionState.On, action.State);
        Assert.True(action.IsAvailable);
        Assert.Equal(slate ? "Tablet taskbar active" : "On in tablet posture", action.Subtitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData("1")]
    [InlineData(1L)]
    public void MissingOrMalformedPreferenceIsNotInvented(object? value)
    {
        var status = TabletTaskbar.ClassifyPreference(true, true, 8, value, false, convertibilityEnabled: 1);
        var action = NewAction(() => status);

        action.Refresh();

        Assert.Null(status.Enabled);
        Assert.False(action.IsAvailable);
        Assert.Equal(ActionState.None, action.State);
        Assert.Equal("Touch taskbar unavailable", action.Subtitle);
    }

    [Theory]
    [InlineData(true, false, 8, 0)] // OEM opt-out
    [InlineData(false, false, 8, 1)] // No integrated touch
    [InlineData(true, false, 1, null)] // Touch desktop, no OEM opt-in
    [InlineData(true, true, 8, 1)] // Unvalidated override
    public void IneligibleOrOverriddenHardwareCannotChangeThePreference(bool touch, bool hasOverride,
        int role, object? convertibility)
    {
        var status = TabletTaskbar.ClassifyPreference(true, touch, role, 1, hasOverride, convertibilityEnabled: convertibility);
        Assert.False(status.IsAvailable);
        Assert.True(status.Enabled); // Keep the stored preference visible without claiming capability.
    }

    [Fact]
    public void RemoteSessionCannotChangeTheLocalTabletPreference()
    {
        var status = TabletTaskbar.ClassifyPreference(true, true, 8, 1, false, remoteSession: true, convertibilityEnabled: 1);
        Assert.False(status.IsAvailable);
        Assert.Equal("Touch taskbar unavailable", status.UnavailableReason);
    }

    [Fact]
    public void ExplicitOemOptInEnablesPreferenceRegardlessOfFallbackRole()
    {
        var status = TabletTaskbar.ClassifyPreference(false, true, 1, 0, false, convertibilityEnabled: -1);
        Assert.True(status.IsAvailable);
        Assert.False(status.Enabled);
        Assert.Equal(TaskbarMode.Normal, status.Mode);
    }

    [Fact]
    public async Task EachTapTogglesTheCurrentSystemPreferenceAndRefreshes()
    {
        bool enabled = true;
        var writes = new List<bool>();
        var action = new TouchTaskbarAction(() => Ready(enabled), (value, _) =>
        {
            writes.Add(value);
            enabled = value;
            return Task.FromResult(new TouchTaskbarChangeResult(true));
        });
        action.Refresh();

        await action.ExecuteAsync(default);
        Assert.Equal(ActionState.Off, action.State);
        Assert.Null(action.Subtitle);
        await action.ExecuteAsync(default);
        Assert.Equal(ActionState.On, action.State);
        Assert.Equal(new[] { false, true }, writes);
    }

    [Fact]
    public async Task ExternalPreferenceChangeAfterRefreshIsReadBeforeToggling()
    {
        bool enabled = true;
        bool? requested = null;
        var action = new TouchTaskbarAction(() => Ready(enabled), (value, _) =>
        {
            requested = value;
            enabled = value;
            return Task.FromResult(new TouchTaskbarChangeResult(true));
        });
        action.Refresh();
        enabled = false;

        await action.ExecuteAsync(default);

        Assert.True(requested);
        Assert.Equal(ActionState.On, action.State);
    }

    [Fact]
    public async Task HardwareChangeAfterRefreshCannotWriteThroughStaleTile()
    {
        var status = Ready(true);
        int writes = 0;
        var action = new TouchTaskbarAction(() => status, (_, _) =>
        {
            writes++;
            return Task.FromResult(new TouchTaskbarChangeResult(true));
        });
        action.Refresh();
        status = status with { IsAvailable = false, UnavailableReason = "Touch taskbar unavailable" };

        await action.ExecuteAsync(default);

        Assert.Equal(0, writes);
        Assert.False(action.IsAvailable);
        Assert.Equal(ActionState.On, action.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrIgnoredWriteDoesNotInventSuccess(bool reportedSuccess)
    {
        var action = new TouchTaskbarAction(() => Ready(true), (_, _) =>
            Task.FromResult(new TouchTaskbarChangeResult(reportedSuccess)));
        action.Refresh();

        await action.ExecuteAsync(default);
        action.Refresh(); // QuickActionBase and system event refreshes must not immediately erase errors.

        Assert.Equal(ActionState.On, action.State);
        Assert.Equal("Could not change touch taskbar.", action.Subtitle);
    }

    [Fact]
    public async Task WarningPreservesReadBackStateAndDisappearsAfterExternalChange()
    {
        bool enabled = true;
        var action = new TouchTaskbarAction(() => Ready(enabled), (value, _) =>
        {
            enabled = value;
            return Task.FromResult(new TouchTaskbarChangeResult(true, "Touch taskbar saved; Windows refresh unconfirmed."));
        });
        action.Refresh();

        await action.ExecuteAsync(default);
        Assert.Equal(ActionState.Off, action.State);
        Assert.Equal("Touch taskbar saved; Windows refresh unconfirmed.", action.Subtitle);
        enabled = true;
        action.Refresh();

        Assert.Equal("Tablet taskbar active", action.Subtitle);
    }

    [Fact]
    public async Task NativeExceptionIsVisibleAndAllowsRetry()
    {
        bool enabled = true;
        int attempts = 0;
        var action = new TouchTaskbarAction(() => Ready(enabled), (value, _) =>
        {
            if (attempts++ == 0) throw new UnauthorizedAccessException();
            enabled = value;
            return Task.FromResult(new TouchTaskbarChangeResult(true));
        });
        action.Refresh();

        await action.ExecuteAsync(default);
        Assert.Equal("Could not change touch taskbar.", action.Subtitle);
        Assert.True(action.IsAvailable);
        await action.ExecuteAsync(default);

        Assert.Equal(ActionState.Off, action.State);
        Assert.Null(action.Subtitle);
    }

    [Fact]
    public async Task CancelledOperationDoesNotWrite()
    {
        int writes = 0;
        var action = new TouchTaskbarAction(() => Ready(true), (_, _) =>
        {
            writes++;
            return Task.FromResult(new TouchTaskbarChangeResult(true));
        });
        action.Refresh();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.ExecuteAsync(cancellation.Token));

        Assert.Equal(0, writes);
        Assert.False(action.IsBusy);
        Assert.Equal(ActionState.On, action.State);
    }

    private static TouchTaskbarStatus Ready(bool enabled) => new(enabled, true,
        enabled ? TaskbarMode.TabletOptimized : TaskbarMode.Normal);

    private static TouchTaskbarAction NewAction(Func<TouchTaskbarStatus> read) => new(read,
        (_, _) => throw new InvalidOperationException("This test must not write native settings."));
}
