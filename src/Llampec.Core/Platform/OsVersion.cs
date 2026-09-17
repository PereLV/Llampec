namespace Llampec.Platform;

public static class OsVersion
{
    /// <summary>Windows 11 24H2. Llampec relies on the 24H2 DisplayConfig HDR API and DWM backdrops.</summary>
    public const int MinimumBuild = 26100;

    /// <summary>Requires the app.manifest supportedOS entry for Windows 10/11, otherwise the OS lies about its version.</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= MinimumBuild;

    public static string Current => Environment.OSVersion.Version.ToString();
}
