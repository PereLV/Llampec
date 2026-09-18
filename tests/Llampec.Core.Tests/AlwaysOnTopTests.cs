using System.Text.Json;
using Llampec.Actions;
using Llampec.Actions.AlwaysOnTop;
using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public sealed class AlwaysOnTopTests
{
    private sealed class Border : IAlwaysOnTopBorder
    {
        public int Thickness;
        public bool Disposed;
        public void Update(int thickness) => Thickness = thickness;
        public void Dispose() => Disposed = true;
    }

    private sealed class Windows : IAlwaysOnTopWindowSystem
    {
        public event Action<WindowChange>? WindowChanged;
        public bool IsTrackingForeground { get; set; } = true;
        public string? LastError { get; set; }
        public nint ForegroundWindow { get; set; } = 1;
        public Dictionary<nint, WindowSnapshot> Items = [];
        public HashSet<nint> OwnWindows = [];
        public Dictionary<nint, nint> ForegroundMappings = [];
        public HashSet<WindowIdentity> Claims = [];
        public List<(WindowIdentity Identity, bool Topmost)> Mutations = [];
        public bool FailClaim, FailPin, FailUnpin, PartialPin, Disposed, NotifyDuringMutation;
        public int Enumerations;

        public Windows()
        {
            Add(1, "Alpha");
            Add(2, "Bravo");
            Add(3, "Shell or Llampec", eligible: false);
        }

        public void Add(nint handle, string title, uint process = 42, uint thread = 24, bool eligible = true, bool topmost = false)
            => Items[handle] = new(new(handle, process, thread), title, eligible, topmost);
        public WindowSnapshot? Read(nint handle) => Items.GetValueOrDefault(handle);
        public ForegroundWindowResolution ResolveForeground(nint handle)
        {
            var window = Read(ForegroundMappings.GetValueOrDefault(handle, handle));
            return new(window is { IsEligible: true } ? window : null, OwnWindows.Contains(handle),
                window is { IsEligible: true } ? null : "ineligible synthetic foreground");
        }
        public IReadOnlyList<WindowSnapshot> Enumerate() { Enumerations++; return Items.Values.ToArray(); }
        public bool TryClaim(WindowIdentity identity) => !FailClaim && Claims.Add(identity);
        public bool HasClaim(WindowIdentity identity) => Read(identity.Handle)?.Identity == identity && Claims.Contains(identity);
        public void ReleaseClaim(WindowIdentity identity) => Claims.Remove(identity);
        public bool TrySetTopmost(WindowIdentity identity, bool topmost)
        {
            if (!HasClaim(identity)) throw new Xunit.Sdk.XunitException("Mutation without a valid claim.");
            Mutations.Add((identity, topmost));
            bool failed = topmost ? FailPin : FailUnpin;
            if (!failed || (topmost && PartialPin)) Items[identity.Handle] = Items[identity.Handle] with { IsTopmost = topmost };
            if (NotifyDuringMutation) Notify(identity.Handle);
            return !failed;
        }
        public void Foreground(nint handle) { ForegroundWindow = handle; WindowChanged?.Invoke(new(handle, true)); }
        public void Notify(nint handle) => WindowChanged?.Invoke(new(handle, false));
        public void Move(nint handle) => WindowChanged?.Invoke(new(handle, false, true));
        public void Dispose() => Disposed = true;
    }

    private static AlwaysOnTopService Create(Windows windows, AlwaysOnTopSettings? settings = null, List<Border>? borders = null)
    {
        settings ??= new();
        return new(() => settings, windows, _ =>
        {
            var border = new Border();
            borders?.Add(border);
            return border;
        });
    }

    [Fact]
    public void RemembersExternalWindowAcrossShellAndFreezesTargetUntilPanelCloses()
    {
        var windows = new Windows();
        using var service = Create(windows);
        windows.Foreground(3);
        service.CaptureTarget();
        Assert.Equal((nint)1, service.TargetWindow);
        windows.Foreground(2);
        Assert.Equal((nint)1, service.TargetWindow);
        service.ToggleTarget();
        Assert.True(windows.Items[1].IsTopmost);
        Assert.False(windows.Items[2].IsTopmost);
        service.ReleaseTarget();
        Assert.Equal((nint)2, service.TargetWindow);
    }

    [Fact]
    public void MissingTargetOffersSelectorAndShortcutNeverUsesBackgroundFallback()
    {
        var windows = new Windows { ForegroundWindow = 3 };
        using var service = Create(windows);
        int requests = 0;
        service.SelectionRequested += (_, _) => requests++;
        service.ToggleTarget();
        windows.Foreground(1);
        windows.Foreground(3);
        service.ToggleForeground();
        Assert.Equal(1, requests);
        Assert.Equal("No eligible window is active.", service.Error);
        Assert.Empty(windows.Mutations);
    }

    [Fact]
    public void ShortcutResolvesOwnedDialogToItsApplicationWithoutOpeningSelector()
    {
        var windows = new Windows();
        windows.Add(4, "Owned dialog", eligible: false);
        windows.ForegroundMappings[4] = 2;
        using var service = Create(windows);
        int selections = 0;
        service.SelectionRequested += (_, _) => selections++;
        windows.Foreground(4);
        service.ToggleForeground();
        Assert.True(windows.Items[2].IsTopmost);
        Assert.False(windows.Items[1].IsTopmost);
        Assert.Equal(0, selections);
        Assert.Null(service.Error);
        Assert.Equal((nint)2, service.TargetWindow);
    }

    [Fact]
    public void ShortcutWhileLlampecHasFocusUsesFrozenExternalTarget()
    {
        var windows = new Windows();
        windows.OwnWindows.Add(3);
        using var service = Create(windows);
        service.CaptureTarget();
        windows.Foreground(2);
        windows.Foreground(3);
        int selections = 0;
        service.SelectionRequested += (_, _) => selections++;
        service.ToggleForeground();
        Assert.True(windows.Items[1].IsTopmost);
        Assert.False(windows.Items[2].IsTopmost);
        Assert.Equal(0, selections);
        Assert.Null(service.Error);
    }

    [Fact]
    public void ShortcutOnExternalWindowIgnoresDifferentFrozenPanelTarget()
    {
        var windows = new Windows();
        using var service = Create(windows);
        service.CaptureTarget();
        windows.Foreground(2);
        service.ToggleForeground();
        Assert.False(windows.Items[1].IsTopmost);
        Assert.True(windows.Items[2].IsTopmost);
    }

    [Fact]
    public void ShortcutWhileOwnPanelHasFocusDoesNotUseClosedOrRecycledTarget()
    {
        var windows = new Windows();
        windows.OwnWindows.Add(3);
        using var service = Create(windows);
        service.CaptureTarget();
        windows.Foreground(3);
        windows.Add(1, "Recycled", process: 987);
        service.ToggleForeground();
        Assert.Empty(windows.Mutations);
        Assert.Equal("No eligible window is active.", service.Error);
    }

    [Fact]
    public void MultiplePinsSortFirstAndTileStateOnlyRepresentsItsTarget()
    {
        var windows = new Windows();
        using var service = Create(windows);
        using var action = new AlwaysOnTopAction(service);
        service.CaptureTarget();
        service.GetWindows();
        service.Toggle(2);
        Assert.Equal(1, service.PinnedCount);
        Assert.Equal(ActionState.Off, action.State);
        Assert.Equal((nint)2, service.GetWindows()[0].Handle);
        service.ToggleTarget();
        Assert.Equal(2, service.PinnedCount);
        Assert.Equal(ActionState.On, action.State);
        service.UnpinAll();
        Assert.Equal(0, service.PinnedCount);
        Assert.All(windows.Items.Values, window => Assert.False(window.IsTopmost));
    }

    [Fact]
    public void ExternalTopmostWindowsAreNeverClaimedOrUnpinned()
    {
        var windows = new Windows();
        windows.Items[1] = windows.Items[1] with { IsTopmost = true };
        using var service = Create(windows);
        Assert.DoesNotContain(service.GetWindows(), window => window.Handle == 1);
        service.ToggleTarget();
        Assert.Equal("This window is already kept on top by another application.", service.Error);
        service.CaptureTarget(); // Showing the panel after a shortcut failure must retain the explanation.
        Assert.NotNull(service.Error);
        service.UnpinAll();
        service.Dispose();
        Assert.True(windows.Items[1].IsTopmost);
        Assert.Empty(windows.Claims);
        Assert.Empty(windows.Mutations);
    }

    [Fact]
    public void FailedClaimAndPinDoNotLeavePinsOrMarkers()
    {
        var windows = new Windows { FailClaim = true };
        using var service = Create(windows);
        service.ToggleTarget();
        Assert.NotNull(service.Error);
        Assert.Empty(windows.Mutations);
        windows.FailClaim = false;
        windows.FailPin = true;
        service.ToggleTarget();
        Assert.Equal(0, service.PinnedCount);
        Assert.Empty(windows.Claims);
        Assert.False(windows.Items[1].IsTopmost);
    }

    [Fact]
    public void PartialNativeFailureRemainsTrackedSoItCanBeRestored()
    {
        var windows = new Windows { FailPin = true, PartialPin = true };
        using var service = Create(windows);
        service.ToggleTarget();
        Assert.NotNull(service.Error);
        Assert.Equal(1, service.PinnedCount);
        service.UnpinAll();
        Assert.False(windows.Items[1].IsTopmost);
        Assert.Empty(windows.Claims);
    }

    [Fact]
    public void NativePermissionFailureIsPreservedForPinAndUnpinFeedback()
    {
        const string permissionError = "This window requires administrator permissions. Restart Llampec as administrator to pin it.";
        var windows = new Windows { FailClaim = true, LastError = permissionError };
        using var service = Create(windows);
        service.ToggleForeground();
        Assert.Equal(permissionError, service.Error);
        windows.FailClaim = false;
        windows.FailPin = true;
        service.ToggleForeground();
        Assert.Equal(permissionError, service.Error);
        windows.FailPin = false;
        windows.LastError = null;
        service.ToggleForeground();
        Assert.Null(service.Error);
        windows.LastError = permissionError;
        windows.FailUnpin = true;
        service.ToggleForeground();
        Assert.Equal(permissionError, service.Error);
        Assert.Equal(1, service.PinnedCount);
        service.UnpinAll();
        Assert.Equal(permissionError, service.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(1400)]
    public void OtherNativeFailuresPreserveTheOperationSpecificMessage(int error)
    {
        Assert.Equal("Could not pin this window.", NativeAlwaysOnTopWindowSystem.ErrorForNativeFailure(error, "Could not pin this window."));
        Assert.Equal("Could not unpin this window.", NativeAlwaysOnTopWindowSystem.ErrorForNativeFailure(error, "Could not unpin this window."));
    }

    [Fact]
    public void AccessDeniedProducesActionableAdministratorExplanation()
    {
        const string expected = "This window requires administrator permissions. Restart Llampec as administrator to pin it.";
        Assert.Equal(expected, NativeAlwaysOnTopWindowSystem.ErrorForNativeFailure(5, "Could not pin this window."));
        Assert.Equal(expected, NativeAlwaysOnTopWindowSystem.ErrorForNativeFailure(5, "Could not unpin this window."));
    }

    [Fact]
    public void FailedUnpinRetainsOwnershipAndBorderForRetry()
    {
        var windows = new Windows();
        var borders = new List<Border>();
        using var service = Create(windows, borders: borders);
        service.ToggleTarget();
        windows.FailUnpin = true;
        service.UnpinAll();
        Assert.Equal("Could not unpin this window.", service.Error);
        Assert.Equal(1, service.PinnedCount);
        Assert.False(Assert.Single(borders).Disposed);
        windows.FailUnpin = false;
        service.UnpinAll();
        Assert.Equal(0, service.PinnedCount);
        Assert.True(borders[0].Disposed);
    }

    [Fact]
    public void ClosingWindowDropsPinAndBorderWithoutMutatingAnotherHandle()
    {
        var windows = new Windows();
        var borders = new List<Border>();
        using var service = Create(windows, borders: borders);
        service.CaptureTarget();
        service.ToggleTarget();
        windows.Items.Remove(1);
        windows.Notify(1);
        Assert.Equal(0, service.PinnedCount);
        Assert.False(service.HasTarget);
        Assert.True(Assert.Single(borders).Disposed);
        Assert.Single(windows.Mutations);
    }

    [Fact]
    public void RecycledPinnedHandleWithDifferentProcessOrMissingMarkerIsNeverUnpinned()
    {
        var windows = new Windows();
        using var service = Create(windows);
        service.ToggleTarget();
        windows.Add(1, "Different process", process: 99, topmost: true);
        service.UnpinAll();
        Assert.True(windows.Items[1].IsTopmost);
        Assert.Single(windows.Mutations);

        windows.Add(1, "Another same-thread window");
        service.ToggleTarget();
        windows.Claims.Clear(); // Window recreation on the same process/thread destroys its property list.
        service.UnpinAll();
        Assert.True(windows.Items[1].IsTopmost);
        Assert.Equal(2, windows.Mutations.Count);
    }

    [Fact]
    public void OldSelectorRowDoesNotPinRecycledHandleBeforeRowsRefresh()
    {
        var windows = new Windows();
        using var service = Create(windows);
        service.GetWindows();
        windows.Add(2, "Replacement", process: 999);
        windows.Notify(2);
        service.Toggle(2);
        Assert.Equal("This window is no longer available.", service.Error);
        Assert.Empty(windows.Mutations);
        service.GetWindows();
        service.Toggle(2);
        Assert.True(windows.Items[2].IsTopmost);
    }

    [Fact]
    public void ExternalUnpinReleasesOurMarkerAndBorder()
    {
        var windows = new Windows();
        var borders = new List<Border>();
        using var service = Create(windows, borders: borders);
        service.ToggleTarget();
        windows.Items[1] = windows.Items[1] with { IsTopmost = false };
        windows.Notify(1);
        Assert.Equal(0, service.PinnedCount);
        Assert.Empty(windows.Claims);
        Assert.True(Assert.Single(borders).Disposed);
    }

    [Fact]
    public void AppearanceAppliesLiveAndDisablingRetainsThickness()
    {
        var windows = new Windows();
        var preferences = new AlwaysOnTopSettings();
        var borders = new List<Border>();
        using var service = Create(windows, preferences, borders);
        service.ToggleTarget();
        Assert.Equal(3, Assert.Single(borders).Thickness);
        preferences.BorderThickness = 6;
        service.ApplyAppearance();
        Assert.Equal(6, borders[0].Thickness);
        preferences.ShowBorder = false;
        service.ApplyAppearance();
        Assert.True(borders[0].Disposed);
        Assert.True(windows.Items[1].IsTopmost);
        Assert.Equal(6, preferences.BorderThickness);
        preferences.ShowBorder = true;
        service.ApplyAppearance();
        Assert.Equal(2, borders.Count);
        Assert.Equal(6, borders[1].Thickness);
    }

    [Fact]
    public void BorderFailureDoesNotAbandonSuccessfulPin()
    {
        var windows = new Windows();
        using var service = new AlwaysOnTopService(() => new(), windows, _ => throw new IOException("test"));
        service.ToggleTarget();
        Assert.Equal("Could not show the window border.", service.Error);
        Assert.Equal(1, service.PinnedCount);
        service.UnpinAll();
        Assert.False(windows.Items[1].IsTopmost);
    }

    [Fact]
    public void ReentrantNativeAndChangedEventsCannotRecursivelyRefresh()
    {
        var windows = new Windows { NotifyDuringMutation = true };
        using var service = Create(windows);
        int notifications = 0;
        service.Changed += (_, _) => { notifications++; service.Refresh(); service.GetWindows(); };
        service.ToggleTarget();
        service.Refresh();
        Assert.Equal(1, notifications);
        service.ToggleTarget();
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void WindowMotionDoesNotRebuildSelectorButDetectsRemovedTopmostStyle()
    {
        var windows = new Windows();
        using var service = Create(windows);
        using var action = new AlwaysOnTopAction(service);
        service.ToggleTarget();
        int enumerations = windows.Enumerations;
        for (int i = 0; i < 30; i++) { windows.Move(1); windows.Move(2); }
        Assert.Equal(enumerations, windows.Enumerations);
        windows.Items[1] = windows.Items[1] with { IsTopmost = false };
        windows.Move(1);
        Assert.Equal(0, service.PinnedCount);
        Assert.Equal(ActionState.Off, action.State);
        Assert.Equal(enumerations + 1, windows.Enumerations);
    }

    [Fact]
    public void DisposeRestoresAllOwnedPinsOnceAndIgnoresLateEvents()
    {
        var windows = new Windows();
        var borders = new List<Border>();
        var service = Create(windows, borders: borders);
        service.GetWindows();
        service.Toggle(1);
        service.Toggle(2);
        service.Dispose();
        service.Dispose();
        windows.Notify(1);
        Assert.True(windows.Disposed);
        Assert.Equal(0, service.PinnedCount);
        Assert.Empty(windows.Claims);
        Assert.All(borders, border => Assert.True(border.Disposed));
        Assert.All(windows.Items.Values, window => Assert.False(window.IsTopmost));
        Assert.Equal(4, windows.Mutations.Count);
    }

    [Fact]
    public void UnavailableForegroundHookRequiresSelectorAfterFocusIsLost()
    {
        var windows = new Windows { IsTrackingForeground = false };
        using var service = Create(windows);
        Assert.Equal("Window tracking is unavailable. Choose a window from the list.", service.Error);
        windows.ForegroundWindow = 3;
        service.CaptureTarget();
        Assert.False(service.HasTarget);
        Assert.NotEmpty(service.GetWindows());
    }

    [Theory]
    [InlineData(-500, 1)]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(800, 8)]
    public void StoredThicknessIsNormalized(int value, int expected)
    {
        var settings = new AlwaysOnTopSettings { BorderThickness = value };
        Assert.Equal(expected, settings.BorderThickness);
    }

    [Theory]
    [InlineData(null, "Ctrl+Alt+T")]
    [InlineData("not-a-shortcut", "Ctrl+Alt+T")]
    [InlineData("  ", "")]
    [InlineData("ctrl+shift+a", "Ctrl+Shift+A")]
    public void StoredHotkeyIsNormalized(string? value, string expected)
        => Assert.Equal(expected, new AlwaysOnTopSettings { Hotkey = value! }.Hotkey);

    [Fact]
    public void AppearanceRoundTripsAndNullFeatureSettingsUseDefaults()
    {
        var settings = new AppSettings { AlwaysOnTop = new() { ShowBorder = false, BorderThickness = 7, Hotkey = "Win+Ctrl+T" } };
        string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        Assert.False(restored.AlwaysOnTop.ShowBorder);
        Assert.Equal(7, restored.AlwaysOnTop.BorderThickness);
        Assert.Equal("Win+Ctrl+T", restored.AlwaysOnTop.Hotkey);
        var nullSettings = JsonSerializer.Deserialize("{\"alwaysOnTop\":null}", SettingsJsonContext.Default.AppSettings)!;
        Assert.True(nullSettings.AlwaysOnTop.ShowBorder);
        Assert.Equal(3, nullSettings.AlwaysOnTop.BorderThickness);
    }

    [Fact]
    public void ExistingTopmostPaletteProtectsItsOwnerIncludingNestedOwnedWindows()
    {
        var owners = new Dictionary<nint, nint> { [1] = 0, [2] = 1, [3] = 2, [4] = 0, [5] = 6, [6] = 5 };
        var topmost = new HashSet<nint> { 3, 4, 5 };
        Assert.True(NativeAlwaysOnTopWindowSystem.HasTopmostOwnedWindow(1, owners.Keys, h => owners[h], topmost.Contains));
        topmost.Remove(3);
        // An unrelated topmost window and a malformed ownership cycle cannot block this owner.
        Assert.False(NativeAlwaysOnTopWindowSystem.HasTopmostOwnedWindow(1, owners.Keys, h => owners[h], topmost.Contains));
    }
}
