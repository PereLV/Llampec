using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Llampec.Settings;

namespace Llampec.Platform;

/// <summary>A process-owned request: no dedicated thread and no power-plan changes.</summary>
public sealed class CaffeineRequest : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _system, _display;
    private CaffeineRequest(SafeFileHandle handle) => _handle = handle;

    public static IDisposable Create(bool keepDisplayOn)
    {
        nint reason = Marshal.StringToHGlobalUni("Llampec: " + UiText.Get("Caffeine mode"));
        SafeFileHandle handle;
        try
        {
            var context = new ReasonContext { Flags = 1, SimpleReasonString = reason };
            handle = PowerCreateRequest(ref context);
        }
        finally { Marshal.FreeHGlobal(reason); }
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        var request = new CaffeineRequest(handle);
        try
        {
            if (!PowerSetRequest(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            request._system = true;
            if (keepDisplayOn)
            {
                if (!PowerSetRequest(handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                request._display = true;
            }
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_handle.IsClosed) return;
        if (_display) PowerClearRequest(_handle, 0);
        if (_system) PowerClearRequest(_handle, 1);
        _handle.Dispose(); // Closing also releases any remaining requests if clearing failed.
    }

    // REASON_CONTEXT contains a 24-byte union on the supported x64/ARM64 targets.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version, Flags;
        public nint SimpleReasonString, Reserved1, Reserved2;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle handle, int type);
}
