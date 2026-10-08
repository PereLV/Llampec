// COM declarations written from the Microsoft Media Foundation documentation (mfidl.h):
// https://learn.microsoft.com/windows/win32/api/mfidl/nf-mfidl-mfcreatesensoractivitymonitor
using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;
using Llampec.Diagnostics;

namespace Llampec.Platform;

/// <summary>One camera that is streaming, and the apps that receive its frames.</summary>
public sealed record CameraUse(string Camera, IReadOnlyList<string> Apps);

/// <summary>
/// Reports camera streaming through Windows' sensor activity monitor. It runs only between
/// Start and Stop; Windows delivers reports on its own thread, with no polling here.
/// </summary>
public sealed partial class CameraActivityMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CameraUse> _active = new(StringComparer.OrdinalIgnoreCase);
    private IMFSensorActivityMonitor? _monitor;
    private bool _disposed;

    /// <summary>Raised from a Media Foundation thread after the active set changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<CameraUse> Active
    {
        get { lock (_gate) return _active.Values.ToArray(); }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _monitor is not null; }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_monitor is not null) return;
            // Each successful MFStartup is paired with MFShutdown in Stop.
            Marshal.ThrowExceptionForHR(MFStartup(MfVersion, MfStartupLite));
            IMFSensorActivityMonitor? monitor = null;
            try
            {
                Marshal.ThrowExceptionForHR(MFCreateSensorActivityMonitor(new ReportCallback(this), out monitor));
                Marshal.ThrowExceptionForHR(monitor.Start());
                _monitor = monitor;
            }
            catch
            {
                if (monitor is not null) ReleaseComObject(monitor);
                MFShutdown();
                throw;
            }
        }
    }

    public void Stop()
    {
        IMFSensorActivityMonitor? monitor;
        bool changed;
        lock (_gate)
        {
            monitor = _monitor;
            _monitor = null;
            changed = _active.Count != 0;
            _active.Clear();
        }
        if (monitor is null) return;
        // Stop waits for an in-flight callback, which takes _gate; call it unlocked.
        try { monitor.Stop(); }
        finally
        {
            ReleaseComObject(monitor);
            MFShutdown();
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) _disposed = true;
    }

    private unsafe void OnReport(IMFSensorActivitiesReport report)
    {
        // A report lists the cameras whose activity changed. A camera without
        // streaming clients is no longer in use.
        var updates = new List<(string Link, CameraUse? Use)>();
        if (report.GetCount(out uint count) < 0) return;
        for (uint index = 0; index < count; index++)
        {
            if (report.GetActivityReport(index, out var device) < 0 || device is null) continue;
            try
            {
                string link = ReadString(device.GetSymbolicLink);
                string name = ReadString(device.GetFriendlyName);
                var apps = new List<string>();
                if (device.GetProcessCount(out uint processes) >= 0)
                    for (uint p = 0; p < processes; p++)
                    {
                        if (device.GetProcessActivity(p, out var activity) < 0 || activity is null) continue;
                        try
                        {
                            if (activity.GetStreamingState(out int streaming) >= 0 && streaming != 0
                                && activity.GetProcessId(out uint pid) >= 0)
                            {
                                string app = AppName(pid);
                                if (!apps.Contains(app, StringComparer.OrdinalIgnoreCase)) apps.Add(app);
                            }
                        }
                        finally { ReleaseComObject(activity); }
                    }
                string key = link.Length != 0 ? link : name;
                updates.Add((key, apps.Count == 0 ? null : new CameraUse(name.Length != 0 ? name : key, apps)));
            }
            finally { ReleaseComObject(device); }
        }
        bool changed = false;
        lock (_gate)
        {
            if (_monitor is null) return;
            foreach (var (link, use) in updates)
                changed |= use is null ? _active.Remove(link) : !_active.TryGetValue(link, out var old) || !Same(old, use);
            foreach (var (link, use) in updates)
                if (use is not null) _active[link] = use;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Same(CameraUse a, CameraUse b) => a.Camera == b.Camera && a.Apps.SequenceEqual(b.Apps);

    private unsafe delegate int StringReader(char* buffer, uint capacity, out uint written);

    private static unsafe string ReadString(StringReader read)
    {
        char* buffer = stackalloc char[512];
        if (read(buffer, 512, out uint written) < 0) return "";
        return new string(buffer).Trim();
    }

    /// <summary>
    /// A packaged app's display name (for example "Camera"), else the executable's description
    /// (for example "Microsoft Teams"), else its file name.
    /// </summary>
    internal static string AppName(uint pid)
    {
        if (ProcessImage.AppUserModelId(pid) is { } aumid)
        {
            try
            {
                string name = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid).DisplayInfo.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            catch (Exception error) when (error is ArgumentException or COMException) { }
        }
        if (ProcessImage.ImagePath(pid) is { } path)
        {
            try
            {
                string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description)) return description;
            }
            catch (IOException) { }
            return Path.GetFileNameWithoutExtension(path);
        }
        return ProcessImage.Name(pid) ?? $"PID {pid}";
    }

    private static void ReleaseComObject(object value)
    {
        if (value is ComObject com) com.FinalRelease();
    }

    [GeneratedComClass]
    private sealed partial class ReportCallback(CameraActivityMonitor owner) : IMFSensorActivitiesReportCallback
    {
        public int OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            try { owner.OnReport(report); }
            catch (Exception error) { Log.Warn($"Camera activity report failed: {error.Message}"); }
            finally { ReleaseComObject(report); }
            return 0;
        }
    }

    // mfapi.h: MF_VERSION = (MF_SDK_VERSION << 16) | MF_API_VERSION; MFSTARTUP_LITE omits sockets.
    private const uint MfVersion = 0x0002_0070;
    private const uint MfStartupLite = 1;

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();

    [LibraryImport("mfsensorgroup.dll")]
    private static partial int MFCreateSensorActivityMonitor(IMFSensorActivitiesReportCallback callback,
        out IMFSensorActivityMonitor monitor);
}

[GeneratedComInterface, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB")]
internal partial interface IMFSensorActivityMonitor
{
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
}

[GeneratedComInterface, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751")]
internal partial interface IMFSensorActivitiesReportCallback
{
    [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777")]
internal partial interface IMFSensorActivitiesReport
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport report);
    [PreserveSig] int GetActivityReportByDeviceName(string symbolicName, out IMFSensorActivityReport report);
}

[GeneratedComInterface, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD")]
internal unsafe partial interface IMFSensorActivityReport
{
    [PreserveSig] int GetFriendlyName(char* friendlyName, uint capacity, out uint written);
    [PreserveSig] int GetSymbolicLink(char* symbolicLink, uint capacity, out uint written);
    [PreserveSig] int GetProcessCount(out uint count);
    [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity activity);
}

[GeneratedComInterface, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8")]
internal partial interface IMFSensorProcessActivity
{
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int GetStreamingState(out int streaming);
    [PreserveSig] int GetStreamingMode(out int mode);
    [PreserveSig] int GetReportTime(out long fileTime);
}

