using System.Diagnostics;
using Llampec.Diagnostics;

namespace Llampec.Actions.Fullscreen;

/// <summary>
/// One session-owned fullscreen window. Construct/use on the UI STA thread. The host calls Tick
/// every 30 ms while NeedsPolling; there is no idle polling or input hook.
/// </summary>
public sealed class FullscreenService : IDisposable
{
    private readonly IFullscreenWindowSystem _windows;
    private readonly Func<long> _clock;
    private readonly SynchronizationContext? _context;
    private readonly Func<string, string> _monitorName;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private FullscreenWindowIdentity? _lastExternal, _target, _differentWindow;
    private IFullscreenSession? _active;
    private long _differentSince, _nextDisplayCheck;
    private bool _frozen, _mutating, _disposed, _refreshing, _restorationFailed;
    private (FullscreenWindowIdentity? Target, string Title, nint Active, string ActiveTitle, string Monitor, bool Polling, string? Error)? _published;

    public event EventHandler? Changed;
    public bool IsActive => _active is not null;
    public bool NeedsPolling => IsActive && !_restorationFailed;
    public bool HasTarget => _target is not null;
    public nint TargetWindow => _target?.Handle ?? 0;
    public nint ActiveWindow => _active?.Identity.Handle ?? 0;
    public string TargetTitle { get; private set; } = string.Empty;
    public string ActiveTitle { get; private set; } = string.Empty;
    public string ActiveMonitor { get; private set; } = string.Empty;
    public string ActiveMonitorDevice => _active?.MonitorDevice ?? string.Empty;
    public string? Error { get; private set; }

    public FullscreenService(Func<string, string>? monitorName = null) : this(new NativeFullscreenWindowSystem(),
        () => (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency)), SynchronizationContext.Current, monitorName) { }

    internal FullscreenService(IFullscreenWindowSystem windows, Func<long> clock, SynchronizationContext? context = null,
        Func<string, string>? monitorName = null)
    {
        _windows = windows;
        _clock = clock;
        _context = context;
        _monitorName = monitorName ?? (device => device);
        _windows.WindowChanged += OnWindowChanged;
        Refresh();
    }

    /// <summary>Call before Llampec's panel takes focus.</summary>
    public void CaptureTarget()
    {
        if (_disposed) return;
        RememberForeground();
        _target = _lastExternal;
        _frozen = true;
        Refresh();
    }

    public void ReleaseTarget() { if (_disposed) return; _frozen = false; Refresh(); }

    public void ToggleTarget()
    {
        if (_disposed || _mutating) return;
        if (_active is not null) { Stop(); return; }
        Refresh();
        Start(_target);
    }

    public void ToggleForeground()
    {
        if (_disposed || _mutating) return;
        if (_active is not null) { Stop(); return; }
        var foreground = _windows.ResolveForeground(_windows.ForegroundWindow);
        var identity = foreground is { IsEligible: true } ? foreground.Identity
            : foreground is { IsOwnWindow: true } ? (_frozen ? _target : _lastExternal) : null;
        Start(identity);
    }

    /// <summary>Restore the owned session before unloading; failed recovery remains available for retry.</summary>
    public bool TryDeactivate()
    {
        if (_mutating) return false;
        Stop();
        return !IsActive;
    }

    private void Start(FullscreenWindowIdentity? identity)
    {
        _mutating = true;
        Error = null;
        try
        {
            var snapshot = identity is { } target ? ReadMatching(target) : null;
            if (snapshot is not { IsEligible: true })
            {
                Error = "No eligible window is active.";
                return;
            }
            _active = _windows.Enter(snapshot.Identity);
            _restorationFailed = false;
            ActiveTitle = snapshot.Title;
            _differentWindow = null;
            _nextDisplayCheck = 0;
        }
        catch (FullscreenActivationRecoveryException ex)
        {
            _active = ex.Session;
            _restorationFailed = true;
            ActiveTitle = ReadMatching(ex.Session.Identity)?.Title ?? string.Empty;
            Error = ex.Message;
            Log.Warn($"Fullscreen rollback failed: {ex}");
        }
        catch (Exception ex)
        {
            Error = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
                ? "This window requires administrator permissions to change fullscreen." : ex.Message;
            Log.Warn($"Fullscreen activation failed: {ex}");
        }
        finally { _mutating = false; Refresh(); }
    }

    private void Stop()
    {
        if (_mutating || _active is null) return;
        _mutating = true;
        var previous = _active;
        bool wasRecovery = _restorationFailed;
        _active = null;
        ActiveTitle = string.Empty;
        _differentWindow = null;
        try
        {
            previous.Restore();
            _restorationFailed = false;
            if (wasRecovery) Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            if (previous.OwnsWindow)
            {
                _active = previous;
                _restorationFailed = true;
                ActiveTitle = ReadMatching(previous.Identity)?.Title ?? string.Empty;
            }
            Log.Warn($"Fullscreen restoration failed: {ex}");
        }
        finally { _mutating = false; Refresh(); }
    }

    /// <summary>Polling is limited to an active session, for edge hover and a 120 ms focus debounce.</summary>
    public void Tick()
    {
        if (_disposed || _mutating || _restorationFailed || _active is not { } active) return;
        long now = _clock();
        try
        {
            // The owner identity/marker and native status queries are sufficient here. A full
            // snapshot reads the process name and caption; reserve that for actual window events.
            if (!active.OwnsWindow || active.IsMinimized || !active.IsVisible || active.IsCloaked)
            { Stop(); return; }
            if (now >= _nextDisplayCheck)
            {
                _nextDisplayCheck = now + 250;
                if (!active.DisplayUnchanged) { Stop(); return; }
            }
            nint foreground = _windows.ForegroundWindow;
            if (active.ClassifyForeground(foreground) == FullscreenForegroundDisposition.DifferentWindow)
            {
                var other = _windows.Read(foreground);
                if (other is null) _differentWindow = null;
                else if (_differentWindow != other.Identity) { _differentWindow = other.Identity; _differentSince = now; }
                else if (now - _differentSince >= 120) { Stop(); return; }
            }
            else _differentWindow = null;
            active.UpdatePointer(now);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn($"Fullscreen maintenance failed: {ex.Message}");
            Stop();
        }
    }

    private FullscreenWindowSnapshot? ReadMatching(FullscreenWindowIdentity identity)
    {
        var snapshot = _windows.Read(identity.Handle);
        return snapshot?.Identity == identity ? snapshot : null;
    }

    private void RememberForeground()
    {
        var window = _windows.ResolveForeground(_windows.ForegroundWindow);
        if (window is { IsEligible: true }) _lastExternal = window.Identity;
        else if (_lastExternal is { } previous && ReadMatching(previous) is not { IsEligible: true }) _lastExternal = null;
    }

    public void Refresh()
    {
        if (_disposed || _mutating || _refreshing) return;
        _refreshing = true;
        try
        {
            RememberForeground();
            if (!_frozen) _target = _lastExternal;
            var target = _target is { } identity ? ReadMatching(identity) : null;
            if (target is not { IsEligible: true }) _target = null;
            TargetTitle = _target is null ? string.Empty : target!.Title;
            if (_active is { } active)
            {
                if (ReadMatching(active.Identity) is { } current) ActiveTitle = current.Title;
                ActiveMonitor = _monitorName(active.MonitorDevice);
            }
            else ActiveMonitor = string.Empty;
        }
        finally { _refreshing = false; }
        Publish();
    }

    /// <summary>Reassert our session marking after Explorer restarts; never change taskbar preferences.</summary>
    public void OnEnvironmentChanged()
    {
        if (_disposed || _mutating) return;
        if (_active is { } active && !_restorationFailed)
        {
            try
            {
                if (!active.DisplayUnchanged) Stop();
                else active.ReapplyShellPolicy();
            }
            catch (Exception ex) { Error = ex.Message; Stop(); }
        }
        Refresh();
    }

    private void OnWindowChanged()
    {
        if (_disposed || _mutating) return;
        if (_context is not null && Environment.CurrentManagedThreadId != _ownerThread)
        { _context.Post(_ => OnWindowChanged(), null); return; }
        // Hidden and idle, only the last external app matters; opening the panel refreshes everything.
        if (!_frozen && _active is null) { RememberForeground(); return; }
        Refresh();
    }

    /// <summary>
    /// Idle Llampec needs only foreground changes to remember the last app. Other window
    /// events refresh titles while the panel is shown or a session is active.
    /// </summary>
    private void UpdateWindowTracking()
    {
        if (_disposed) return;
        // Hooks belong to the owner thread's message loop.
        if (_context is not null && Environment.CurrentManagedThreadId != _ownerThread)
        { _context.Post(_ => UpdateWindowTracking(), null); return; }
        _windows.SetWindowTracking(_frozen || _active is not null);
    }

    private void Publish()
    {
        UpdateWindowTracking();
        var state = (_target, TargetTitle, ActiveWindow, ActiveTitle, ActiveMonitor, NeedsPolling, Error);
        if (_published == state) return;
        _published = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _windows.WindowChanged -= OnWindowChanged;
        Stop();
        _disposed = true;
        _windows.Dispose();
    }
}
