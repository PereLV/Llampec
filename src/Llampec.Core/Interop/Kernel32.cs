namespace Llampec.Interop;

public static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? lpModuleName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSize(nint hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    /// <summary>
    /// Asks the memory manager to move the process's pages to the standby list. Pages come back on demand
    /// via cheap soft faults; this mostly lowers the number shown in Task Manager after the flyout closes.
    /// </summary>
    public static void TrimWorkingSet() => SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
}
