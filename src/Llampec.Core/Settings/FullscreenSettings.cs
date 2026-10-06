namespace Llampec.Settings;

/// <summary>Shortcut preferences; fullscreen windows belong only to the current session.</summary>
public sealed class FullscreenSettings
{
    public const string DefaultHotkey = "Ctrl+Alt+F";
    private string _hotkey = DefaultHotkey;

    /// <summary>An empty value disables the shortcut.</summary>
    public string Hotkey
    {
        get => _hotkey;
        set => _hotkey = value is not null && string.IsNullOrWhiteSpace(value) ? string.Empty
            : Platform.Hotkey.TryParse(value, out var parsed) ? parsed.ToString() : DefaultHotkey;
    }
}
