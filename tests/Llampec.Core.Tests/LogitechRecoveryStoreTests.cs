using System.Text.Json;
using Llampec.Devices.Logitech;
using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public class LogitechRecoveryStoreTests
{
    [Fact]
    public void Journal_roundtrip_preserves_physical_identity_and_every_restore_field()
    {
        using var files = new TemporaryJournal();
        var original = CompleteJournal();

        files.Store.Save(original);
        var restored = Assert.IsType<LogitechRecoveryJournal>(files.Store.Load());

        Assert.Equal(1, restored.Version);
        Assert.Equal(original.Identity, restored.Identity);
        Assert.Equal((ushort)1000, restored.State.Dpi);
        Assert.Equal((byte)2, restored.State.SmartMode);
        Assert.Equal((byte)17, restored.State.SmartThreshold);
        Assert.False(restored.State.VerticalInvert);
        Assert.True(restored.State.HorizontalInvert);
        Assert.Equal(original.State.Controls, restored.State.Controls);
        Assert.False(File.Exists(files.Path + ".tmp"));

        using var json = JsonDocument.Parse(File.ReadAllBytes(files.Path));
        Assert.Equal("1122AABB", json.RootElement.GetProperty("identity").GetProperty("unitId").GetString());
        Assert.Equal(195, json.RootElement.GetProperty("state").GetProperty("controls")[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void Save_replaces_previous_journal_and_clear_is_idempotent()
    {
        using var files = new TemporaryJournal();
        Assert.Null(files.Store.Load());
        files.Store.Save(CompleteJournal());
        var replacement = new LogitechRecoveryJournal(1,
            new(@"\\?\hid#another-device", 0xB034, 255, null, null),
            new(null, null, null, true, null, []));

        files.Store.Save(replacement);
        var loaded = Assert.IsType<LogitechRecoveryJournal>(files.Store.Load());

        Assert.Equal(replacement.Identity, loaded.Identity);
        Assert.Null(loaded.State.Dpi);
        Assert.Null(loaded.State.SmartMode);
        Assert.Null(loaded.State.SmartThreshold);
        Assert.True(loaded.State.VerticalInvert);
        Assert.Null(loaded.State.HorizontalInvert);
        Assert.Empty(loaded.State.Controls);
        Assert.False(File.Exists(files.Path + ".tmp"));
        files.Store.Clear();
        Assert.False(File.Exists(files.Path));
        Assert.Null(files.Store.Load());
        files.Store.Clear();
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"identity\":null,\"state\":null}")]
    [InlineData("{\"version\":1,\"identity\":{\"path\":\"\",\"productId\":45108,\"deviceIndex\":255},\"state\":{\"controls\":[]}}")]
    public void Invalid_journal_is_rejected_without_removing_or_rewriting_the_evidence(string content)
    {
        using var files = new TemporaryJournal();
        Directory.CreateDirectory(files.Directory);
        File.WriteAllText(files.Path, content);
        byte[] originalBytes = File.ReadAllBytes(files.Path);

        Exception? error = Record.Exception(() => files.Store.Load());

        Assert.True(error is JsonException or InvalidDataException, error?.ToString());
        Assert.Equal(originalBytes, File.ReadAllBytes(files.Path));
        Assert.False(File.Exists(files.Path + ".tmp"));
    }

    [Fact]
    public void Receiver_journal_without_a_physical_unit_id_is_rejected_and_retained()
    {
        using var files = new TemporaryJournal();
        var missingIdentity = CompleteJournal() with
        {
            Identity = new(@"\\?\hid#receiver", 0xC548, 1, "receiver-serial", null),
        };
        files.Store.Save(missingIdentity);
        byte[] originalBytes = File.ReadAllBytes(files.Path);

        Assert.Throws<InvalidDataException>(() => files.Store.Load());

        Assert.Equal(originalBytes, File.ReadAllBytes(files.Path));
    }

    [Theory]
    [InlineData("{\"dpi\":0,\"controls\":[]}")]
    [InlineData("{\"smartMode\":3,\"controls\":[]}")]
    [InlineData("{\"smartThreshold\":0,\"controls\":[]}")]
    [InlineData("{\"controls\":null}")]
    [InlineData("{\"controls\":[null]}")]
    [InlineData("{\"controls\":[{\"id\":0,\"diverted\":false}]}")]
    [InlineData("{\"controls\":[{\"id\":195},{\"id\":195}]}")]
    public void Invalid_restore_fields_are_rejected_before_opening_any_device(string stateJson)
    {
        using var files = new TemporaryJournal();
        Directory.CreateDirectory(files.Directory);
        string json = "{\"version\":1,\"identity\":{\"path\":\"mouse\",\"productId\":45108,\"deviceIndex\":255},\"state\":" + stateJson + "}";
        File.WriteAllText(files.Path, json);

        Assert.Throws<InvalidDataException>(() => files.Store.Load());

        Assert.Equal(json, File.ReadAllText(files.Path));
    }

    [Theory]
    [InlineData(0, "1122AABB")]
    [InlineData(7, "1122AABB")]
    [InlineData(3, "bad")]
    [InlineData(255, "1122ZZZZ")]
    public void Invalid_recovery_identity_is_rejected_and_retained(int slot, string unitId)
    {
        using var files = new TemporaryJournal();
        var journal = CompleteJournal();
        files.Store.Save(journal with { Identity = journal.Identity with { DeviceIndex = (byte)slot, UnitId = unitId } });
        byte[] bytes = File.ReadAllBytes(files.Path);

        Assert.Throws<InvalidDataException>(() => files.Store.Load());

        Assert.Equal(bytes, File.ReadAllBytes(files.Path));
    }

    [Fact]
    public void Disabling_remains_valid_even_when_saved_optional_preferences_need_repair()
    {
        var preferences = new LogitechMouseSettings
        {
            Enabled = false, DeviceIndex = 0, UnitId = "bad", Dpi = 0, WheelMode = "bad",
            SmartShiftThreshold = 0, ButtonShortcuts = new() { [0] = "not a shortcut" },
        };

        preferences.Validate();

        var enabled = preferences.Clone();
        enabled.Enabled = true;
        Assert.Throws<ArgumentException>(enabled.Validate);
        Assert.Equal(preferences.ButtonShortcuts, enabled.ButtonShortcuts);
    }

    [Fact]
    public void Null_json_shortcut_values_load_as_original_actions_and_clones_own_their_dictionary()
    {
        var settings = JsonSerializer.Deserialize("{\"logitech\":{\"buttonShortcuts\":{\"195\":null}}}",
            SettingsJsonContext.Default.AppSettings)!;

        Assert.Equal("", settings.Logitech.ButtonShortcuts[195]);
        var clone = settings.Logitech.Clone();
        clone.ButtonShortcuts[195] = "Win+Tab";
        Assert.Equal("", settings.Logitech.ButtonShortcuts[195]);
    }

    [Fact]
    public void Source_generated_app_settings_roundtrip_preserves_mouse_selection_and_numeric_control_keys()
    {
        var preferences = new LogitechMouseSettings
        {
            Enabled = true,
            DevicePath = @"\\?\hid#vid_046d&pid_c548#receiver",
            ProductId = 0xC548,
            SerialNumber = "receiver-serial",
            UnitId = "1122AABB",
            DeviceIndex = 3,
            Dpi = 1600,
            WheelMode = "auto",
            SmartShiftThreshold = 32,
            InvertVertical = true,
            InvertHorizontal = false,
            ButtonShortcuts = new() { [0x00C3] = "Win+Tab", [0x0053] = "Ctrl+Alt+T", [0x0056] = "" },
        };
        var settings = new AppSettings { Logitech = preferences };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!.Logitech;

        restored.Validate();
        Assert.True(restored.Enabled);
        Assert.Equal(preferences.DevicePath, restored.DevicePath);
        Assert.Equal(preferences.ProductId, restored.ProductId);
        Assert.Equal(preferences.SerialNumber, restored.SerialNumber);
        Assert.Equal(preferences.UnitId, restored.UnitId);
        Assert.Equal(preferences.DeviceIndex, restored.DeviceIndex);
        Assert.Equal(preferences.Dpi, restored.Dpi);
        Assert.Equal("auto", restored.WheelMode);
        Assert.Equal((byte)32, restored.SmartShiftThreshold);
        Assert.True(restored.InvertVertical);
        Assert.False(restored.InvertHorizontal);
        Assert.Equal(preferences.ButtonShortcuts.OrderBy(pair => pair.Key), restored.ButtonShortcuts.OrderBy(pair => pair.Key));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Win+Tab", document.RootElement.GetProperty("logitech").GetProperty("buttonShortcuts").GetProperty("195").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"logitech\":null}")]
    [InlineData("{\"logitech\":{\"buttonShortcuts\":null}}")]
    public void Older_or_null_mouse_settings_load_disabled_with_a_usable_shortcut_dictionary(string json)
    {
        var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;

        Assert.NotNull(settings.Logitech);
        Assert.False(settings.Logitech.Enabled);
        Assert.Equal((byte)255, settings.Logitech.DeviceIndex);
        Assert.Empty(settings.Logitech.ButtonShortcuts);
        settings.Logitech.Validate();
    }

    private static LogitechRecoveryJournal CompleteJournal() => new(1,
        new(@"\\?\hid#vid_046d&pid_c548#receiver", 0xC548, 3, "receiver-serial", "1122AABB"),
        new(1000, 2, 17, false, true,
            [new LogitechControlRestoreState(0x00C3, false), new LogitechControlRestoreState(0x0053, true)]));

    private sealed class TemporaryJournal : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Llampec-recovery-test-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public LogitechRecoveryStore Store { get; }
        public TemporaryJournal()
        {
            Path = System.IO.Path.Combine(Directory, "logitech-recovery.json");
            Store = new(Path);
        }
        public void Dispose()
        {
            // Only remove the two explicitly created files and this empty test folder.
            File.Delete(Path);
            File.Delete(Path + ".tmp");
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory);
        }
    }
}
