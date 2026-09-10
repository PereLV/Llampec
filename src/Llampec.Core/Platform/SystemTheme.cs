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

    /// <summary>
    /// Sets both the "apps" and "system" colour mode to light or dark -- the same two registry values
    /// Settings &gt; Personalization &gt; Colors' Light/Dark switch writes ("Custom" mode, where the two
    /// differ, is not exposed here) -- then broadcasts WM_SETTINGCHANGE the same way Settings does after an
    /// edit there, so Explorer and every theme-aware app (Llampec's own panel included, through
    /// <see cref="SystemEvents"/>) repaints immediately instead of waiting for the next unrelated broadcast.
    /// </summary>
    public static void SetLightTheme(bool light)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: true)
            ?? throw new InvalidOperationException($@"Registry key HKCU\{PersonalizeKey} not found");

        int value = light ? 1 : 0;
        key.SetValue(AppsUseLightThemeValue, value, RegistryValueKind.DWord);
        key.SetValue(SystemUsesLightThemeValue, value, RegistryValueKind.DWord);

        // SendMessageTimeout, not SendMessage: a hung top-level window elsewhere on the desktop can't
        // block this call forever, and nothing here needs the reply.
        User32.SendMessageTimeout(User32.HWND_BROADCAST, User32.WM_SETTINGCHANGE, 0,
            "ImmersiveColorSet", User32.SMTO_ABORTIFHUNG, 3000, out _);
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
