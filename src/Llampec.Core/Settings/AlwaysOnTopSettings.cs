namespace Llampec.Settings;

/// <summary>Appearance and shortcut preferences; pinned windows belong only to the current session.</summary>
public sealed class AlwaysOnTopSettings
{
    public const int MinimumBorderThickness = 1;
    public const int MaximumBorderThickness = 8;
    public const int DefaultBorderThickness = 3;
    public const string DefaultHotkey = "Ctrl+Alt+T";

    private int _borderThickness = DefaultBorderThickness;
    private string _hotkey = DefaultHotkey;

    public bool ShowBorder { get; set; } = true;

    /// <summary>Logical pixels, scaled separately for each window's monitor.</summary>
    public int BorderThickness
    {
        get => _borderThickness;
        set => _borderThickness = Math.Clamp(value, MinimumBorderThickness, MaximumBorderThickness);
    }

    /// <summary>An empty value disables the shortcut.</summary>
    public string Hotkey
    {
        get => _hotkey;
        set => _hotkey = value is not null && string.IsNullOrWhiteSpace(value) ? string.Empty
            : Platform.Hotkey.TryParse(value, out var parsed) ? parsed.ToString() : DefaultHotkey;
    }
}
