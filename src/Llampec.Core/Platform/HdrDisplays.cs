using Llampec.Interop;

namespace Llampec.Platform;

/// <summary>
/// Per-monitor HDR state, read and set through the Windows 11 24H2 Connecting and Configuring Displays
/// (CCD) API -- DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 and DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE.
/// This is the same mechanism the OS's own Settings &gt; Display &gt; HDR toggle uses on 24H2+; there is no
/// older-Windows fallback here because Llampec already refuses to run below build 26100 (see
/// <see cref="OsVersion"/>), so carrying the pre-24H2 advanced-color API would only add an untested path.
/// </summary>
public static class HdrDisplays
{
    private const int ErrorInsufficientBuffer = 122;

    public readonly record struct Display(DisplayConfig.LUID AdapterId, uint TargetId, string Name, bool Supported, bool Enabled);

    /// <summary>One entry per active display path, in the order QueryDisplayConfig reports them.</summary>
    public static IReadOnlyList<Display> GetDisplays()
    {
        var paths = QueryActivePaths();
        var displays = new List<Display>(paths.Count);

        foreach (var path in paths)
        {
            if (path.targetInfo.targetAvailable == 0)
            {
                continue; // transient: monitor unplugged, path not yet retired by the OS
            }

            if (!TryGetColorState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out bool enabled))
            {
                continue; // same race as above
            }

            displays.Add(new Display(path.targetInfo.adapterId, path.targetInfo.id,
                GetFriendlyName(path.targetInfo.adapterId, path.targetInfo.id), supported, enabled));
        }

        return displays;
    }

    /// <summary>
    /// Re-reads support/enabled for one already-known target, without re-enumerating every display path.
    /// Used by a monitor tile's own Refresh() so opening the panel with N HDR-capable monitors costs N
    /// device-info calls, not the full QueryDisplayConfig walk N times over.
    /// </summary>
    public static bool TryGetColorState(DisplayConfig.LUID adapterId, uint targetId, out bool supported, out bool enabled)
    {
        var colorInfo = new DisplayConfig.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            header = Header(DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2,
                Marshal.SizeOf<DisplayConfig.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(), adapterId, targetId),
        };

        if (DisplayConfig.DisplayConfigGetDeviceInfo(ref colorInfo) != 0)
        {
            supported = false;
            enabled = false;
            return false;
        }

        supported = colorInfo.HighDynamicRangeSupported;
        enabled = colorInfo.HighDynamicRangeUserEnabled;
        return true;
    }

    public static void SetEnabled(DisplayConfig.LUID adapterId, uint targetId, bool enable)
    {
        var state = new DisplayConfig.DISPLAYCONFIG_SET_HDR_STATE
        {
            header = Header(DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE,
                Marshal.SizeOf<DisplayConfig.DISPLAYCONFIG_SET_HDR_STATE>(), adapterId, targetId),
            value = enable ? 1u : 0u,
        };

        int result = DisplayConfig.DisplayConfigSetDeviceInfo(ref state);
        if (result != 0)
        {
            throw new InvalidOperationException($"DisplayConfigSetDeviceInfo(SET_HDR_STATE) failed: {result}");
        }
    }

    private static string GetFriendlyName(DisplayConfig.LUID adapterId, uint targetId)
    {
        var name = new DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = Header(DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                Marshal.SizeOf<DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId, targetId),
        };

        if (DisplayConfig.DisplayConfigGetDeviceInfo(ref name) != 0)
        {
            return "Display";
        }

        string friendly = name.GetFriendlyName();
        return string.IsNullOrWhiteSpace(friendly) ? "Display" : friendly;
    }

    private static DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, int size, DisplayConfig.LUID adapterId, uint id) => new()
    {
        type = type,
        size = (uint)size,
        adapterId = adapterId,
        id = id,
    };

    private static List<DisplayConfig.DISPLAYCONFIG_PATH_INFO> QueryActivePaths()
    {
        int result;
        uint pathCount, modeCount;
        DisplayConfig.DISPLAYCONFIG_PATH_INFO[] paths;

        do
        {
            result = DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
            if (result != 0)
            {
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {result}");
            }

            paths = new DisplayConfig.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DisplayConfig.DISPLAYCONFIG_MODE_INFO_OPAQUE[modeCount];
            result = DisplayConfig.QueryDisplayConfig(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, 0);

            // The display topology can change between GetDisplayConfigBufferSizes and QueryDisplayConfig
            // (e.g. a monitor is unplugged mid-call); retry with freshly sized buffers, as the Microsoft
            // sample for QueryDisplayConfig does.
        } while (result == ErrorInsufficientBuffer);

        if (result != 0)
        {
            throw new InvalidOperationException($"QueryDisplayConfig failed: {result}");
        }

        if (pathCount < paths.Length)
        {
            Array.Resize(ref paths, (int)pathCount);
        }

        return [.. paths];
    }
}
