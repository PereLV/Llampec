// Original implementation using the Microsoft HID/SetupAPI documentation.
// No Mouser, Solaar, or third-party transport code is incorporated here.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/obtaining-hid-reports
// https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/sending-hid-reports
// https://learn.microsoft.com/en-us/windows/win32/api/setupapi/ns-setupapi-sp_device_interface_detail_data_w
using System.ComponentModel;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Llampec.Devices.Logitech;

public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    string? ProductName,
    string? SerialNumber);

/// <summary>
/// User-mode access to a Logitech vendor HID collection. Opening and enumerating
/// collections never sends HID++ commands or changes the device configuration.
/// </summary>
public sealed class WindowsHidTransport : IHidTransport
{
    private const ushort LogitechVendorId = 0x046D;
    private const int LongReportLength = 20;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 3, OpenExisting = 3, Overlapped = 0x40000000;
    private const uint PresentDeviceInterfaces = 0x12;
    private const int InsufficientBuffer = 122, NoMoreItems = 259;
    private const int HidpStatusSuccess = 0x00110000;
    private readonly FileStream _stream;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private int _disposed;

    private WindowsHidTransport(FileStream stream, HidDeviceInfo info)
    {
        _stream = stream;
        InputReportLength = info.InputReportLength;
        OutputReportLength = info.OutputReportLength;
    }

    public int InputReportLength { get; }
    public int OutputReportLength { get; }

    /// <summary>
    /// Returns present, accessible vendor collections with room for long HID++
    /// reports. A receiver collection can represent several paired devices.
    /// </summary>
    public static IReadOnlyList<HidDeviceInfo> EnumerateLogitech()
    {
        HidD_GetHidGuid(out Guid hidClass);
        using var deviceSet = SetupDiGetClassDevsW(ref hidClass, null, 0, PresentDeviceInterfaces);
        if (deviceSet.IsInvalid)
            throw LastError("Could not enumerate HID device interfaces");

        var devices = new List<HidDeviceInfo>();
        for (uint index = 0; ; index++)
        {
            var deviceInterface = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(deviceSet, 0, ref hidClass, index, ref deviceInterface))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NoMoreItems) break;
                throw NativeError(error, "Could not enumerate a HID device interface");
            }

            string? path = GetDevicePath(deviceSet, ref deviceInterface);
            if (path is null) continue; // A device may disappear while being enumerated.

            // Zero desired access permits metadata queries without opening the
            // input/output channel. Inaccessible/disconnected collections are skipped.
            using var handle = CreateFileW(path, 0, ShareReadWrite, 0, OpenExisting, 0, 0);
            if (handle.IsInvalid) continue;
            if (ReadDeviceInfo(handle, path) is { } info) devices.Add(info);
        }

        return devices.AsReadOnly();
    }

    public static WindowsHidTransport Open(HidDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.Path);
        var handle = CreateFileW(info.Path, GenericRead | GenericWrite, ShareReadWrite,
            0, OpenExisting, Overlapped, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeError(error, "Could not open the Logitech HID collection for shared input/output");
        }

        try
        {
            // Recheck live metadata: paths and caller-supplied records are not an
            // authority to write to arbitrary files or to a standard mouse collection.
            var actual = ReadDeviceInfo(handle, info.Path)
                ?? throw new IOException("The selected interface is not an accessible Logitech vendor HID collection with long reports.");
            if (actual.ProductId != info.ProductId || actual.UsagePage != info.UsagePage || actual.Usage != info.Usage)
                throw new IOException("The HID collection changed after enumeration. Enumerate devices again.");

            // Buffering must be disabled: HID report boundaries matter, and writes
            // must be allowed while the independent asynchronous read is pending.
            var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
            return new WindowsHidTransport(stream, actual);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (buffer.Length < InputReportLength)
            throw new ArgumentException($"A HID read buffer must hold at least {InputReportLength} bytes.", nameof(buffer));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            int count = await _stream.ReadAsync(buffer[..InputReportLength], linked.Token).ConfigureAwait(false);
            if (count == 0) throw new IOException("The Logitech HID input channel closed.");
            return count;
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (report.IsEmpty || report.Length > OutputReportLength)
            throw new ArgumentException($"A HID output report must contain 1 to {OutputReportLength} bytes, including its report ID.", nameof(report));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // Windows expects the collection's maximum report size, including ID.
            // Padding belongs in the transport, not in the HID++ packet codec.
            if (report.Length == OutputReportLength)
            {
                await _stream.WriteAsync(report, linked.Token).ConfigureAwait(false);
            }
            else
            {
                var padded = new byte[OutputReportLength];
                report.CopyTo(padded);
                await _stream.WriteAsync(padded, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Cancel overlapped operations before closing their SafeFileHandle; wait
        // until the runtime has observed completion and unpinned the report buffers.
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _readGate.WaitAsync().ConfigureAwait(false);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
            _writeGate.Release();
            _readGate.Release();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static string? GetDevicePath(SafeDeviceInfoSetHandle deviceSet, ref DeviceInterfaceData deviceInterface)
    {
        _ = SetupDiGetDeviceInterfaceDetailW(deviceSet, ref deviceInterface, 0, 0, out uint requiredSize, 0);
        if (Marshal.GetLastWin32Error() != InsufficientBuffer || requiredSize < 6 || requiredSize > int.MaxValue)
            return null;

        nint detail = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            // cbSize includes native alignment (8 on ARM64/x64, 6 on x86);
            // DevicePath always begins immediately after the four-byte DWORD.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetailW(deviceSet, ref deviceInterface, detail, requiredSize, out _, 0)
                ? Marshal.PtrToStringUni(detail + sizeof(uint))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static HidDeviceInfo? ReadDeviceInfo(SafeFileHandle handle, string path)
    {
        var attributes = new HidAttributes { Size = (uint)Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != LogitechVendorId)
            return null;
        if (!HidD_GetPreparsedData(handle, out nint preparsedData)) return null;
        try
        {
            if (HidP_GetCaps(preparsedData, out var capabilities) != HidpStatusSuccess
                || capabilities.UsagePage < 0xFF00
                || capabilities.InputReportByteLength < LongReportLength
                || capabilities.OutputReportByteLength < LongReportLength)
                return null;

            return new HidDeviceInfo(path, attributes.VendorId, attributes.ProductId,
                capabilities.UsagePage, capabilities.Usage, capabilities.InputReportByteLength,
                capabilities.OutputReportByteLength, GetDeviceString(handle, serial: false), GetDeviceString(handle, serial: true));
        }
        finally
        {
            _ = HidD_FreePreparsedData(preparsedData);
        }
    }

    private static string? GetDeviceString(SafeFileHandle handle, bool serial)
    {
        var buffer = new byte[256];
        bool success = serial
            ? HidD_GetSerialNumberString(handle, buffer, (uint)buffer.Length)
            : HidD_GetProductString(handle, buffer, (uint)buffer.Length);
        if (!success) return null;
        string value = Encoding.Unicode.GetString(buffer);
        int end = value.IndexOf('\0');
        if (end >= 0) value = value[..end];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static Win32Exception LastError(string operation) => NativeError(Marshal.GetLastWin32Error(), operation);

    private static Win32Exception NativeError(int error, string operation) =>
        new(error, $"{operation}: {new Win32Exception(error).Message} (Win32 {error}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    // HIDP_CAPS has no pointer-sized members. Reserve all 64 native bytes even
    // though this transport only consumes its first five USHORT fields.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCapabilities
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(6)] public ushort OutputReportByteLength;
        [FieldOffset(8)] public ushort FeatureReportByteLength;
    }

    private sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDeviceInfoSetHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeDeviceInfoSetHandle SetupDiGetClassDevsW(ref Guid classGuid,
        string? enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(SafeDeviceInfoSetHandle deviceSet,
        nint deviceInfo, ref Guid interfaceClassGuid, uint memberIndex, ref DeviceInterfaceData interfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(SafeDeviceInfoSetHandle deviceSet,
        ref DeviceInterfaceData interfaceData, nint detailData, uint detailDataSize, out uint requiredSize, nint deviceInfo);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    private static extern int HidP_GetCaps(nint preparsedData, out HidCapabilities capabilities);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, [Out] byte[] buffer, uint bufferLength);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle device, [Out] byte[] buffer, uint bufferLength);
}
