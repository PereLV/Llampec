using Microsoft.Win32;

namespace Llampec.Platform;

/// <summary>Per-user startup for the portable, unpackaged application. No service or elevation.</summary>
public sealed class StartupRegistration(string executablePath, string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    private const string ValueName = "Llampec";
    public static string BuildCommand(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("An absolute executable path is required.", nameof(path));
        string command = $"\"{path}\" --background";
        if (command.Length > 260) throw new ArgumentException("The startup command exceeds Windows' 260-character limit.", nameof(path));
        return command;
    }
    public bool IsRegistered
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(registryPath); return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command); }
    }
    public void SetRegistered(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
        if (enabled) key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
