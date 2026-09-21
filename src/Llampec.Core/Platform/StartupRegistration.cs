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
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return key?.GetValue(ValueName) is string command
                && string.Equals(command, BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Updates a previous opt-in when the portable app is opened from a new location.
    /// Call only on a manual launch, never while Windows is processing its Run entries.
    /// Windows' separate startup approval is deliberately left unchanged.
    /// </summary>
    public bool RefreshExistingRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
        if (key?.GetValue(ValueName) is not string command || string.IsNullOrWhiteSpace(command))
            return false;

        string currentCommand = BuildCommand(executablePath);
        if (string.Equals(command, currentCommand, StringComparison.OrdinalIgnoreCase)) return false;

        key.SetValue(ValueName, currentCommand, RegistryValueKind.String);
        return true;
    }

    public void SetRegistered(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
        if (enabled) key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
