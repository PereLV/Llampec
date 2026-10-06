namespace Llampec.Actions.Fullscreen;

internal readonly record struct FullscreenWindowIdentity(nint Handle, uint ProcessId, uint ThreadId);
internal sealed record FullscreenWindowSnapshot(FullscreenWindowIdentity Identity, string Title,
    bool IsEligible, bool IsOwnWindow, bool IsShellSurface, bool IsMinimized = false, bool IsVisible = true);
internal enum FullscreenForegroundDisposition { Target, Preserve, DifferentWindow }

internal interface IFullscreenSession
{
    FullscreenWindowIdentity Identity { get; }
    string MonitorDescription { get; }
    string MonitorDevice { get; }
    bool OwnsWindow { get; }
    bool IsVisible { get; }
    bool IsMinimized { get; }
    bool IsCloaked { get; }
    bool DisplayUnchanged { get; }
    FullscreenForegroundDisposition ClassifyForeground(nint foreground);
    void UpdatePointer(long now);
    void ReapplyShellPolicy();
    void RevealTaskbar();
    void CoverTaskbar();
    void Restore();
}

internal interface IFullscreenWindowSystem : IDisposable
{
    event Action? WindowChanged;
    /// <summary>Foreground changes are always reported; other window events only while enabled.</summary>
    void SetWindowTracking(bool enabled) { }
    nint ForegroundWindow { get; }
    FullscreenWindowSnapshot? Read(nint handle);
    FullscreenWindowSnapshot? ResolveForeground(nint handle);
    IFullscreenSession Enter(FullscreenWindowIdentity identity);
}

internal sealed class FullscreenActivationRecoveryException(IFullscreenSession session, Exception error)
    : InvalidOperationException("Could not fully restore the window.", error)
{
    public IFullscreenSession Session { get; } = session;
}
