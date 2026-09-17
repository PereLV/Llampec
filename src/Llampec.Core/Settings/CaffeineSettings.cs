namespace Llampec.Settings;

/// <summary>Preferences only; active power requests never survive a process restart.</summary>
public sealed class CaffeineSettings
{
    /// <summary>Zero means indefinite; otherwise 1–1440 minutes.</summary>
    public int Minutes { get; set; } = 60;
    public bool KeepDisplayOn { get; set; }
    public static bool IsValid(int minutes) => minutes is >= 0 and <= 1440;
}
