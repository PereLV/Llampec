namespace Llampec.Interop;

/// <summary>Desktop Window Manager attributes (dwmapi.h). Windows 11 22H2+ values only.</summary>
public static partial class Dwm
{
    public const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const uint DWMWA_BORDER_COLOR = 34;
    public const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    public const int DWMSBT_AUTO = 0;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;      // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic (what flyouts such as Quick Settings use)
    public const int DWMSBT_TABBEDWINDOW = 4;    // Mica Alt

    /// <summary>Special value for DWMWA_BORDER_COLOR that removes the border.</summary>
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, in int pvAttribute, uint cbAttribute);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, in uint pvAttribute, uint cbAttribute);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetColorizationColor(out uint pcrColorization, [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend);

    public static void SetInt(nint hwnd, uint attribute, int value) =>
        _ = DwmSetWindowAttribute(hwnd, attribute, in value, sizeof(int));

    public static void SetUInt(nint hwnd, uint attribute, uint value) =>
        _ = DwmSetWindowAttribute(hwnd, attribute, in value, sizeof(uint));
}
