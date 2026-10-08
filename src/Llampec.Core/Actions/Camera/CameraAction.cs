using System.Diagnostics;
using Llampec.Platform;
using Llampec.Settings;

namespace Llampec.Actions.Camera;

/// <summary>
/// Lights up while any camera streams, naming the camera and the apps using it. Pressing it opens
/// Windows camera settings. Activity is observed only while the panel is visible.
/// </summary>
public sealed class CameraAction : QuickActionBase, IDisposable
{
    private readonly CameraActivityMonitor _monitor = new();
    private bool _unavailable;

    public CameraAction()
    {
        _monitor.Changed += OnMonitorChanged;
        Refresh();
    }

    public override string Id => "camera";
    public override string Title => "Camera";
    public override string Glyph => "\uE722"; // Camera
    public override ActionKind Kind => ActionKind.Toggle;
    public IReadOnlyList<CameraUse> Active => _monitor.Active;

    /// <summary>Starts observation when the panel opens and stops it when the panel hides.</summary>
    public void SetObserving(bool observing)
    {
        if (!observing) _monitor.Stop();
        else if (!_monitor.IsRunning)
        {
            try { _monitor.Start(); _unavailable = false; }
            catch (Exception error)
            {
                _unavailable = true;
                Diagnostics.Log.Warn($"Camera activity is unavailable: {error.Message}");
            }
        }
        Refresh();
    }

    public static string Describe(IReadOnlyList<CameraUse> active)
    {
        if (active.Count == 0) return UiText.Get("Not in use");
        var apps = active.SelectMany(use => use.Apps).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string camera = active.Count == 1 ? active[0].Camera : UiText.Format("{0} cameras", active.Count);
        return apps.Length == 0 ? camera : $"{camera} · {string.Join(", ", apps)}";
    }

    public override void Refresh()
    {
        var active = _monitor.Active;
        State = active.Count > 0 ? ActionState.On : ActionState.Off;
        Subtitle = _unavailable ? "Camera activity is unavailable." : _monitor.IsRunning ? Describe(active) : null;
        OnChanged();
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var settings = Process.Start(new ProcessStartInfo("ms-settings:camera") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private void OnMonitorChanged(object? sender, EventArgs e) => Refresh();

    public void Dispose()
    {
        _monitor.Changed -= OnMonitorChanged;
        _monitor.Dispose();
    }
}
