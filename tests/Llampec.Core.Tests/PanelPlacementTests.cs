using Llampec.Interop;
using Llampec.Platform;
using Xunit;
using static Llampec.Platform.PanelPlacement;

namespace Llampec.Tests;

public sealed class PanelPlacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidatedButtonHeightOverridesBothPaddedHostAndPrimary(bool hidden)
    {
        var monitor = Rect(0, 0, 2880, 1920);
        var primary = Rect(0, 1776, 2880, 1920);
        var host = hidden ? Rect(0, 1918, 2880, 2062) : primary;
        var buttons = hidden ? Rect(618, 1966, 1498, 2062) : Rect(618, 1824, 1498, 1920);
        TaskbarBounds selected = SelectTaskbarBounds(host, primary, buttons, TaskbarEdge.Bottom, 2);
        Assert.Equal(96, selected.Bounds.Height);
        Assert.Equal(host.Bottom, selected.Bounds.Bottom);
        AssertRect(Rect(0, 0, 2880, 1824), ReserveTaskbarArea(monitor, monitor, [selected], 2));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(112)]
    [InlineData(160)]
    public void ButtonThicknessIsMeasuredRatherThanAFixedPaddingDeduction(int thickness)
    {
        var host = Rect(0, 1728, 2880, 1920);
        var buttons = Rect(600, 1920 - thickness, 1600, 1920);
        TaskbarBounds selected = SelectTaskbarBounds(host, host, buttons, TaskbarEdge.Bottom, 2);
        Assert.Equal(thickness, selected.Bounds.Height);
    }

    [Fact]
    public void MissingOrImplausibleButtonGeometryKeepsTheConservativeHost()
    {
        var host = Rect(0, 1776, 2880, 1920);
        User32.RECT?[] candidates =
        [
            null,
            Rect(0, 0, 0, 0),
            Rect(-1, 1824, 1000, 1920), // Outside the host.
            Rect(600, 1872, 1600, 1920), // Only 24 DIP: could be collapsed.
            Rect(600, 1776, 1600, 1872), // Anchored to the opposite edge.
            Rect(600, 1824, 620, 1920), // Does not resemble a horizontal button container.
            Rect(600, 1728, 1600, 1920)  // Thicker than the host.
        ];
        foreach (var candidate in candidates)
            AssertRect(host, SelectTaskbarBounds(host, host, candidate, TaskbarEdge.Bottom, 2).Bounds);
    }

    [Fact]
    public void ExpandedPrimaryGeometryRemainsTheFallbackForACollapsedHost()
    {
        var host = Rect(0, 1918, 2880, 1920);
        var primary = Rect(0, 1776, 2880, 1920);
        AssertRect(primary, SelectTaskbarBounds(host, primary, null, TaskbarEdge.Bottom, 2).Bounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MeasuredButtonsRemovePaddingAtOnlyTheOwningEdge(int edgeValue)
    {
        var monitor = Rect(-1200, -1200, 0, 0);
        var edge = (TaskbarEdge)edgeValue;
        var host = edge switch
        {
            TaskbarEdge.Left => Rect(-1200, -1200, -1056, 0),
            TaskbarEdge.Top => Rect(-1200, -1200, 0, -1056),
            TaskbarEdge.Right => Rect(-144, -1200, 0, 0),
            _ => Rect(-1200, -144, 0, 0)
        };
        var buttons = edge switch
        {
            TaskbarEdge.Left => Rect(-1200, -1000, -1104, -200),
            TaskbarEdge.Top => Rect(-1000, -1200, -200, -1104),
            TaskbarEdge.Right => Rect(-96, -1000, 0, -200),
            _ => Rect(-1000, -96, -200, 0)
        };
        var expected = edge switch
        {
            TaskbarEdge.Left => Rect(-1104, -1200, 0, 0),
            TaskbarEdge.Top => Rect(-1200, -1104, 0, 0),
            TaskbarEdge.Right => Rect(-1200, -1200, -96, 0),
            _ => Rect(-1200, -1200, 0, -96)
        };
        TaskbarBounds selected = SelectTaskbarBounds(host, host, buttons, edge, 2);
        AssertRect(expected, ReserveTaskbarArea(monitor, monitor, [selected], 2));
    }

    [Fact]
    public void HiddenConventionalTaskbarReservesItsFullHeight()
    {
        var monitor = Rect(0, 0, 1920, 1080);
        var bar = new TaskbarBounds(Rect(0, 1079, 1920, 1127), TaskbarEdge.Bottom);
        AssertRect(Rect(0, 0, 1920, 1032), Reserve(monitor, monitor, bar));
    }

    [Fact]
    public void AlreadyReservedWorkAreaIsNotReducedTwice()
    {
        var monitor = Rect(0, 0, 1920, 1080);
        var work = Rect(0, 0, 1920, 1032);
        var bar = new TaskbarBounds(Rect(0, 1032, 1920, 1080));
        AssertRect(work, Reserve(monitor, work, bar));
        AssertRect(work, Reserve(monitor, work, bar, bar));
    }

    [Fact]
    public void LargerExistingAppbarReservationIsPreserved()
    {
        var monitor = Rect(0, 0, 1920, 1080);
        var work = Rect(0, 0, 1920, 900);
        var bar = new TaskbarBounds(Rect(0, 1079, 1920, 1127), TaskbarEdge.Bottom);
        AssertRect(work, Reserve(monitor, work, bar));
    }

    [Fact]
    public void ExpandedTaskbarGeometryWinsOverItsCollapsedRepresentation()
    {
        var monitor = Rect(-2160, -3840, 0, 0);
        var work = Rect(-2160, -3840, 0, -48);
        var collapsed = new TaskbarBounds(Rect(-2160, -48, 0, 0), TaskbarEdge.Bottom);
        var expanded = new TaskbarBounds(Rect(-2160, -160, 0, 0), TaskbarEdge.Bottom);
        AssertRect(Rect(-2160, -3840, 0, -160),
            ReserveTaskbarArea(monitor, work, [collapsed, expanded], scale: 2));
        AssertRect(Rect(-2160, -3840, 0, -160),
            ReserveTaskbarArea(monitor, work, [expanded, collapsed], scale: 2));
    }

    [Theory]
    [InlineData(93)]
    [InlineData(96)]
    public void ConventionalExpandedBarUsesItsMeasuredHeightAtHighDpi(int height)
    {
        var monitor = Rect(0, 0, 3840, 2160);
        var expanded = new TaskbarBounds(Rect(0, 2160 - height, 3840, 2160), TaskbarEdge.Bottom);
        var hiddenStrip = new TaskbarBounds(Rect(0, 2159, 3840, 2161), TaskbarEdge.Bottom);
        var expected = Rect(0, 0, 3840, 2160 - height);
        // A touch-taskbar preference must not turn an attached-keyboard 48-DIP bar
        // into a guessed 96-DIP bar. An accompanying hidden strip does not change it.
        AssertRect(expected, ReserveTaskbarArea(monitor, monitor, [expanded], scale: 2));
        AssertRect(expected, ReserveTaskbarArea(monitor, monitor, [expanded, hiddenStrip], scale: 2));
        AssertRect(expected, ReserveTaskbarArea(monitor, expected, [expanded, hiddenStrip], scale: 2));
    }

    [Fact]
    public void CollapsedBandAloneDoesNotInferTabletHeightFromAPreference()
    {
        var monitor = Rect(-2160, -3840, 0, 0);
        var work = Rect(-2160, -3840, 0, -48);
        var bar = new TaskbarBounds(Rect(-2160, -48, 0, 0), TaskbarEdge.Bottom);
        AssertRect(Rect(-2160, -3840, 0, -96),
            ReserveTaskbarArea(monitor, work, [bar], scale: 2));
    }

    [Fact]
    public void HiddenThinStripUsesAConventionalFloorOnlyWhenABarExists()
    {
        var monitor = Rect(1920, -200, 3840, 880);
        var bar = new TaskbarBounds(Rect(1920, 879, 3840, 881), TaskbarEdge.Bottom);
        AssertRect(Rect(1920, -200, 3840, 808),
            ReserveTaskbarArea(monitor, monitor, [bar], scale: 1.5));
        AssertRect(monitor, ReserveTaskbarArea(monitor, monitor, [], scale: 1.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryEdgeProjectsHiddenBoundsInsideTheOwningMonitor(int edgeValue)
    {
        var edge = (TaskbarEdge)edgeValue;
        var monitor = Rect(-1600, -200, 0, 700);
        var bar = edge switch
        {
            TaskbarEdge.Left => Rect(-1663, -200, -1599, 700),
            TaskbarEdge.Top => Rect(-1600, -263, 0, -199),
            TaskbarEdge.Right => Rect(-1, -200, 63, 700),
            _ => Rect(-1600, 699, 0, 763)
        };
        var expected = edge switch
        {
            TaskbarEdge.Left => Rect(-1536, -200, 0, 700),
            TaskbarEdge.Top => Rect(-1600, -136, 0, 700),
            TaskbarEdge.Right => Rect(-1600, -200, -64, 700),
            _ => Rect(-1600, -200, 0, 636)
        };
        Assert.Equal(edge, InferEdge(monitor, bar));
        AssertRect(expected, Reserve(monitor, monitor, new TaskbarBounds(bar, edge)));
    }

    [Fact]
    public void SecondaryMonitorBarDoesNotReduceThePrimaryMonitor()
    {
        var primary = Rect(0, 0, 1920, 1080);
        var secondary = Rect(-1920, 0, 0, 1080);
        var bar = new TaskbarBounds(Rect(-1920, 1032, 0, 1080));
        Assert.Null(InferEdge(primary, bar.Bounds));
        AssertRect(primary, Reserve(primary, primary, bar));
        AssertRect(Rect(-1920, 0, 0, 1032), Reserve(secondary, secondary, bar));
    }

    [Fact]
    public void HiddenUpperTaskbarDoesNotBecomeALowerMonitorTopBar()
    {
        var lower = Rect(0, 0, 1920, 1080);
        var hiddenUpperTray = Rect(0, 0, 1920, 48);
        var upperPrimaryBar = Rect(0, -48, 1920, 0);
        Assert.Equal(TaskbarEdge.Top, InferEdge(lower, hiddenUpperTray));
        Assert.Null(SelectUnregisteredEdge(lower, lower, hiddenUpperTray));
        // A valid primary query for the upper display must prevent HWND fallback below,
        // even when an unrelated appbar happens to reserve this lower display's top.
        Assert.Null(SelectUnregisteredEdge(lower, lower, hiddenUpperTray, upperPrimaryBar));
        Assert.Null(SelectUnregisteredEdge(lower, Rect(0, 48, 1920, 1080), hiddenUpperTray, upperPrimaryBar));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NormalNonBottomTaskbarsNeedTheirExistingWorkAreaReservation(int edgeValue)
    {
        var monitor = Rect(-1600, -200, 0, 700);
        var edge = (TaskbarEdge)edgeValue;
        var tray = edge switch
        {
            TaskbarEdge.Left => Rect(-1600, -200, -1552, 700),
            TaskbarEdge.Top => Rect(-1600, -200, 0, -152),
            _ => Rect(-48, -200, 0, 700)
        };
        var work = edge switch
        {
            TaskbarEdge.Left => Rect(-1552, -200, 0, 700),
            TaskbarEdge.Top => Rect(-1600, -152, 0, 700),
            _ => Rect(-1600, -200, -48, 700)
        };
        Assert.Null(SelectUnregisteredEdge(monitor, monitor, tray));
        Assert.Equal(edge, SelectUnregisteredEdge(monitor, work, tray));
        AssertRect(work, Reserve(monitor, work, new TaskbarBounds(tray, edge)));
    }

    [Fact]
    public void RotatedMonitorUsesItsCurrentBottomAndExpandedHeight()
    {
        var monitor = Rect(-1200, -1920, 0, 0);
        var bar = new TaskbarBounds(Rect(-1200, -80, 0, 0));
        AssertRect(Rect(-1200, -1920, 0, -80),
            ReserveTaskbarArea(monitor, monitor, [bar], 1));
    }

    [Fact]
    public void UnrelatedMiddleWindowAndMalformedBoundsAreIgnored()
    {
        var monitor = Rect(0, 0, 1920, 1080);
        Assert.Null(InferEdge(monitor, Rect(200, 300, 900, 500)));
        AssertRect(monitor, Reserve(monitor, monitor,
            new(Rect(200, 300, 900, 500)), new(Rect(0, 0, 0, 0))));
    }

    private static User32.RECT Reserve(User32.RECT monitor, User32.RECT work, params TaskbarBounds[] bars)
        => ReserveTaskbarArea(monitor, work, bars, 1);
    private static User32.RECT Rect(int left, int top, int right, int bottom)
        => new() { Left = left, Top = top, Right = right, Bottom = bottom };
    private static void AssertRect(User32.RECT expected, User32.RECT actual)
        => Assert.Equal((expected.Left, expected.Top, expected.Right, expected.Bottom),
            (actual.Left, actual.Top, actual.Right, actual.Bottom));
}
