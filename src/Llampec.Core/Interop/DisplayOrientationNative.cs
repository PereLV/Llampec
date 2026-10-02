namespace Llampec.Interop;

/// <summary>Public Win32 display APIs. DEVMODEW is the 220-byte Unicode structure on x64 and ARM64.</summary>
internal static class DisplayOrientationNative
{
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 220)]
    internal unsafe struct DevMode
    {
        [FieldOffset(0)] public fixed char DeviceName[32];
        [FieldOffset(64)] public ushort SpecVersion;
        [FieldOffset(66)] public ushort DriverVersion;
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(70)] public ushort DriverExtra;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(76)] public int X;
        [FieldOffset(80)] public int Y;
        [FieldOffset(84)] public uint Orientation;
        [FieldOffset(88)] public uint FixedOutput;
        [FieldOffset(92)] public short Color;
        [FieldOffset(94)] public short Duplex;
        [FieldOffset(96)] public short YResolution;
        [FieldOffset(98)] public short TTOption;
        [FieldOffset(100)] public short Collate;
        [FieldOffset(102)] public fixed char FormName[32];
        [FieldOffset(166)] public ushort LogPixels;
        [FieldOffset(168)] public uint BitsPerPixel;
        [FieldOffset(172)] public uint Width;
        [FieldOffset(176)] public uint Height;
        [FieldOffset(180)] public uint DisplayFlags;
        [FieldOffset(184)] public uint Frequency;
        [FieldOffset(188)] public uint IcmMethod;
        [FieldOffset(192)] public uint IcmIntent;
        [FieldOffset(196)] public uint MediaType;
        [FieldOffset(200)] public uint DitherType;
        [FieldOffset(204)] public uint Reserved1;
        [FieldOffset(208)] public uint Reserved2;
        [FieldOffset(212)] public uint PanningWidth;
        [FieldOffset(216)] public uint PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct SourceName
    {
        public DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public fixed char Name[32];
        public string GetName() { fixed (char* p = Name) return new string(p); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetAutoRotationState(out uint state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsW(string deviceName, uint modeNumber, ref DevMode mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ChangeDisplaySettingsExW(string deviceName, ref DevMode mode, nint hwnd, uint flags, nint param);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int GetSourceName(ref SourceName packet);
}
