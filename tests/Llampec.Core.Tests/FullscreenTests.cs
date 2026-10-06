using Llampec.Actions;
using Llampec.Actions.Fullscreen;
using Xunit;

namespace Llampec.Tests;

public sealed class FullscreenTests
{
    private sealed class WindowSystem : IFullscreenWindowSystem
    {
        public event Action? WindowChanged;
        public nint ForegroundWindow { get; set; } = 1;
        public Dictionary<nint, FullscreenWindowSnapshot> Items = [];
        public Dictionary<nint, nint> Owners = [];
        public Session? Session;
        public bool FailEnter, FailRollback, Disposed;
        public int SnapshotReads;
        public WindowSystem() { Add(1, "Alpha"); Add(2, "Bravo"); Add(3, "Shell", shell: true); Add(4, "Llampec", own: true); }
        public void Add(nint handle, string title, bool own = false, bool shell = false, uint process = 42, uint thread = 24)
            => Items[handle] = new(new(handle, process, thread), title, !own && !shell, own, shell);
        public FullscreenWindowSnapshot? Read(nint handle) { SnapshotReads++; return Items.GetValueOrDefault(handle); }
        public FullscreenWindowSnapshot? ResolveForeground(nint handle) => Read(Owners.GetValueOrDefault(handle, handle));
        public IFullscreenSession Enter(FullscreenWindowIdentity identity)
        {
            Session = new(this, identity);
            if (FailRollback) throw new FullscreenActivationRecoveryException(Session, new InvalidOperationException("Partial failure"));
            if (FailEnter) throw new InvalidOperationException("Failed to enter");
            return Session;
        }
        public void Foreground(nint handle) { ForegroundWindow = handle; WindowChanged?.Invoke(); }
        public bool Tracking;
        public void SetWindowTracking(bool enabled) => Tracking = enabled;
        public void Dispose() => Disposed = true;
    }

    private sealed class Session(WindowSystem windows, FullscreenWindowIdentity identity) : IFullscreenSession
    {
        public FullscreenWindowIdentity Identity { get; } = identity;
        public string MonitorDescription => "Screen 2";
        public string MonitorDevice => @"\\.\DISPLAY2";
        public bool Claimed = true;
        public bool DisplayUnchanged { get; set; } = true;
        public bool OwnsWindow => Claimed && windows.Items.GetValueOrDefault(Identity.Handle)?.Identity == Identity;
        public bool IsVisible => windows.Items.GetValueOrDefault(Identity.Handle)?.IsVisible == true;
        public bool IsMinimized => windows.Items.GetValueOrDefault(Identity.Handle)?.IsMinimized == true;
        public bool IsCloaked { get; set; }
        public int PointerUpdates, Restores, Reapplies;
        public bool FailRestoreOnce;
        public FullscreenForegroundDisposition ClassifyForeground(nint foreground)
        {
            if (foreground == Identity.Handle) return FullscreenForegroundDisposition.Target;
            if (windows.Owners.GetValueOrDefault(foreground) == Identity.Handle) return FullscreenForegroundDisposition.Preserve;
            return windows.Read(foreground) is { IsEligible: true }
                ? FullscreenForegroundDisposition.DifferentWindow : FullscreenForegroundDisposition.Preserve;
        }
        public void UpdatePointer(long now) => PointerUpdates++;
        public void ReapplyShellPolicy() => Reapplies++;
        public void RevealTaskbar() { }
        public void CoverTaskbar() { }
        public void Restore()
        {
            Restores++;
            if (FailRestoreOnce) { FailRestoreOnce = false; throw new InvalidOperationException("Failed to restore"); }
            Claimed = false;
        }
    }

    [Fact]
    public void WindowEventsAreTrackedOnlyWhilePanelIsShownOrSessionActive()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        Assert.False(windows.Tracking);
        service.CaptureTarget(); Assert.True(windows.Tracking);
        service.ToggleTarget(); service.ReleaseTarget();
        Assert.True(windows.Tracking);
        service.ToggleTarget(); Assert.False(service.IsActive);
        Assert.False(windows.Tracking);
    }

    [Fact]
    public void CapturesBeforePanelAndFreezesTargetAcrossShellAndOwnApp()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        windows.Foreground(3); service.CaptureTarget(); windows.Foreground(4);
        Assert.Equal((nint)1, service.TargetWindow);
        windows.Foreground(2); Assert.Equal((nint)1, service.TargetWindow);
        service.ToggleTarget(); Assert.Equal((nint)1, service.ActiveWindow);
        service.ReleaseTarget(); Assert.Equal((nint)2, service.TargetWindow);
    }

    [Fact]
    public void ShortcutIgnoresShellButResolvesDialogAndRememberedPanelTarget()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        windows.Foreground(3); service.ToggleForeground();
        Assert.False(service.IsActive); Assert.Equal("No eligible window is active.", service.Error);
        windows.Add(5, "Dialog"); windows.Owners[5] = 1;
        windows.Foreground(5); service.ToggleForeground(); Assert.Equal((nint)1, service.ActiveWindow);
        service.ToggleForeground(); service.CaptureTarget(); windows.Foreground(4);
        service.ToggleForeground(); Assert.Equal((nint)1, service.ActiveWindow);
    }

    [Fact]
    public void OtherWindowIncludingSameProcessRestoresAfterDebounce()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget(); var session = windows.Session!;
        windows.Foreground(2); service.Tick();
        now = 119; service.Tick(); Assert.True(service.IsActive);
        now = 120; service.Tick(); Assert.False(service.IsActive);
        Assert.Equal(1, session.Restores); Assert.False(service.NeedsPolling);
    }

    [Fact]
    public void ShellOwnAppAndDialogPreserveFullscreenAndResetExitDebounce()
    {
        var windows = new WindowSystem(); long now = 0;
        windows.Add(5, "Dialog"); windows.Owners[5] = 1;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget();
        foreach (nint foreground in new nint[] { 3, 4, 5, 0 })
        {
            windows.Foreground(foreground); service.Tick(); now += 1000; service.Tick();
            Assert.True(service.IsActive);
        }
        windows.Foreground(2); service.Tick(); now += 100;
        windows.Foreground(3); service.Tick(); now += 1000;
        windows.Foreground(2); service.Tick(); Assert.True(service.IsActive);
        now += 120; service.Tick(); Assert.False(service.IsActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HiddenOrMinimizedTargetRestores(bool minimized)
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget();
        windows.Items[1] = windows.Items[1] with { IsMinimized = minimized, IsVisible = minimized };
        service.Tick(); Assert.False(service.IsActive); Assert.Equal(1, windows.Session!.Restores);
    }

    [Fact]
    public void RecycledHandleCannotKeepSession()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget(); windows.Add(1, "Replacement", process: 99, thread: 100);
        Assert.False(windows.Session!.OwnsWindow);
        service.Tick(); Assert.False(service.IsActive);
    }

    [Fact]
    public void ActiveTickUsesNativeStatusWithoutReadingProcessAndCaptionSnapshots()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget();
        windows.SnapshotReads = 0;
        for (int tick = 0; tick < 40; tick++) { now += 30; service.Tick(); }
        Assert.Equal(0, windows.SnapshotReads);
        Assert.True(service.IsActive);
        Assert.Equal(40, windows.Session!.PointerUpdates);
    }

    [Fact]
    public void CloakedTargetEndsSessionAndRestoresOwnedWindow()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget();
        windows.Session!.IsCloaked = true;
        service.Tick();
        Assert.False(service.IsActive);
        Assert.Equal(1, windows.Session.Restores);
    }

    [Fact]
    public void RestoreFailureRetainsRecoveryWithoutPollingAndToggleRetries()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget(); windows.Session!.FailRestoreOnce = true; service.ToggleTarget();
        Assert.True(service.IsActive); Assert.False(service.NeedsPolling);
        Assert.Equal("Failed to restore", service.Error);
        service.Tick(); Assert.Equal(1, windows.Session.Restores);
        service.ToggleForeground(); Assert.False(service.IsActive);
        Assert.Equal(2, windows.Session.Restores); Assert.Null(service.Error);
    }

    [Fact]
    public void DeactivationFailureRetainsOwnedSessionAndStopsPollingUntilRetry()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget();
        var session = windows.Session!;
        session.FailRestoreOnce = true;

        Assert.False(service.TryDeactivate());
        Assert.True(service.IsActive);
        Assert.True(session.OwnsWindow);
        Assert.False(service.NeedsPolling);
        Assert.False(windows.Disposed);

        Assert.True(service.TryDeactivate());
        Assert.False(service.IsActive);
        Assert.False(session.OwnsWindow);
        Assert.Equal(2, session.Restores);
        Assert.Null(service.Error);
        Assert.False(windows.Disposed);
        Assert.True(service.TryDeactivate());
        Assert.Equal(2, session.Restores);
    }

    [Fact]
    public void PartialActivationRollbackFailureRetainsSessionForCleanup()
    {
        var windows = new WindowSystem { FailRollback = true }; long now = 0;
        using var service = new FullscreenService(windows, () => now);
        service.ToggleTarget();
        Assert.True(service.IsActive); Assert.False(service.NeedsPolling);
        Assert.Equal("Could not fully restore the window.", service.Error);
        service.ToggleTarget(); Assert.False(service.IsActive); Assert.Null(service.Error);
    }

    [Fact]
    public void ExplorerRestartReappliesAndTopologyChangeRestores()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now, monitorName: _ => "External display");
        service.ToggleTarget(); Assert.Equal("External display", service.ActiveMonitor);
        Assert.Equal(@"\\.\DISPLAY2", service.ActiveMonitorDevice);
        service.OnEnvironmentChanged(); Assert.Equal(1, windows.Session!.Reapplies);
        windows.Session.DisplayUnchanged = false;
        service.OnEnvironmentChanged(); Assert.False(service.IsActive);
    }

    [Fact]
    public void FailedActivationDoesNotPollAndDisposeRestores()
    {
        var windows = new WindowSystem { FailEnter = true }; long now = 0;
        var service = new FullscreenService(windows, () => now);
        service.ToggleTarget(); Assert.False(service.NeedsPolling); Assert.Equal("Failed to enter", service.Error);
        windows.FailEnter = false; service.ToggleTarget(); service.Dispose();
        Assert.Equal(1, windows.Session!.Restores); Assert.True(windows.Disposed);
    }

    [Fact]
    public void ActionReflectsActiveMonitorAndRestoresUsingTile()
    {
        var windows = new WindowSystem(); long now = 0;
        using var service = new FullscreenService(windows, () => now);
        using var action = new FullscreenAction(service);
        Assert.Equal(ActionState.Off, action.State); service.ToggleTarget();
        Assert.Equal(ActionState.On, action.State); Assert.Contains(@"\\.\DISPLAY2", action.Subtitle);
        service.ToggleTarget(); Assert.Equal(ActionState.Off, action.State);
    }

    [Fact]
    public void NegativeMonitorCoverageDoesNotDoubleCountClientAndDwmInsets()
    {
        var raw = Rect(-1600, -200, 0, 700);
        var visible = Rect(-1592, -200, -8, 692);
        var client = Rect(-1593, -200, -7, 693);
        Assert.True(NativeFullscreenWindowSystem.CompensateCoverageBounds(raw, visible, client)
            .EqualsRect(Rect(-1608, -200, 8, 708)));
        Assert.True(NativeFullscreenWindowSystem.CompensateCoverageBounds(raw, raw, client)
            .EqualsRect(Rect(-1607, -200, 7, 707)));
        Assert.True(NativeFullscreenWindowSystem.CompensateCoverageBounds(raw, raw, raw).EqualsRect(raw));
        Assert.Throws<InvalidOperationException>(() => NativeFullscreenWindowSystem.CompensateCoverageBounds(raw, raw, Rect(-1567, -200, 0, 700)));
    }

    private static NativeFullscreenWindowSystem.Rect Rect(int left, int top, int right, int bottom)
        => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    [Fact]
    public void RestoreMaximizedAdaptsToAvailableWorkAreaAndDpi()
    {
        var oldWork = Rect(-1600, -200, 0, 640);
        var oldBounds = Rect(-1608, -208, 8, 648);
        var rotatedWork = Rect(0, 0, 900, 1540);
        Assert.True(NativeFullscreenWindowSystem.AdaptMaximizedBounds(oldBounds, oldWork, rotatedWork, 96, 144)
            .EqualsRect(Rect(-12, -12, 912, 1552)));
        Assert.True(NativeFullscreenWindowSystem.AdaptMaximizedBounds(oldBounds, oldWork, oldWork, 96, 96).EqualsRect(oldBounds));
    }

    [Fact]
    public void RectangularRegionPreservesPhysicalFrameInsetsWhenMonitorRotates()
    {
        var oldBounds = Rect(-11, -11, 3851, 2171);
        var oldRegion = Rect(11, 11, 3851, 2171);
        var newBounds = Rect(-11, -11, 2171, 3851);
        Assert.True(NativeFullscreenWindowSystem.AdaptRegionBounds(oldRegion, oldBounds, newBounds, 96, 96)
            .EqualsRect(Rect(11, 11, 2171, 3851)));
        Assert.True(NativeFullscreenWindowSystem.AdaptRegionBounds(oldRegion, oldBounds, newBounds, 96, 144)
            .EqualsRect(Rect(16, 16, 2166, 3846)));
    }

    [Fact]
    public void RegisteredAutoHiddenBarKeepsInteractionAreaWhileOutsideItsMonitor()
    {
        var upperMonitor = Rect(-1920, -1080, 0, 0);
        // Explorer's bar for the upper screen has slid into the lower screen's coordinates.
        var hiddenBar = Rect(-1920, 1, 0, 73);
        var visibleBar = Rect(-1920, -72, 0, 0);
        Assert.Equal(72, NativeFullscreenWindowSystem.TaskbarInteractionHeight(upperMonitor, hiddenBar, 96, registeredForMonitor: true));
        Assert.Equal(72, NativeFullscreenWindowSystem.TaskbarInteractionHeight(upperMonitor, visibleBar, 96));
        Assert.Equal(72, NativeFullscreenWindowSystem.TaskbarInteractionHeight(upperMonitor, Rect(-1920, 1, 0, 3), 144, registeredForMonitor: true));
        var lowerMonitorWithoutBar = Rect(-1920, 0, 0, 1080);
        Assert.Equal(0, NativeFullscreenWindowSystem.TaskbarInteractionHeight(lowerMonitorWithoutBar, hiddenBar, 96));
        Assert.Equal(0, NativeFullscreenWindowSystem.TaskbarInteractionHeight(upperMonitor, Rect(0, 0, 0, 0), 96));
    }
}
