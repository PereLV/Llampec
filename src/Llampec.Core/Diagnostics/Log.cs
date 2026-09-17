using System.Diagnostics;

namespace Llampec.Diagnostics;

/// <summary>
/// Minimal logging: debugger output always; a small rolling text file only when the user enables it in
/// settings (see <c>AppSettings.EnableLogFile</c>). Nothing ever leaves the machine.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _filePath;

    /// <summary>Enables writing to <paramref name="filePath"/>. Pass null to disable.</summary>
    public static void SetFile(string? filePath)
    {
        lock (Gate)
        {
            _filePath = filePath;
        }

        // SettingsStore.Load() doesn't create %LOCALAPPDATA%\Llampec\ (it only reads), so on a machine
        // where settings.json was never saved yet, that folder doesn't exist and every AppendAllText below
        // was throwing DirectoryNotFoundException -- silently, since it's an IOException subtype and the
        // catch below swallows it. Create it here so logging actually works on a fresh install.
        if (filePath is not null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);

        string? path;
        lock (Gate)
        {
            path = _filePath;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Logging must never break the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
