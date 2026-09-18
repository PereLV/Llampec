using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Llampec.Platform;

/// <summary>User-initiated elevation through the standard Windows UAC prompt.</summary>
public static class AdministratorRestart
{
    public const string PermissionError = "This window requires administrator permissions. Restart Llampec as administrator to pin it.";
    public const string RestartArgumentPrefix = "--restart-from=";

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static bool TryStart(out string? error)
    {
        error = null;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add(RestartArgumentPrefix + Environment.ProcessId);
            using var replacement = Process.Start(start);
            if (replacement is not null) return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Administrator restart was cancelled.";
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Diagnostics.Log.Warn($"Administrator restart failed: {ex.Message}");
        }
        error = "Could not restart Llampec as administrator.";
        return false;
    }

    /// <summary>The replacement waits for normal cleanup and mutex release by this same executable.</summary>
    public static bool WaitForPreviousInstance(string[] arguments)
    {
        string? argument = arguments.FirstOrDefault(arg => arg.StartsWith(RestartArgumentPrefix, StringComparison.Ordinal));
        if (argument is null) return true;
        if (!int.TryParse(argument.AsSpan(RestartArgumentPrefix.Length), out int id) || id <= 0 || id == Environment.ProcessId)
            return false;
        try
        {
            using var previous = Process.GetProcessById(id);
            if (!string.Equals(previous.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return false;
            return previous.WaitForExit(10000);
        }
        catch (ArgumentException) { return true; } // Already exited after approving the UAC prompt.
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception ex)
        {
            Diagnostics.Log.Warn($"Could not wait for administrator restart: {ex.Message}");
            return false;
        }
    }
}
