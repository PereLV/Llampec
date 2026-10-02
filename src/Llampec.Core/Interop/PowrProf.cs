// P/Invoke declaration from the public powerbase.h API:
// https://learn.microsoft.com/windows/win32/api/powerbase/nf-powerbase-powerdetermineplatformroleex
namespace Llampec.Interop;

internal static partial class PowrProf
{
    // Version 2 includes PlatformRoleSlate (8); version 1 reports it as Mobile (2).
    [LibraryImport("powrprof.dll")]
    internal static partial int PowerDeterminePlatformRoleEx(uint version);
}
