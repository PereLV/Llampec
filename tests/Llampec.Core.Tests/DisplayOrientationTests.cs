using System.Runtime.InteropServices;
using Llampec.Actions;
using Llampec.Actions.Rotation;
using Llampec.Interop;
using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public sealed class DisplayOrientationTests
{
    private static readonly DisplayMode Landscape = new(0, 2880, 1920, 0, 0, 32, 120, 0, 0);
    private static DisplayInfo Internal => new("surface", "Surface", @"\\.\DISPLAY2", "source2", true, false, true, Landscape);
    private static DisplayInfo External => new("external", "External", @"\\.\DISPLAY1", "source1", false, false, true, Landscape with { X = 2880 });

    [Theory]
    [InlineData(AutoRotationFlags.Enabled, false, true)]
    [InlineData(AutoRotationFlags.Disabled, true, true)]
    [InlineData(AutoRotationFlags.Laptop, false, false)]
    [InlineData(AutoRotationFlags.Disabled | AutoRotationFlags.Laptop, true, false)]
    [InlineData(AutoRotationFlags.Disabled | AutoRotationFlags.Docked | AutoRotationFlags.MultipleMonitors, true, false)]
    [InlineData(AutoRotationFlags.NoSensor, false, false)]
    [InlineData(AutoRotationFlags.NotSupported, false, false)]
    [InlineData(AutoRotationFlags.RemoteSession, false, false)]
    [InlineData(AutoRotationFlags.Suppressed, false, false)]
    [InlineData((AutoRotationFlags)0x100, false, false)]
    public void RotationPreferenceIsSeparateFromSuppression(AutoRotationFlags flags, bool locked, bool toggle)
    {
        var state = new RotationState(true, flags);
        Assert.Equal(locked, state.IsLocked);
        Assert.Equal(toggle, state.CanToggle);
        Assert.NotEmpty(state.ReasonKeys);
    }

    [Fact]
    public void CombinedReasonsRemainVisible()
    {
        var state = new RotationState(true, AutoRotationFlags.Disabled | AutoRotationFlags.Laptop | AutoRotationFlags.MultipleMonitors);
        Assert.True(state.IsLocked);
        Assert.Equal(2, state.ReasonKeys.Count);
        Assert.Contains("Rotation paused while the keyboard is attached", state.ReasonKeys);
        Assert.Contains("Rotation paused with multiple displays", state.ReasonKeys);
        Assert.False(new RotationState(false, AutoRotationFlags.Enabled).CanToggle);
    }

    [Theory]
    [InlineData(AutoRotationFlags.Enabled, false)]
    [InlineData(AutoRotationFlags.Disabled, true)]
    [InlineData(AutoRotationFlags.Laptop, true)]
    [InlineData(AutoRotationFlags.Laptop | AutoRotationFlags.Suppressed, true)]
    [InlineData(AutoRotationFlags.Laptop | AutoRotationFlags.RemoteSession, true)]
    [InlineData(AutoRotationFlags.Disabled | AutoRotationFlags.Laptop, true)]
    [InlineData(AutoRotationFlags.Docked, true)]
    [InlineData(AutoRotationFlags.MultipleMonitors, true)]
    [InlineData(AutoRotationFlags.NoSensor, true)]
    [InlineData(AutoRotationFlags.NotSupported, true)]
    [InlineData(AutoRotationFlags.Suppressed, false)]
    [InlineData(AutoRotationFlags.RemoteSession, false)]
    [InlineData((AutoRotationFlags)0x100, false)]
    [InlineData((AutoRotationFlags)0x101, false)]
    public void ManualOrientationDoesNotRequireAnAvailableAutoRotationToggle(AutoRotationFlags flags, bool withoutLockChange)
    {
        Assert.Equal(withoutLockChange, new RotationState(true, flags).CanSetOrientationWithoutChangingLock);
        Assert.False(new RotationState(false, flags).CanSetOrientationWithoutChangingLock);
    }

    [Theory]
    [InlineData(DisplayOrientation.Landscape, 0u, 2880u, 1920u)]
    [InlineData(DisplayOrientation.Portrait, 1u, 1920u, 2880u)]
    [InlineData(DisplayOrientation.LandscapeFlipped, 2u, 2880u, 1920u)]
    [InlineData(DisplayOrientation.PortraitFlipped, 3u, 1920u, 2880u)]
    public void RotationPreservesResolutionAndOtherModeFields(DisplayOrientation orientation, uint nativeRotation, uint width, uint height)
    {
        foreach (DisplayOrientation start in Enum.GetValues<DisplayOrientation>())
        {
            var changed = Landscape.WithOrientation(start).WithOrientation(orientation);
            Assert.Equal(orientation, changed.Orientation);
            Assert.Equal(nativeRotation, changed.Rotation);
            Assert.Equal(width, changed.Width);
            Assert.Equal(height, changed.Height);
            Assert.Equal(Landscape.Frequency, changed.Frequency);
            Assert.Equal(Landscape, changed.WithOrientation(DisplayOrientation.Landscape));
        }
    }

    [Fact]
    public void NaturallyPortraitMonitorHasCorrectLabelsAndDimensions()
    {
        var natural = Landscape with { Width = 1200, Height = 1920 };
        Assert.Equal(DisplayOrientation.Portrait, natural.Orientation);
        var landscape = natural.WithOrientation(DisplayOrientation.Landscape);
        Assert.Equal(1920u, landscape.Width);
        Assert.Equal(1200u, landscape.Height);
        Assert.Equal(DisplayOrientation.Landscape, landscape.Orientation);
        Assert.Equal(natural, landscape.WithOrientation(DisplayOrientation.Portrait));
    }

    [Fact]
    public void NativeStructuresHaveWindowsLayout()
    {
        Assert.Equal(220, Marshal.SizeOf<DisplayOrientationNative.DevMode>());
        Assert.Equal(84, Marshal.OffsetOf<DisplayOrientationNative.DevMode>(nameof(DisplayOrientationNative.DevMode.Orientation)).ToInt32());
        Assert.Equal(172, Marshal.OffsetOf<DisplayOrientationNative.DevMode>(nameof(DisplayOrientationNative.DevMode.Width)).ToInt32());
        Assert.Equal(84, Marshal.SizeOf<DisplayOrientationNative.SourceName>());
    }

    [Fact]
    public async Task ExternalOrientationIsSavedImmediatelyWithoutTouchingSurface()
    {
        var backend = new Backend(External, Internal);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(External.Id, DisplayOrientation.Portrait);
        Assert.Equal(0, backend.Shortcuts);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.Equal(DisplayOrientation.Portrait, backend.Current.Find(External.Id)!.Orientation);
        Assert.Equal(backend.Current.Find(External.Id)!.Mode, backend.Current.Find(External.Id)!.SavedMode);
        Assert.False(service.RecoveryPending);
        Assert.Equal("Orientation applied", service.MessageKey);
        Assert.Single(backend.Applies);
        Assert.True(backend.Applies[^1].Persist);
    }

    [Fact]
    public async Task IntegratedDisplayIsLockedBeforeApplyingAndSavingOrientation()
    {
        var backend = new Backend(Internal);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.True(backend.Current.Rotation.IsLocked);
        Assert.Equal(["lock", "apply:surface:Portrait:True"], backend.Operations);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task ClosingTheServiceNeverRevertsASuccessfulSelectionOrItsLock()
    {
        var backend = new Backend(Internal);
        var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        await service.DisposeAsync();
        Assert.Equal(DisplayOrientation.Portrait, backend.Current.Find(Internal.Id)!.Orientation);
        Assert.Equal(backend.Current.Find(Internal.Id)!.Mode, backend.Current.Find(Internal.Id)!.SavedMode);
        Assert.True(backend.Current.Rotation.IsLocked);
        Assert.Single(backend.Applies);
        Assert.Equal(1, backend.Shortcuts);
    }

    [Fact]
    public async Task SuccessDoesNotCreateATimerOrRequireConfirmationBeforeNextSelection()
    {
        var backend = new Backend(Internal);
        await using var service = new DisplayOrientationService(backend, new RejectTimersTimeProvider());
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.LandscapeFlipped);
        Assert.Equal(DisplayOrientation.LandscapeFlipped, backend.Current.Find(Internal.Id)!.Orientation);
        Assert.Equal(2, backend.Applies.Count);
        Assert.Equal(1, backend.Shortcuts);
        Assert.False(service.RecoveryPending);
        await service.ToggleLockAsync();
        Assert.False(backend.Current.Rotation.IsLocked);
    }

    [Fact]
    public async Task SelectingCurrentOrientationLocksWithoutRedundantDisplayWrites()
    {
        var backend = new Backend(Internal);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Landscape);
        Assert.True(backend.Current.Rotation.IsLocked);
        Assert.Empty(backend.Applies);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task SelectingCurrentLiveModeStillSavesItWhenTheSavedModeDiffers()
    {
        var backend = new Backend(External with { SavedMode = External.Mode.WithOrientation(DisplayOrientation.Portrait) });
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(External.Id, DisplayOrientation.Landscape);
        Assert.Single(backend.Applies);
        Assert.True(backend.Applies[0].Persist);
        Assert.Equal(External.Mode, backend.Current.Find(External.Id)!.SavedMode);
    }

    [Fact]
    public async Task ClonedSourceIsRejectedBeforeAnyLockOrDisplayWrite()
    {
        var backend = new Backend(Internal with { IsCloned = true });
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Empty(backend.Applies);
        Assert.Equal(0, backend.Shortcuts);
        Assert.Equal("Use Extend to rotate displays separately", service.MessageKey);
    }

    [Fact]
    public async Task MissingSelectedDisplayNeverFallsBackToPrimaryOrInternal()
    {
        var backend = new Backend(Internal);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(External.Id, DisplayOrientation.Portrait);
        Assert.Empty(backend.Applies);
        Assert.Equal(0, backend.Shortcuts);
        Assert.Equal("The selected display was disconnected", service.MessageKey);
    }

    [Theory]
    [InlineData(AutoRotationFlags.Laptop)]
    [InlineData(AutoRotationFlags.Docked)]
    [InlineData(AutoRotationFlags.MultipleMonitors)]
    [InlineData(AutoRotationFlags.Laptop | AutoRotationFlags.Suppressed)]
    [InlineData(AutoRotationFlags.Laptop | AutoRotationFlags.RemoteSession)]
    public async Task HardwareSuppressionAllowsManualOrientationWithoutChangingTheRotationLock(AutoRotationFlags flags)
    {
        var backend = new Backend(Internal);
        backend.Flags(flags);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Single(backend.Applies);
        Assert.Equal(DisplayOrientation.Portrait, backend.Current.Find(Internal.Id)!.Orientation);
        Assert.Equal(backend.Current.Find(Internal.Id)!.Mode, backend.Current.Find(Internal.Id)!.SavedMode);
        Assert.Equal(flags, backend.Current.Rotation.Flags);
        Assert.False(backend.Current.Rotation.CanToggle);
        Assert.Equal(0, backend.Shortcuts);
        Assert.False(service.RecoveryPending);
        Assert.Equal("Orientation applied", service.MessageKey);
    }

    [Theory]
    [InlineData(AutoRotationFlags.Laptop, AutoRotationFlags.Enabled, 1)]
    [InlineData(AutoRotationFlags.Enabled, AutoRotationFlags.Laptop, 0)]
    public async Task KeyboardTransitionDuringValidationUsesTheFreshAutoRotationState(
        AutoRotationFlags before, AutoRotationFlags after, int shortcuts)
    {
        var backend = new Backend(Internal);
        backend.Flags(before);
        backend.AfterTest = () => { backend.Flags(after); backend.AfterTest = null; };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Equal(shortcuts, backend.Shortcuts);
        Assert.Equal(DisplayOrientation.Portrait, backend.Current.Find(Internal.Id)!.Orientation);
        Assert.Equal("Orientation applied", service.MessageKey);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task SensorResumingDuringManualWriteIsNotReportedAsAFixedOrientation()
    {
        var backend = new Backend(Internal);
        backend.Flags(AutoRotationFlags.Laptop);
        backend.AfterApply = call => { if (call == 1) backend.Flags(AutoRotationFlags.Enabled); };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.SavedMode);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.Equal(0, backend.Shortcuts);
        Assert.Equal("Windows could not change rotation lock", service.MessageKey);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task DriverCanRejectManualOrientationEvenWhenTheKeyboardIsAttached()
    {
        var backend = new Backend(Internal) { FailTest = true };
        backend.Flags(AutoRotationFlags.Laptop);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Empty(backend.Applies);
        Assert.Equal(0, backend.Shortcuts);
        Assert.Equal("This orientation is not supported", service.MessageKey);
    }

    [Fact]
    public async Task PartialManualRotationFailureWithTheKeyboardAttachedRestoresModesWithoutTouchingTheLock()
    {
        var backend = new Backend(Internal);
        backend.Flags(AutoRotationFlags.Laptop);
        backend.FailApplyAt.Add(1);
        backend.PartialLiveAt.Add(1);
        backend.PartialSaveAt.Add(1);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.SavedMode);
        Assert.Equal(AutoRotationFlags.Laptop, backend.Current.Rotation.Flags);
        Assert.Equal(0, backend.Shortcuts);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task KeyboardAttachmentDuringRecoveryRetainsOurLockUntilItCanBeRestored()
    {
        var backend = new Backend(Internal);
        backend.FailApplyAt.Add(1);
        backend.PartialLiveAt.Add(1);
        backend.PartialSaveAt.Add(1);
        backend.AfterApply = call =>
        {
            if (call == 1) backend.Flags(AutoRotationFlags.Disabled | AutoRotationFlags.Laptop);
        };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.True(service.RecoveryPending);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.SavedMode);
        Assert.True(backend.Current.Rotation.IsLocked);
        Assert.Equal(1, backend.Shortcuts);
        backend.Flags(AutoRotationFlags.Disabled);
        await service.RevertRecoveryAsync();
        Assert.False(service.RecoveryPending);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.Equal(2, backend.Shortcuts);
    }

    [Theory]
    [InlineData(AutoRotationFlags.Suppressed)]
    [InlineData(AutoRotationFlags.RemoteSession)]
    [InlineData((AutoRotationFlags)0x101)]
    public async Task UnknownOrAppOnlySuppressionCannotBeUsedToAssumeAFixedOrientation(AutoRotationFlags flags)
    {
        var backend = new Backend(Internal);
        backend.Flags(flags);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Empty(backend.Applies);
        Assert.Equal(0, backend.Shortcuts);
        Assert.False(service.RecoveryPending);
        Assert.NotEqual("Orientation applied", service.MessageKey);
    }

    [Fact]
    public async Task IntegratedDisplayWithoutASensorCanStillRotateManually()
    {
        var backend = new Backend(Internal);
        backend.Flags(AutoRotationFlags.NoSensor);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Equal(DisplayOrientation.Portrait, backend.Current.Find(Internal.Id)!.Orientation);
        Assert.False(service.RecoveryPending);
        Assert.Equal(0, backend.Shortcuts);
    }

    [Fact]
    public async Task HeldKeysAreReportedWithoutInventingLockState()
    {
        var backend = new Backend(Internal) { SendAccepted = false };
        await using var service = new DisplayOrientationService(backend);
        await service.ToggleLockAsync();
        Assert.False(service.Snapshot.Rotation.IsLocked);
        Assert.Equal("Release the keyboard keys and try again", service.MessageKey);
        Assert.Empty(backend.Applies);
    }

    [Fact]
    public async Task FailedModeValidationDoesNotChangeRotationLock()
    {
        var backend = new Backend(Internal) { FailTest = true };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Equal(0, backend.Shortcuts);
        Assert.Empty(backend.Applies);
        Assert.Equal("This orientation is not supported", service.MessageKey);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PartialCommitRestoresOriginalLiveSavedModesAndOwnLock(bool changedLive, bool changedSaved)
    {
        var saved = Internal.Mode with { Frequency = 60 };
        var backend = new Backend(Internal with { SavedMode = saved });
        backend.FailApplyAt.Add(1);
        if (changedLive) backend.PartialLiveAt.Add(1);
        if (changedSaved) backend.PartialSaveAt.Add(1);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.Equal(saved, backend.Current.Find(Internal.Id)!.SavedMode);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.False(service.RecoveryPending);
        Assert.Equal(2, backend.Shortcuts);
    }

    [Fact]
    public async Task IgnoredDriverWriteIsNotReportedAsSuccessful()
    {
        var backend = new Backend(Internal);
        backend.IgnoreApplyAt.Add(1);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.False(service.RecoveryPending);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.Equal("Windows could not verify the orientation", service.MessageKey);
        Assert.Single(backend.Applies);
    }

    [Fact]
    public async Task IgnoredPersistenceAutomaticallyRecoversTheAppliedMode()
    {
        var backend = new Backend(External);
        backend.IgnoreSaveAt.Add(1);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(External.Id, DisplayOrientation.Portrait);
        Assert.Equal(External.Mode, backend.Current.Find(External.Id)!.Mode);
        Assert.False(service.RecoveryPending);
        Assert.Equal("Windows could not verify the orientation", service.MessageKey);
    }

    [Fact]
    public async Task RecoveryFailureRetainsManualRetryWithoutATimer()
    {
        var backend = new Backend(External);
        backend.FailApplyAt.UnionWith([1, 2]);
        backend.PartialLiveAt.Add(1);
        backend.PartialSaveAt.Add(1);
        await using var service = new DisplayOrientationService(backend, new RejectTimersTimeProvider());
        await service.ChangeOrientationAsync(External.Id, DisplayOrientation.Portrait);
        Assert.True(service.RecoveryPending);
        Assert.Equal(External.Id, service.RecoveryDisplayId);
        Assert.Equal("Windows could not restore the previous orientation", service.MessageKey);
        await service.RevertRecoveryAsync();
        Assert.False(service.RecoveryPending);
        Assert.Equal(External.Mode, backend.Current.Find(External.Id)!.Mode);
        Assert.Equal(External.Mode, backend.Current.Find(External.Id)!.SavedMode);
    }

    [Fact]
    public async Task ReadFailureDuringManualRecoveryRetainsControlsForRetry()
    {
        var backend = new Backend(External);
        await using var service = new DisplayOrientationService(backend);
        await LeaveRecoveryPending(service, backend, External);
        backend.CaptureFailuresRemaining = 1;
        await service.RevertRecoveryAsync();
        Assert.True(service.RecoveryPending);
        Assert.Single(backend.Applies);
        await service.RevertRecoveryAsync();
        Assert.False(service.RecoveryPending);
        Assert.Equal(External.Mode, backend.Current.Find(External.Id)!.Mode);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadFailureAfterRestoringSavedOrLiveModeStillAllowsFullRecovery(int failAfterWrite)
    {
        var saved = Internal.Mode with { Frequency = 60 };
        var backend = new Backend(Internal with { SavedMode = saved });
        backend.FailApplyAt.Add(1);
        backend.PartialLiveAt.Add(1);
        backend.PartialSaveAt.Add(1);
        backend.AfterApply = call => { if (call == failAfterWrite) backend.CaptureFailuresRemaining = 1; };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.True(service.RecoveryPending);
        Assert.True(backend.Current.Rotation.IsLocked);
        await service.RevertRecoveryAsync();
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.Equal(saved, backend.Current.Find(Internal.Id)!.SavedMode);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.False(service.RecoveryPending);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PartialSavedModeRestorationCanBeRetriedWithoutLosingTheOriginalLiveMode(bool changedLive, bool changedSaved)
    {
        var saved = Internal.Mode with { Frequency = 60 };
        var backend = new Backend(Internal with { SavedMode = saved });
        backend.FailApplyAt.UnionWith([1, 2]);
        backend.PartialLiveAt.Add(1);
        backend.PartialSaveAt.Add(1);
        if (changedLive) backend.PartialLiveAt.Add(2);
        if (changedSaved) backend.PartialSaveAt.Add(2);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.True(service.RecoveryPending);
        await service.RevertRecoveryAsync();
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.Equal(saved, backend.Current.Find(Internal.Id)!.SavedMode);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task AForeignSavedModePreventsRecoveryEvenWhenTheLiveModeStillMatches()
    {
        var backend = new Backend(External);
        await using var service = new DisplayOrientationService(backend);
        await LeaveRecoveryPending(service, backend, External);
        var foreign = External.Mode with { Frequency = 30 };
        backend.Current = backend.Current with
        {
            Displays = backend.Current.Displays.Select(d => d with { SavedMode = foreign }).ToArray()
        };
        await service.RevertRecoveryAsync();
        Assert.Single(backend.Applies);
        Assert.Equal(foreign, backend.Current.Find(External.Id)!.SavedMode);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task TheOriginalSavedModeIsNotAnOwnedLiveModeUntilRecoveryActuallyWritesIt()
    {
        var saved = External.Mode with { Frequency = 60 };
        var backend = new Backend(External with { SavedMode = saved });
        await using var service = new DisplayOrientationService(backend);
        await LeaveRecoveryPending(service, backend, External);
        backend.Change(External.Id, saved);
        await service.RevertRecoveryAsync();
        Assert.Single(backend.Applies);
        Assert.Equal(saved, backend.Current.Find(External.Id)!.Mode);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task NewExternalModePreventsObsoleteRecovery()
    {
        var backend = new Backend(External);
        await using var service = new DisplayOrientationService(backend);
        await LeaveRecoveryPending(service, backend, External);
        backend.Change(External.Id, External.Mode.WithOrientation(DisplayOrientation.LandscapeFlipped));
        await service.RevertRecoveryAsync();
        Assert.Single(backend.Applies);
        Assert.Equal(DisplayOrientation.LandscapeFlipped, backend.Current.Find(External.Id)!.Orientation);
        Assert.False(service.RecoveryPending);
        Assert.Equal("Display changed elsewhere; previous settings were not restored", service.MessageKey);
    }

    [Fact]
    public async Task AChangeOnAnotherDisplayAlsoInvalidatesRecovery()
    {
        var backend = new Backend(Internal, External);
        await using var service = new DisplayOrientationService(backend);
        await LeaveRecoveryPending(service, backend, External);
        backend.Change(Internal.Id, Internal.Mode with { Frequency = 60 });
        await service.RevertRecoveryAsync();
        Assert.Single(backend.Applies);
        Assert.Equal(60u, backend.Current.Find(Internal.Id)!.Mode.Frequency);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task ManualUnlockWhileRecoveryIsPendingIsNeverOverwritten()
    {
        var backend = new Backend(Internal);
        await using var service = new DisplayOrientationService(backend);
        await LeaveRecoveryPending(service, backend, Internal);
        backend.Flags(AutoRotationFlags.Enabled);
        await service.RevertRecoveryAsync();
        Assert.Single(backend.Applies);
        Assert.Equal(1, backend.Shortcuts);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task ConcurrentUnlockOfAnAlreadyLockedSurfaceIsNotReportedAsAFixedOrientation()
    {
        var backend = new Backend(Internal);
        backend.Flags(AutoRotationFlags.Disabled);
        backend.AfterApply = _ => backend.Flags(AutoRotationFlags.Enabled);
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.False(service.RecoveryPending);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.Equal(0, backend.Shortcuts);
        Assert.Equal("Windows could not change rotation lock", service.MessageKey);
        Assert.Equal(2, backend.Applies.Count);
    }

    [Fact]
    public async Task ANewModeDuringPersistenceIsNeverOverwrittenByRecovery()
    {
        var backend = new Backend(External);
        backend.AfterApply = call =>
        {
            if (call == 1) backend.Change(External.Id, External.Mode.WithOrientation(DisplayOrientation.LandscapeFlipped));
        };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(External.Id, DisplayOrientation.Portrait);
        Assert.Single(backend.Applies);
        Assert.Equal(DisplayOrientation.LandscapeFlipped, backend.Current.Find(External.Id)!.Orientation);
        Assert.False(service.RecoveryPending);
        Assert.Equal("Windows could not verify the orientation", service.MessageKey);
    }

    [Fact]
    public async Task AnotherDisplayChangingDuringSavedModeRecoveryStopsFurtherWrites()
    {
        var saved = External.Mode with { Frequency = 60 };
        var backend = new Backend(External with { SavedMode = saved }, Internal);
        backend.FailApplyAt.Add(1);
        backend.PartialLiveAt.Add(1);
        backend.PartialSaveAt.Add(1);
        backend.AfterApply = call =>
        {
            if (call == 2) backend.Change(Internal.Id, Internal.Mode with { Frequency = 60 });
        };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(External.Id, DisplayOrientation.Portrait);
        Assert.Equal(2, backend.Applies.Count);
        Assert.Equal(saved, backend.Current.Find(External.Id)!.Mode);
        Assert.Equal(60u, backend.Current.Find(Internal.Id)!.Mode.Frequency);
        Assert.False(service.RecoveryPending);
    }

    [Fact]
    public async Task SourceReassignmentDuringApplyCannotBecomeAValidRecoveryTarget()
    {
        var backend = new Backend(External);
        backend.AfterApply = _ => backend.Current = backend.Current with
        { Displays = backend.Current.Displays.Select(d => d with { SourceId = "another-source" }).ToArray() };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(External.Id, DisplayOrientation.Portrait);
        Assert.False(service.RecoveryPending);
        Assert.Single(backend.Applies);
        Assert.Equal("Windows could not verify the orientation", service.MessageKey);
    }

    [Fact]
    public async Task ReadFailureImmediatelyAfterLockDoesNotLeakAnUntrackedLockChange()
    {
        var backend = new Backend(Internal) { FailCaptureAfterShortcut = true };
        await using var service = new DisplayOrientationService(backend);
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.Portrait);
        Assert.False(backend.Current.Rotation.IsLocked);
        Assert.Empty(backend.Applies);
        Assert.Equal(2, backend.Shortcuts);
    }

    [Fact]
    public async Task PendingRecoveryPreventsNewMutationAndDisablesTheTile()
    {
        var backend = new Backend(Internal);
        await using var service = new DisplayOrientationService(backend);
        using var action = new RotationAction(service);
        await LeaveRecoveryPending(service, backend, Internal);
        await service.ToggleLockAsync();
        await service.ChangeOrientationAsync(Internal.Id, DisplayOrientation.LandscapeFlipped);
        Assert.Single(backend.Applies);
        Assert.Equal(1, backend.Shortcuts);
        Assert.False(action.IsAvailable);
        Assert.Equal("Restore the previous orientation", action.Subtitle);
        Assert.Equal("Restore the previous orientation before making another change", service.MessageKey);
    }

    [Fact]
    public async Task ShutdownStillRetriesRecoveryOfAFailedChange()
    {
        var backend = new Backend(Internal);
        var service = new DisplayOrientationService(backend);
        await LeaveRecoveryPending(service, backend, Internal);
        await service.DisposeAsync();
        Assert.Equal(Internal.Mode, backend.Current.Find(Internal.Id)!.Mode);
        Assert.False(backend.Current.Rotation.IsLocked);
    }

    [Fact]
    public async Task TileTracksRealLockAndAvailabilityAfterExternalChange()
    {
        var backend = new Backend(Internal);
        await using var service = new DisplayOrientationService(backend);
        using var action = new RotationAction(service);
        action.Refresh();
        Assert.True(action.IsAvailable);
        Assert.Equal(ActionState.Off, action.State);
        await action.ExecuteAsync(default);
        Assert.Equal(ActionState.On, action.State);
        backend.Flags(AutoRotationFlags.Disabled | AutoRotationFlags.Laptop);
        action.Refresh();
        Assert.False(action.IsAvailable);
        Assert.Equal(ActionState.On, action.State);
        Assert.Equal("Rotation paused while the keyboard is attached", action.Subtitle);
    }

    private static async Task LeaveRecoveryPending(DisplayOrientationService service, Backend backend, DisplayInfo display)
    {
        backend.AfterApply = call => { if (call == 1) backend.CaptureFailuresRemaining = 2; };
        await service.ChangeOrientationAsync(display.Id, DisplayOrientation.Portrait);
        backend.AfterApply = null;
        Assert.True(service.RecoveryPending);
    }

    private sealed class Backend(params DisplayInfo[] displays) : IDisplayOrientationBackend
    {
        public DisplaySnapshot Current = new(new(true, AutoRotationFlags.Enabled), displays.Select(d => d with { SavedMode = d.SavedMode ?? d.Mode }).ToArray());
        public int Shortcuts;
        public bool SendAccepted = true;
        public bool FailTest;
        public HashSet<int> FailApplyAt = [];
        public HashSet<int> PartialLiveAt = [];
        public HashSet<int> PartialSaveAt = [];
        public HashSet<int> IgnoreApplyAt = [];
        public HashSet<int> IgnoreSaveAt = [];
        public int CaptureFailuresRemaining;
        public bool FailCaptureAfterShortcut;
        public Action? AfterTest;
        public Action<int>? AfterApply;
        public List<(string Id, DisplayMode Mode, bool Persist)> Applies = [];
        public List<string> Operations = [];
        public void Flags(AutoRotationFlags flags) => Current = Current with { Rotation = new(true, flags) };
        public void Change(string id, DisplayMode mode) => Current = Current with
        { Displays = Current.Displays.Select(d => d.Id == id ? d with { Mode = mode } : d).ToArray() };
        private void Save(string id, DisplayMode mode) => Current = Current with
        { Displays = Current.Displays.Select(d => d.Id == id ? d with { SavedMode = mode } : d).ToArray() };
        public DisplaySnapshot Capture()
        {
            if (CaptureFailuresRemaining > 0)
            {
                CaptureFailuresRemaining--;
                throw new DisplayOrientationException("Display configuration unavailable");
            }
            return Current;
        }
        public bool SendRotationLockShortcut()
        {
            Shortcuts++;
            if (!SendAccepted) return false;
            Flags(Current.Rotation.Flags ^ AutoRotationFlags.Disabled);
            Operations.Add(Current.Rotation.IsLocked ? "lock" : "unlock");
            if (FailCaptureAfterShortcut) { CaptureFailuresRemaining = 1; FailCaptureAfterShortcut = false; }
            return true;
        }
        public void TestOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode)
        {
            if (!expected.SameDisplays(Current)) throw new DisplayOrientationException("Display configuration changed; try again");
            if (FailTest) throw new DisplayOrientationException("This orientation is not supported");
            AfterTest?.Invoke();
        }
        public void ApplyAndSaveOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode) =>
            Write(expected, displayId, mode, persist: true);
        public void ApplyOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode) =>
            Write(expected, displayId, mode, persist: false);
        private void Write(DisplaySnapshot expected, string displayId, DisplayMode mode, bool persist)
        {
            TestOrientation(expected, displayId, mode);
            Applies.Add((displayId, mode, persist));
            Operations.Add($"apply:{displayId}:{mode.Orientation}:{persist}");
            int call = Applies.Count;
            if (FailApplyAt.Contains(call))
            {
                if (PartialLiveAt.Contains(call)) Change(displayId, mode);
                if (persist && PartialSaveAt.Contains(call)) Save(displayId, mode);
                AfterApply?.Invoke(call);
                throw new DisplayOrientationException("Windows could not change the orientation");
            }
            if (!IgnoreApplyAt.Contains(call))
            {
                Change(displayId, mode);
                if (persist && !IgnoreSaveAt.Contains(call)) Save(displayId, mode);
            }
            AfterApply?.Invoke(call);
        }
    }

    private sealed class RejectTimersTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("A completed rotation must not schedule a confirmation timer.");
    }
}
