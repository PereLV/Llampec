// P/Invoke declarations written from the Microsoft Win32 documentation (winuser.h, wingdi.h): the
// Connecting and Configuring Displays (CCD) API Llampec uses for HDR and for switching projection
// topology (PC screen only / Duplicate / Extend / Second screen only, i.e. the Win+P menu). Only the
// Windows 11 24H2 (build 26100+) HDR device info types are declared -- Llampec already requires that
// build (see Platform.OsVersion) -- but SetDisplayConfig/QueryDisplayConfig themselves are plain,
// long-stable Windows 7+ API, not something 24H2 introduced.
namespace Llampec.Interop;

public static partial class DisplayConfig
{
    // ---- QueryDisplayConfig flags (winuser.h) ----

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    /// <summary>Returns the active paths from the persistence database for the currently connected displays.</summary>
    public const uint QDC_DATABASE_CURRENT = 0x00000004;

    /// <summary>DISPLAYCONFIG_PATH_INFO.flags: set by QueryDisplayConfig when the path is part of the desktop.</summary>
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;

    // ---- SetDisplayConfig flags (winuser.h) -- only the values Llampec uses, to switch projection topology ----

    public const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    public const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    public const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    public const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;
    public const uint SDC_APPLY = 0x00000080;

    // ---- DISPLAYCONFIG_DEVICE_INFO_TYPE (wingdi.h) -- only the values Llampec uses ----

    public const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    /// <summary>Windows 11 24H2+. Read with DisplayConfigGetDeviceInfo into DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2.</summary>
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;

    /// <summary>Windows 11 24H2+. Write with DisplayConfigSetDeviceInfo from DISPLAYCONFIG_SET_HDR_STATE.</summary>
    public const int DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE = 16;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx; // union with a packed cloneGroupId/sourceModeInfoIdx pair; Llampec never reads it
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx; // union with a packed desktop/target mode index pair; Llampec never reads it
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public int scanLineOrdering;
        public int targetAvailable; // BOOL
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>
    /// Opaque placeholder for DISPLAYCONFIG_MODE_INFO (a union of source/target/desktop-image mode data,
    /// 64 bytes on all supported architectures). QueryDisplayConfig requires a correctly sized array to
    /// write into, but Llampec only needs the path array (adapter/target ids), never the mode contents.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO_OPAQUE
    {
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags; // DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS bit-field; bit 0 = friendlyNameFromEdid
        public int outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        public fixed char monitorFriendlyDeviceName[64];
        public fixed char monitorDevicePath[128];

        public readonly bool FriendlyNameFromEdid => (flags & 0x1) != 0;

        public string GetFriendlyName()
        {
            fixed (char* p = monitorFriendlyDeviceName)
            {
                return new string(p);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        /// <summary>
        /// Bit-field: 0 advancedColorSupported, 1 advancedColorActive, 2 reserved, 3 advancedColorLimitedByPolicy,
        /// 4 highDynamicRangeSupported, 5 highDynamicRangeUserEnabled, 6 wideColorSupported, 7 wideColorUserEnabled.
        /// </summary>
        public uint value;

        public int colorEncoding;
        public uint bitsPerColorChannel;
        public int activeColorMode;

        public readonly bool HighDynamicRangeSupported => (value & (1u << 4)) != 0;
        public readonly bool HighDynamicRangeUserEnabled => (value & (1u << 5)) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SET_HDR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        /// <summary>Bit-field: bit 0 enableHdr, bits 1-31 reserved (must be zero).</summary>
        public uint value;
    }

    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO_OPAQUE[] modeInfoArray,
        nint currentTopologyId);

    /// <summary>Overload for QDC_DATABASE_CURRENT, the only flag for which currentTopologyId must be non-null.</summary>
    [LibraryImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    public static partial int QueryDisplayConfigCurrentTopology(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO_OPAQUE[] modeInfoArray,
        out uint currentTopologyId);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_HDR_STATE setPacket);

    /// <summary>Used only to switch projection topology: pathArray/modeInfoArray are always null (see remarks
    /// on SDC_TOPOLOGY_XXX in the SetDisplayConfig documentation -- they must be for a topology-only call).</summary>
    [LibraryImport("user32.dll")]
    public static partial int SetDisplayConfig(uint numPathArrayElements, nint pathArray, uint numModeInfoArrayElements, nint modeInfoArray, uint flags);
}
