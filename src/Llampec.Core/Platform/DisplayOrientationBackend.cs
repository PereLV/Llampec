using Llampec.Interop;

namespace Llampec.Platform;

/// <summary>CCD identifies physical targets; GDI changes exactly one non-cloned source using public APIs.</summary>
public sealed class DisplayOrientationBackend : IDisplayOrientationBackend
{
    private const uint OrientationField = 0x80;
    private readonly KeyboardShortcut _shortcut = new("Win+O");

    public DisplaySnapshot Capture()
    {
        var rotation = DisplayOrientationNative.GetAutoRotationState(out uint flags)
            ? new RotationState(true, (AutoRotationFlags)flags) : new RotationState(false, default);
        var paths = QueryPaths();
        var displays = new List<DisplayInfo>();
        foreach (var path in paths)
        {
            if (path.targetInfo.targetAvailable == 0) continue;
            var source = new DisplayOrientationNative.SourceName
            {
                Header = Header(1, Marshal.SizeOf<DisplayOrientationNative.SourceName>(), path.sourceInfo.adapterId, path.sourceInfo.id)
            };
            var target = new DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = Header(2, Marshal.SizeOf<DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), path.targetInfo.adapterId, path.targetInfo.id)
            };
            if (DisplayOrientationNative.GetSourceName(ref source) != 0 || DisplayConfig.DisplayConfigGetDeviceInfo(ref target) != 0)
                throw new DisplayOrientationException("Display configuration changed; try again");
            string deviceName = source.GetName();
            var native = ReadMode(deviceName);
            string sourceId = SourceId(path);
            string targetPath = GetDevicePath(target);
            // Never fall back to the primary monitor if a physical target cannot be identified.
            string id = string.IsNullOrWhiteSpace(targetPath)
                ? $"{path.targetInfo.adapterId.HighPart}:{path.targetInfo.adapterId.LowPart}:{path.targetInfo.id}"
                : targetPath;
            string name = target.GetFriendlyName();
            displays.Add(new(id, string.IsNullOrWhiteSpace(name) ? "Display" : name, deviceName, sourceId,
                path.targetInfo.outputTechnology is 6 or 11 or 13 or unchecked((int)0x80000000),
                paths.Count(p => SourceId(p) == sourceId) > 1,
                (native.Fields & OrientationField) != 0 && native.Orientation <= 3,
                Mode(native), Mode(ReadMode(deviceName, saved: true))));
        }
        if (displays.Select(d => d.Id).Distinct().Count() != displays.Count)
            throw new DisplayOrientationException("Display configuration changed; try again");
        return new(rotation, displays.AsReadOnly());
    }

    public bool SendRotationLockShortcut() => _shortcut.Send();

    public void TestOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode)
    {
        var display = Validate(expected, displayId);
        var native = Requested(display.DeviceName, mode);
        int result = DisplayOrientationNative.ChangeDisplaySettingsExW(display.DeviceName, ref native, 0, 2, 0);
        if (result != 0) throw new DisplayOrientationException("This orientation is not supported");
    }

    public void ApplyAndSaveOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode) =>
        WriteOrientation(expected, displayId, mode, 1); // CDS_UPDATEREGISTRY

    public void ApplyOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode) =>
        WriteOrientation(expected, displayId, mode, 0);

    private void WriteOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode, uint flags)
    {
        TestOrientation(expected, displayId, mode);
        // CDS_TEST and the actual write are separated by a fresh topology check.
        var display = Validate(expected, displayId);
        var native = Requested(display.DeviceName, mode);
        int result = DisplayOrientationNative.ChangeDisplaySettingsExW(display.DeviceName, ref native, 0, flags, 0);
        if (result != 0)
        {
            Diagnostics.Log.Warn($"ChangeDisplaySettingsEx failed: flags=0x{flags:X}, result={result}");
            throw new DisplayOrientationException("Windows could not change the orientation");
        }
    }

    private DisplayInfo Validate(DisplaySnapshot expected, string displayId)
    {
        var current = Capture();
        if (!expected.SameDisplays(current)) throw new DisplayOrientationException("Display configuration changed; try again");
        var display = current.Find(displayId) ?? throw new DisplayOrientationException("The selected display was disconnected");
        if (display.IsCloned) throw new DisplayOrientationException("Use Extend to rotate displays separately");
        if (!display.CanOrient) throw new DisplayOrientationException("This orientation is not supported");
        return display;
    }

    private static DisplayOrientationNative.DevMode ReadMode(string deviceName, bool saved = false)
    {
        var mode = new DisplayOrientationNative.DevMode { Size = (ushort)Marshal.SizeOf<DisplayOrientationNative.DevMode>() };
        if (!DisplayOrientationNative.EnumDisplaySettingsW(deviceName, saved ? uint.MaxValue - 1 : uint.MaxValue, ref mode))
            throw new DisplayOrientationException("Display configuration changed; try again");
        return mode;
    }

    private static DisplayOrientationNative.DevMode Requested(string deviceName, DisplayMode requested)
    {
        var mode = ReadMode(deviceName);
        mode.Fields |= OrientationField | 0x80000u | 0x100000u; // DM_DISPLAYORIENTATION / PELSWIDTH / PELSHEIGHT
        mode.Orientation = requested.Rotation;
        mode.Width = requested.Width;
        mode.Height = requested.Height;
        mode.X = requested.X;
        mode.Y = requested.Y;
        mode.BitsPerPixel = requested.BitsPerPixel;
        mode.Frequency = requested.Frequency;
        mode.DisplayFlags = requested.Flags;
        mode.FixedOutput = requested.FixedOutput;
        // Preserve the rest of the EnumDisplaySettings result; the snapshot comparison has verified it.
        return mode;
    }

    private static DisplayMode Mode(DisplayOrientationNative.DevMode mode) => new(mode.Orientation, mode.Width,
        mode.Height, mode.X, mode.Y, mode.BitsPerPixel, mode.Frequency, mode.DisplayFlags, mode.FixedOutput);

    private static string SourceId(DisplayConfig.DISPLAYCONFIG_PATH_INFO path) =>
        $"{path.sourceInfo.adapterId.HighPart}:{path.sourceInfo.adapterId.LowPart}:{path.sourceInfo.id}";

    private static unsafe string GetDevicePath(DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME target) => new(target.monitorDevicePath);

    private static DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, int size, DisplayConfig.LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    private static DisplayConfig.DISPLAYCONFIG_PATH_INFO[] QueryPaths()
    {
        // Bound retries: repeatedly changing topology must not block the UI indefinitely.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int result = DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, out uint count, out uint modesCount);
            if (result != 0) break;
            var paths = new DisplayConfig.DISPLAYCONFIG_PATH_INFO[count];
            var modes = new DisplayConfig.DISPLAYCONFIG_MODE_INFO_OPAQUE[modesCount];
            result = DisplayConfig.QueryDisplayConfig(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, ref count, paths, ref modesCount, modes, 0);
            if (result == 122) continue;
            if (result != 0) break;
            return paths.Take((int)count).ToArray();
        }
        throw new DisplayOrientationException("Display configuration unavailable");
    }
}
