namespace Llampec.Devices.Logitech;

public sealed record LogitechControl(byte Index, ushort Id, ushort TaskId, ushort Flags,
    byte Position, byte Group, byte GroupMask, ushort ReportingFlags, ushort MappedTo)
{
    public bool Divertable => (Flags & 0x0020) != 0;
    public bool Virtual => (Flags & 0x0080) != 0;
}

public sealed record LogitechDpiRange(ushort Minimum, ushort Maximum, ushort Step)
{
    public bool Contains(ushort value) => Step != 0 && value >= Minimum && value <= Maximum
        && (value - Minimum) % Step == 0;
}

/// <summary>The prototype deliberately operates only on sensor zero.</summary>
public sealed record LogitechDpiState(byte SensorCount, ushort Current, ushort Default,
    IReadOnlyList<ushort> Values, LogitechDpiRange? Range)
{
    public bool Supports(ushort value) => Range?.Contains(value) ?? Values.Contains(value);
}

/// <param name="ThirdByte">Default threshold for 0x2110; torque for 0x2111. Never modified.</param>
public sealed record LogitechSmartShiftState(ushort FeatureId, byte Mode, byte Threshold, byte ThirdByte)
{
    public bool Automatic => Mode == 2 && Threshold != 255;
}

public sealed record LogitechThumbWheelState(byte ReportingMode, bool Inverted);

/// <summary>Battery from HID++ 0x1004 (unified battery) or 0x1000 (battery status).</summary>
/// <param name="Percent">Null when the device reports only an approximate level.</param>
/// <param name="Level">Approximate level: critical, low, good or full; null with an exact percentage only.</param>
public sealed record LogitechBattery(int? Percent, string? Level, bool Charging, bool Full);

/// <summary>Only the temporary diversion bit is owned; mapping and other flags are preserved.</summary>
public sealed record LogitechControlRestoreState(ushort Id, bool Diverted);

/// <summary>
/// Transport-independent, serializable pre-change values. Null settings were not selected.
/// The caller must bind this record to the physical device identity before replaying it.
/// </summary>
public sealed record LogitechRestoreState(ushort? Dpi, byte? SmartMode, byte? SmartThreshold,
    bool? VerticalInvert, bool? HorizontalInvert, IReadOnlyList<LogitechControlRestoreState> Controls);
