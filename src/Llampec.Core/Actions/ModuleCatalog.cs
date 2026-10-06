namespace Llampec.Actions;

/// <summary>Static presentation metadata; reading it never constructs or starts a module.</summary>
public sealed record ModuleDescriptor(string Id, string Title, string Glyph, string? GlyphBadge, bool HasSubpage);

/// <summary>Every configurable module, including disabled modules without an active action instance.</summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleDescriptor> All { get; } = Array.AsReadOnly<ModuleDescriptor>(
    [
        new("hdr", "HDR", "\uE7A1", null, true),
        new("display-off", "Turn off display", "\uE7F4", null, false),
        new("theme", "Dark mode", "\uE706", "\uE708", true),
        new("projection", "Multiple displays", "\uEBC6", null, true),
        new("taskbar-autohide", "Auto-hide taskbar", "\uE90E", null, false),
        new("touch-taskbar", "Touch taskbar", "\uEBFC", null, false),
        new("caffeine", "Caffeine mode", "\uEC32", null, true),
        new("always-on-top", "Always on Top", "\uE718", null, true),
        new("rotation", "Rotation lock", "\uE755", null, true),
        new("screenshot", "Screenshot", "\uE8A7", null, false),
        new("fullscreen", "Full screen", "\uE740", null, true),
    ]);
}
