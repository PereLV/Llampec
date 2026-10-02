using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Llampec.Flyout;

/// <summary>Fluent glyphs and matching monochrome vectors, with a stable tile footprint.</summary>
internal static class ActionIcons
{
    private const double TileSize = 20;
    private const double TileFrameSize = 24;

    internal static FontIcon Glyph(string glyph, double size = TileSize) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    internal static FrameworkElement Tile(string id, string glyph, string? badge)
    {
        var frame = new Grid
        {
            Width = TileFrameSize, Height = TileFrameSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        FrameworkElement icon = id switch
        {
            "display-off" => DisplayOff(),
            "screenshot" => Screenshot(),
            _ => Glyph(glyph),
        };
        frame.Children.Add(icon);
        if (!string.IsNullOrEmpty(badge))
        {
            // Keep the sun/moon composition within the same footprint as other
            // tiles. The previous external margin moved its optical centre.
            if (icon is FontIcon font) font.FontSize = 16;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Top;
            var overlay = Glyph(badge, 12);
            overlay.HorizontalAlignment = HorizontalAlignment.Right;
            overlay.VerticalAlignment = VerticalAlignment.Bottom;
            frame.Children.Add(overlay);
        }
        return frame;
    }

    private static Viewbox Vector(string data) => new()
    {
        Width = TileSize, Height = TileSize,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        Stretch = Stretch.Uniform,
        // PathIcon does not scale its geometry to Width/Height. Keep the whole
        // 24-unit drawing inside its native viewport, then scale the viewport;
        // constraining PathIcon itself to 20 cuts off the monitor's right edge
        // and stand (and the ends of the screenshot's plus).
        Child = new PathIcon
        {
            Width = 24, Height = 24,
            IsHitTestVisible = false,
            // Inherits the button foreground, including accent, disabled and
            // high-contrast states. No bitmap or fixed colour is needed.
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
        },
    };

    private static Viewbox Screenshot() => Vector(
        // Three rounded corners, one long dash on each edge, and the plus.
        // The 1.5-unit contours match the lighter Fluent glyphs. Wide gaps
        // replace the twelve tightly packed dots at small display sizes.
        "M 2.25,6 A 3.75,3.75 0 0 1 6,2.25 A .75,.75 0 0 1 6,3.75 " +
        "A 2.25,2.25 0 0 0 3.75,6 A .75,.75 0 0 1 2.25,6 Z " +
        "M 16,2.25 A 3.75,3.75 0 0 1 19.75,6 A .75,.75 0 0 1 18.25,6 " +
        "A 2.25,2.25 0 0 0 16,3.75 A .75,.75 0 0 1 16,2.25 Z " +
        "M 2.25,16 A .75,.75 0 0 1 3.75,16 A 2.25,2.25 0 0 0 6,18.25 " +
        "A .75,.75 0 0 1 6,19.75 A 3.75,3.75 0 0 1 2.25,16 Z " +
        "M 9.25,2.25 H 12.75 A .75,.75 0 0 1 12.75,3.75 H 9.25 A .75,.75 0 0 1 9.25,2.25 Z " +
        "M 2.25,9.25 A .75,.75 0 0 1 3.75,9.25 V 12.75 A .75,.75 0 0 1 2.25,12.75 Z " +
        "M 9.25,18.25 H 12.25 A .75,.75 0 0 1 12.25,19.75 H 9.25 A .75,.75 0 0 1 9.25,18.25 Z " +
        "M 18.25,9.25 A .75,.75 0 0 1 19.75,9.25 V 12.25 A .75,.75 0 0 1 18.25,12.25 Z " +
        "M 18.25,16 A .75,.75 0 0 1 19.75,16 V 18.25 H 22 A .75,.75 0 0 1 22,19.75 " +
        "H 19.75 V 22 A .75,.75 0 0 1 18.25,22 V 19.75 H 16 A .75,.75 0 0 1 16,18.25 H 18.25 Z");

    private static Viewbox DisplayOff() => Vector(
        // Rounded monitor, balanced stand and an inset power symbol, all with
        // the same 1.5-unit weight as the selection-frame icon.
        "F0 M 4,3 H 20 A 2,2 0 0 1 22,5 V 16 A 2,2 0 0 1 20,18 H 12.75 V 20.5 " +
        "H 16.25 A .75,.75 0 0 1 16.25,22 H 7.75 A .75,.75 0 0 1 7.75,20.5 H 11.25 V 18 " +
        "H 4 A 2,2 0 0 1 2,16 V 5 A 2,2 0 0 1 4,3 Z " +
        "M 4,4.5 A .5,.5 0 0 0 3.5,5 V 16 A .5,.5 0 0 0 4,16.5 H 20 " +
        "A .5,.5 0 0 0 20.5,16 V 5 A .5,.5 0 0 0 20,4.5 Z " +
        "M 11.25,6.75 A .75,.75 0 0 1 12.75,6.75 V 10 A .75,.75 0 0 1 11.25,10 Z " +
        "M 9.3,7.5 A 4,4 0 1 0 14.7,7.5 L 13.7,8.625 A 2.5,2.5 0 1 1 10.3,8.625 Z");
}
