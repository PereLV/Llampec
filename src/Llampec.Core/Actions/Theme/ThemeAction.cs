using Llampec.Platform;

namespace Llampec.Actions.Theme;

/// <summary>
/// "Dark mode": the same switch as Settings &gt; Personalization &gt; Colors, applied to the whole system
/// (not just Llampec's own panel) via <see cref="SystemTheme.SetLightTheme"/>. Llampec's panel repaints
/// itself afterwards through the same WM_SETTINGCHANGE broadcast every other theme-aware app reacts to
/// (see <see cref="SystemEvents"/> and <c>ThemeManager</c>), so this action does not need to know about
/// Llampec's own theme setting at all.
/// </summary>
public sealed class ThemeAction : QuickActionBase
{
    public override string Id => "theme";
    public override string Title => "Dark mode";
    public override string Glyph => "\uE706"; // Brightness (sun)
    public override string? GlyphBadge => "\uE708"; // QuietHours (crescent moon)
    public override ActionKind Kind => ActionKind.Toggle;

    public override void Refresh()
    {
        State = SystemTheme.IsAppsLightTheme() ? ActionState.Off : ActionState.On;
        IsAvailable = true;
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        SystemTheme.SetLightTheme(light: !SystemTheme.IsAppsLightTheme());
        return Task.CompletedTask;
    }
}
