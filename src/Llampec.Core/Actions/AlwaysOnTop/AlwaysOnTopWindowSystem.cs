using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Llampec.Core.Tests")]
[assembly: InternalsVisibleTo("Llampec.AlwaysOnTop.Smoke")]

namespace Llampec.Actions.AlwaysOnTop;

internal readonly record struct WindowIdentity(nint Handle, uint ProcessId, uint ThreadId);
internal sealed record WindowSnapshot(WindowIdentity Identity, string Title, bool IsEligible, bool IsTopmost);
internal readonly record struct ForegroundWindowResolution(WindowSnapshot? Target, bool IsOwnWindow, string? Reason = null);
internal readonly record struct WindowChange(nint Handle, bool Foreground, bool PositionOnly = false);

/// <summary>The ownership marker is also checked, since Windows can reuse a handle in the same thread.</summary>
internal interface IAlwaysOnTopWindowSystem : IDisposable
{
    event Action<WindowChange>? WindowChanged;
    bool IsTrackingForeground { get; }
    string? LastError => null;
    nint ForegroundWindow { get; }
    ForegroundWindowResolution ResolveForeground(nint handle);
    WindowSnapshot? Read(nint handle);
    IReadOnlyList<WindowSnapshot> Enumerate();
    bool TryClaim(WindowIdentity identity);
    bool HasClaim(WindowIdentity identity);
    void ReleaseClaim(WindowIdentity identity);
    bool TrySetTopmost(WindowIdentity identity, bool topmost);
}
