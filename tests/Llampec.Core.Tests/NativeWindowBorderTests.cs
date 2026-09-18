using System.Runtime.InteropServices;
using Llampec.Actions.AlwaysOnTop;
using Llampec.Interop;
using Xunit;

namespace Llampec.Tests;

/// <summary>Checks the actual GDI mask, including the corner cutout that used to remain square.</summary>
public sealed class NativeWindowBorderTests
{
    [Theory]
    [InlineData(4, 3)]
    [InlineData(8, 3)]
    [InlineData(20, 8)]
    public void RoundedOutlineHasConcentricInnerAndOuterArcs(int radius, int thickness)
    {
        using var shape = new Region(NativeWindowBorder.CreateHollowRegion(240, 160, thickness, radius));
        Assert.NotEqual(0, shape.Handle);
        Assert.False(Contains(shape.Handle, 0, 0));

        int center = thickness + radius;
        int arc = (int)Math.Round(center - (radius + thickness / 2.0) / Math.Sqrt(2));
        // This point lies inside the target's bounding rectangle but outside its
        // curved corner: the previous rectangular hole incorrectly excluded it.
        Assert.True(arc >= thickness);
        Assert.True(Contains(shape.Handle, arc, arc));
        Assert.False(Contains(shape.Handle, center, center));
        AssertUniformStraightSides(shape.Handle, thickness);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void SquareOutlineKeepsSquareCornersAndAnEmptyInterior(int thickness)
    {
        using var shape = new Region(NativeWindowBorder.CreateHollowRegion(240, 160, thickness, 0));
        Assert.True(Contains(shape.Handle, 0, 0));
        Assert.True(Contains(shape.Handle, 239, 159));
        Assert.False(Contains(shape.Handle, thickness, thickness));
        AssertUniformStraightSides(shape.Handle, thickness);
    }

    [Fact]
    public void OversizedStrokeDoesNotBecomeAnOpaqueWindow()
    {
        Assert.Equal(0, NativeWindowBorder.CreateHollowRegion(10, 10, 5, 8));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(12, 2)]
    [InlineData(1, 3)]
    [InlineData(3, 0)]
    public void StrokeTouchesTheWindowInsideItsNativeFrameOnEverySide(int thickness, int nativeBorder)
    {
        // Includes the reported 150% DPI / two-physical-pixel DWM frame, a stroke
        // thinner than the native frame, and a frameless window with no padding.
        var frame = new User32.RECT { Left = 100, Top = 200, Right = 340, Bottom = 360 };
        var geometry = NativeWindowBorder.CalculateOutlineGeometry(frame, thickness, 12, (uint)nativeBorder, false);
        using var shape = new Region(NativeWindowBorder.CreateHollowRegion(
            geometry.Bounds.Width, geometry.Bounds.Height, thickness, geometry.InnerCornerRadius));
        bool At(int x, int y) => Contains(shape.Handle, x - geometry.Bounds.Left, y - geometry.Bounds.Top);

        // The innermost native-frame pixel is colored; the first content pixel is
        // clear. A wholly exterior outline passes strip-width tests but fails here.
        Assert.True(At(100 + nativeBorder - 1, 280));
        Assert.False(At(100 + nativeBorder, 280));
        Assert.True(At(340 - nativeBorder, 280));
        Assert.False(At(340 - nativeBorder - 1, 280));
        Assert.True(At(220, 200 + nativeBorder - 1));
        Assert.False(At(220, 200 + nativeBorder));
        Assert.True(At(220, 360 - nativeBorder));
        Assert.False(At(220, 360 - nativeBorder - 1));
        Assert.False(At(220, 280));

        // Insetting a rounded contour must retain its center, rather than moving
        // the curve away from the target when its native frame width changes.
        Assert.Equal(112, geometry.Bounds.Left + thickness + geometry.InnerCornerRadius);
        Assert.Equal(212, geometry.Bounds.Top + thickness + geometry.InnerCornerRadius);
    }

    [Fact]
    public void MaximizedOutlineDoesNotApplyNativeFrameOverlapTwice()
    {
        var frame = new User32.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1040 };
        var geometry = NativeWindowBorder.CalculateOutlineGeometry(frame, 4, 12, 2, true);
        Assert.Equal(frame.Left, geometry.Bounds.Left);
        Assert.Equal(frame.Top, geometry.Bounds.Top);
        Assert.Equal(frame.Right, geometry.Bounds.Right);
        Assert.Equal(frame.Bottom, geometry.Bounds.Bottom);
        Assert.Equal(0, geometry.InnerCornerRadius);
        using var shape = new Region(NativeWindowBorder.CreateHollowRegion(
            geometry.Bounds.Width, geometry.Bounds.Height, 4, geometry.InnerCornerRadius));
        Assert.True(Contains(shape.Handle, 0, 0));
        Assert.False(Contains(shape.Handle, 4, 4));
    }

    private static void AssertUniformStraightSides(nint shape, int thickness)
    {
        Assert.False(Contains(shape, 120, 80));
        for (int offset = 0; offset < thickness; offset++)
        {
            Assert.True(Contains(shape, offset, 80));
            Assert.True(Contains(shape, 239 - offset, 80));
            Assert.True(Contains(shape, 120, offset));
            Assert.True(Contains(shape, 120, 159 - offset));
        }
        Assert.False(Contains(shape, thickness, 80));
        Assert.False(Contains(shape, 239 - thickness, 80));
        Assert.False(Contains(shape, 120, thickness));
        Assert.False(Contains(shape, 120, 159 - thickness));
    }

    private sealed class Region(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;
        public void Dispose() { if (Handle != 0) DeleteObject(Handle); }
    }

    [DllImport("gdi32.dll", EntryPoint = "PtInRegion")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Contains(nint region, int x, int y);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint region);
}
