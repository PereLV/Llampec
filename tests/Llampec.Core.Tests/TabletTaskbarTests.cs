using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public sealed class TabletTaskbarTests
{
    [Theory]
    [InlineData(2)] // Mobile
    [InlineData(8)] // Slate
    public void TabletOptimizationNeedsAnExplicitEnabledPreference(int role)
    {
        Assert.Equal(TaskbarMode.TabletOptimized, TabletTaskbar.Classify(true, true, role, 1, false));
        Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, true, role, 0, false));
        foreach (object? value in new object?[] { null, 2, -1, "1", 1L, Array.Empty<byte>() })
            Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, role, value, false));
    }

    [Theory]
    [InlineData(0)] // Unspecified
    [InlineData(1)] // Desktop
    [InlineData(2)] // Mobile
    [InlineData(8)] // Slate
    [InlineData(99)] // Future/invalid role
    public void ExplicitlyDisabledOptimizationDoesNotBlockSlateHardware(int role)
    {
        Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, true, role, 0, false));
    }

    [Theory]
    [InlineData(1)] // Desktop
    [InlineData(3)] // Workstation
    [InlineData(4)] // Enterprise server
    [InlineData(5)] // SOHO server
    [InlineData(6)] // Appliance
    [InlineData(7)] // Performance server
    public void DesktopRolesStayNormalEvenWithATouchMonitorAndZeroSlateMetric(int role)
    {
        foreach (object? preference in new object?[] { null, 0, 1, 2 })
        {
            Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, true, role, preference, false));
            Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, false, role, preference, false));
        }
    }

    [Fact]
    public void ConnectedKeyboardOrNoIntegratedTouchDoesNotActivateTabletTaskbar()
    {
        Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(false, true, 8, 1, false));
        Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, false, 2, 1, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void UnrecognizedRoleOnPossibleTabletHardwareIsUnknown(int role)
    {
        Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, role, 1, false));
    }

    [Fact]
    public void UnvalidatedOverridePreventsInferringItsMeaningOrPrecedence()
    {
        Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, 8, 0, true));
        Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, 8, 1, true));
        Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(false, false, 1, null, true));
    }

    [Fact]
    public void RemoteSessionDoesNotInferTabletModeFromLocalHardware()
    {
        Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, 8, 1, false, remoteSession: true));
        Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, true, 8, 0, false, remoteSession: true));
        Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, true, 1, 1, false, remoteSession: true));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void ExplicitConvertibilityOptOutKeepsNormalTaskbarOnTabletHardware(int role)
    {
        foreach (object? preference in new object?[] { null, 0, 1, "1" })
            Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, true, role, preference, false, convertibilityEnabled: 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)] // A DWORD with all bits set is also a nonzero opt-in.
    public void AnyNonzeroDwordOptsInRegardlessOfFallbackDesktopRole(int convertibility)
    {
        Assert.Equal(TaskbarMode.TabletOptimized,
            TabletTaskbar.Classify(true, true, 1, 1, false, convertibilityEnabled: convertibility));
        Assert.Equal(TaskbarMode.Normal,
            TabletTaskbar.Classify(true, true, 1, 0, false, convertibilityEnabled: convertibility));
        Assert.Equal(TaskbarMode.Unknown,
            TabletTaskbar.Classify(true, true, 1, null, false, convertibilityEnabled: convertibility));
    }

    [Fact]
    public void ConvertibilityOptInDoesNotForceTabletModeWithKeyboardAttachedOrWithoutIntegratedTouch()
    {
        Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(false, true, 1, 1, false, convertibilityEnabled: 2));
        Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, false, 1, 1, false, convertibilityEnabled: 2));
    }

    [Fact]
    public void MalformedConvertibilityDoesNotPretendToBeAnOptInOrOptOut()
    {
        foreach (object value in new object[] { "0", "1", 1L, Array.Empty<byte>(), true })
        {
            Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, 1, 1, false, convertibilityEnabled: value));
            Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, 8, 1, false, convertibilityEnabled: value));
            Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(true, true, 8, 0, false, convertibilityEnabled: value));
            Assert.Equal(TaskbarMode.Normal, TabletTaskbar.Classify(false, true, 8, 1, false, convertibilityEnabled: value));
        }
    }

    [Fact]
    public void ConvertibilityDoesNotResolveAnUnvalidatedPostureOverride()
    {
        Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, 8, 1, true, convertibilityEnabled: 0));
        Assert.Equal(TaskbarMode.Unknown, TabletTaskbar.Classify(true, true, 8, 1, true, convertibilityEnabled: 2));
    }
}
