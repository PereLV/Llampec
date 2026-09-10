using System.Windows;
using System.Windows.Media;
using Llampec.Platform;
using Llampec.Settings;

namespace Llampec;

/// <summary>
/// Applies the light or dark resource dictionary and the system accent colour, and follows Windows
/// theme changes (WM_SETTINGCHANGE "ImmersiveColorSet") when the setting is <see cref="AppTheme.System"/>.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private static readonly Uri LightUri = new("Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("Themes/Dark.xaml", UriKind.Relative);

    private readonly Application _app;
    private readonly SystemEvents _events;
    private readonly AppSettings _settings;
    private bool? _appliedDark; // null until the first Apply(); forces that first call to always take effect
    private uint _appliedAccentArgb;

    public ThemeManager(Application app, SystemEvents events, AppSettings settings)
    {
        _app = app;
        _events = events;
        _settings = settings;
        _events.SettingChanged += OnSettingChanged;
        Apply();
    }

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Apply()
    {
        bool dark = _settings.Theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => !SystemTheme.IsAppsLightTheme(),
        };
        uint argb = SystemTheme.GetAccentColorArgb();

        // WM_SETTINGCHANGE fires for lots of unrelated settings too (fonts, mouse, ...) whenever the
        // broadcast's section is null, and now that SystemEvents' window actually receives broadcasts
        // (see SystemEvents.cs), that means Apply() runs far more often than the theme actually changes.
        // Skip the resource-dictionary swap and the ThemeChanged broadcast (which repaints the flyout's
        // backdrop via DWM calls) when neither dark/light nor the accent colour actually moved.
        if (_appliedDark == dark && _appliedAccentArgb == argb)
        {
            return;
        }

        if (_appliedDark != dark)
        {
            var merged = _app.Resources.MergedDictionaries;
            merged[0] = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        }

        var accent = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        SetBrush("AccentFillBrush", accent);
        // Fluent uses lighter/darker accent variants for hover/pressed on accent tiles.
        SetBrush("AccentFillSecondaryBrush", WithOpacity(accent, 0.90));
        SetBrush("AccentFillTertiaryBrush", WithOpacity(accent, 0.80));
        SetBrush("AccentTextBrush", dark ? Lighten(accent, 0.35) : Darken(accent, 0.15));

        IsDark = dark;
        _appliedDark = dark;
        _appliedAccentArgb = argb;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _app.Resources[key] = brush;
    }

    private static Color WithOpacity(Color c, double opacity) => Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B);

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));

    private void OnSettingChanged(object? sender, string? section)
    {
        if (section is "ImmersiveColorSet" or null)
        {
            Apply();
        }
    }

    public void Dispose() => _events.SettingChanged -= OnSettingChanged;
}
