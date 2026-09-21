using System.Text.Json;
using System.Text.Json.Serialization;
using Llampec.Settings;

namespace Llampec.Devices.Logitech;

internal sealed record LogitechRecoveryJournal(int Version, LogitechMouseIdentity Identity, LogitechRestoreState State);

internal interface ILogitechRecoveryStore
{
    LogitechRecoveryJournal? Load();
    void Save(LogitechRecoveryJournal journal);
    void Clear();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LogitechRecoveryJournal))]
internal sealed partial class LogitechRecoveryJsonContext : JsonSerializerContext;

internal sealed class LogitechRecoveryStore(string path) : ILogitechRecoveryStore
{
    public static string DefaultPath => Path.Combine(SettingsStore.Directory, "logitech-recovery.json");

    public LogitechRecoveryJournal? Load()
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var journal = JsonSerializer.Deserialize(stream, LogitechRecoveryJsonContext.Default.LogitechRecoveryJournal)
            ?? throw new InvalidDataException("The Logitech recovery journal is empty.");
        if (journal.Version != 1 || journal.Identity is null || string.IsNullOrWhiteSpace(journal.Identity.Path)
            || journal.Identity.ProductId == 0 || journal.State is null)
            throw new InvalidDataException("The Logitech recovery journal is not supported; no device settings were changed.");
        var identity = journal.Identity;
        if (identity.DeviceIndex != 0xFF && identity.DeviceIndex is not (>= 1 and <= 6))
            throw new InvalidDataException("The Logitech recovery receiver slot is invalid.");
        if ((!string.IsNullOrEmpty(identity.UnitId) && (identity.UnitId.Length != 8 || !identity.UnitId.All(char.IsAsciiHexDigit)))
            || (identity.DeviceIndex != 0xFF && string.IsNullOrEmpty(identity.UnitId)))
            throw new InvalidDataException("Receiver recovery requires the original valid mouse unit ID; no settings were changed.");
        var state = journal.State;
        if (state.Dpi == 0 || state.SmartMode is not (null or 1 or 2) || state.SmartThreshold == 0 || state.Controls is null)
            throw new InvalidDataException("The Logitech recovery settings are invalid; no device settings were changed.");
        var controls = new HashSet<ushort>();
        if (state.Controls.Any(control => control is null || control.Id == 0 || !controls.Add(control.Id)))
            throw new InvalidDataException("The Logitech recovery controls are invalid or duplicated; no device settings were changed.");
        return journal;
    }

    public void Save(LogitechRecoveryJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, journal, LogitechRecoveryJsonContext.Default.LogitechRecoveryJournal);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public void Clear() => File.Delete(path);
}
