using Llampec.Devices.Logitech;
using Llampec.Platform;

namespace Llampec.Settings;

/// <summary>Opt-in preferences for one explicitly selected Logitech mouse interface and receiver slot.</summary>
public sealed class LogitechMouseSettings
{
    public bool Enabled { get; set; }
    public string? DevicePath { get; set; }
    public ushort? ProductId { get; set; }
    public string? SerialNumber { get; set; }
    public string? UnitId { get; set; }
    public byte DeviceIndex { get; set; } = 0xFF;
    public ushort? Dpi { get; set; }
    public string? WheelMode { get; set; }
    public byte SmartShiftThreshold { get; set; } = 25;
    public bool? InvertVertical { get; set; }
    public bool? InvertHorizontal { get; set; }
    private Dictionary<ushort, string> _buttonShortcuts = [];
    public Dictionary<ushort, string> ButtonShortcuts
    {
        get => _buttonShortcuts;
        set => _buttonShortcuts = value?.ToDictionary(pair => pair.Key, pair => pair.Value ?? "") ?? [];
    }

    public LogitechMouseSettings Clone() => new()
    {
        Enabled = Enabled, DevicePath = DevicePath, ProductId = ProductId, SerialNumber = SerialNumber, UnitId = UnitId,
        DeviceIndex = DeviceIndex, Dpi = Dpi, WheelMode = WheelMode, SmartShiftThreshold = SmartShiftThreshold,
        InvertVertical = InvertVertical, InvertHorizontal = InvertHorizontal,
        ButtonShortcuts = ButtonShortcuts,
    };

    public void Validate()
    {
        // Disabling must remain possible even when saved optional values are invalid.
        // Recovery validates its own journal and does not use these preferences.
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(DevicePath) || ProductId is null or 0)
            throw new ArgumentException("Select a Logitech mouse before enabling its settings.");
        if (DeviceIndex != 0xFF && DeviceIndex is not (>= 1 and <= 6))
            throw new ArgumentException("The receiver slot must be 1–6, or 255 for a directly connected mouse.");
        if (!string.IsNullOrEmpty(UnitId) && (UnitId.Length != 8 || !UnitId.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("The mouse unit ID must contain eight hexadecimal digits.");
        if (DeviceIndex != 0xFF && string.IsNullOrEmpty(UnitId))
            throw new ArgumentException("Select a receiver mouse with a stable unit ID before enabling its settings.");
        if (Dpi == 0) throw new ArgumentException("DPI must be greater than zero.");
        if (WheelMode is not (null or "free" or "ratchet" or "auto"))
            throw new ArgumentException("Wheel mode must be free, ratchet, auto, or unspecified.");
        if (WheelMode == "auto" && SmartShiftThreshold is 0 or 255)
            throw new ArgumentException("The automatic wheel threshold must be between 1 and 254.");
        foreach (var pair in ButtonShortcuts)
        {
            if (pair.Key == 0) throw new ArgumentException("A mouse control identifier cannot be zero.");
            if (!string.IsNullOrWhiteSpace(pair.Value)) _ = new KeyboardShortcut(pair.Value);
        }
    }

    internal LogitechMouseIdentity Identity() => new(DevicePath!, ProductId!.Value, DeviceIndex, SerialNumber, UnitId);
}
