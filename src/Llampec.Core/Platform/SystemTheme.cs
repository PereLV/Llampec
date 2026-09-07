using Llampec.Interop;
using Microsoft.Win32;

namespace Llampec.Platform;

/// <summary>Reads the current Windows theme and accent colour (registry + DWM; no WinRT projection needed).</summary>
public static class SystemTheme
{
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string AppsUseLightThemeValue = "AppsUseLightTheme";
    public const string SystemUsesLightThemeValue = "SystemUsesLightTheme";

    /// <summary>True when apps should use the light theme (the value the native Quick Settings panel follows).</summary>
    public static bool IsAppsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(AppsUseLightThemeValue) is not int v || v != 0;
    }

    /// <summary>Accent colour as 0xAARRGGBB. Falls back to Windows' default blue.</summary>
    public static uint GetAccentColorArgb()
    {
        // DWM colorization is the accent colour blended for window frames; the raw accent lives under
        // HKCU\Software\Microsoft\Windows\DWM\AccentColor as ABGR. Prefer the registry value.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is int abgr)
        {
            uint v = unchecked((uint)abgr);
            uint a = (v >> 24) & 0xFF, b = (v >> 16) & 0xFF, g = (v >> 8) & 0xFF, r = v & 0xFF;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        if (Dwm.DwmGetColorizationColor(out uint argb, out _) == 0)
        {
            return argb | 0xFF000000;
        }

        return 0xFF0078D4;
    }
}
