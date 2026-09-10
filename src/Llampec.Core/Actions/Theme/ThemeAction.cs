using Llampec.Settings;

namespace Llampec.Actions.Theme;

/// <summary>
/// "Dark mode": switches between the light and dark theme. Pressing the tile always picks an explicit
/// <see cref="AppTheme.Light"/> or <see cref="AppTheme.Dark"/> — like the native Quick Settings toggles,
/// it overrides <see cref="AppTheme.System"/> rather than trying to represent it as a third state.
/// </summary>
/// <remarks>
/// Core has no reference to the WPF-only <c>ThemeManager</c>, so the actual theme is read and applied
/// through delegates supplied from <c>App.xaml.cs</c>: <paramref name="isDark"/> reports the theme that is
/// currently painted on screen, and <paramref name="applyTheme"/> repaints it (and persists the setting)
/// after <see cref="ExecuteCoreAsync"/> flips <paramref name="settings"/>.
/// </remarks>
public sealed class ThemeAction(AppSettings settings, Func<bool> isDark, Action applyTheme) : QuickActionBase
{
    public override string Id => "theme";
    public override string Title => "Dark mode";
    public override string Glyph => "\uE706"; // Brightness (sun)
    public override string? GlyphBadge => "\uE708"; // QuietHours (crescent moon)
    public override ActionKind Kind => ActionKind.Toggle;

    public override void Refresh()
    {
        State = isDark() ? ActionState.On : ActionState.Off;
        IsAvailable = true;
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        settings.Theme = isDark() ? AppTheme.Light : AppTheme.Dark;
        applyTheme();
        return Task.CompletedTask;
    }
}
