// P/Invoke declarations written from the Microsoft documentation (processthreadsapi.h,
// winbase.h, appmodel.h).
using System.Diagnostics;

namespace Llampec.Platform;

/// <summary>
/// Reads one process's image name. <c>Process.GetProcessById(id).ProcessName</c> snapshots every
/// process on the system (about 7 ms and 6 KB per call with 300 processes); a limited-access query
/// reads only the requested process in about a microsecond. The snapshot remains only a fallback.
/// </summary>
public static partial class ProcessImage
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Full image path, or null when the process cannot be queried.</summary>
    public static unsafe string? ImagePath(uint processId)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>The image file name without extension, like <c>Process.ProcessName</c>; null once the process exits.</summary>
    public static string? Name(uint processId)
    {
        if (ImagePath(processId) is { } path) return Path.GetFileNameWithoutExtension(path);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The Application User Model ID of a packaged process; null for desktop processes.</summary>
    public static unsafe string? AppUserModelId(uint processId)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            char* buffer = stackalloc char[256];
            uint length = 256;
            return GetApplicationUserModelId(process, ref length, buffer) == 0 ? new string(buffer) : null;
        }
        finally { CloseHandle(process); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetApplicationUserModelId(nint process, ref uint length, char* applicationUserModelId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
