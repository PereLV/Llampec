using System.Text.Json;
using System.Text.Json.Serialization;
using Llampec.Diagnostics;

namespace Llampec.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\Llampec\settings.json.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>UI language tag ("en", "es", "ca") or null to follow Windows.</summary>
    public string? Language { get; set; }

    /// <summary>Global hotkey that opens the panel, e.g. "Ctrl+Alt+Space". Empty disables it.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>
    /// Render with WPF's software rasterizer (no Direct3D device in the process). Lower RAM, but the GDI
    /// presentation path drops the alpha channel so the DWM backdrop is not composed reliably. Off by default.
    /// </summary>
    public bool SoftwareRendering { get; set; }

    /// <summary>Trim the working set after the panel closes.</summary>
    public bool TrimWorkingSetOnClose { get; set; } = true;

    /// <summary>Write a small log file next to settings.json. Off by default; nothing ever leaves the machine.</summary>
    public bool EnableLogFile { get; set; }

    /// <summary>Tile ids in display order. Ids not listed are appended in catalog order; ids in <see cref="HiddenTiles"/> are not shown.</summary>
    public List<string> TileOrder { get; set; } = [];

    public List<string> HiddenTiles { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Loads and saves <see cref="AppSettings"/>. Uses source-generated JSON (no reflection metadata at run time).</summary>
public static class SettingsStore
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Llampec");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public static string LogFilePath { get; } = Path.Combine(Directory, "llampec.log");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using FileStream stream = File.OpenRead(FilePath);
                return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read settings, using defaults: {ex.Message}");
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + ".tmp";
            using (FileStream stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
            }

            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save settings: {ex.Message}");
        }
    }
}
