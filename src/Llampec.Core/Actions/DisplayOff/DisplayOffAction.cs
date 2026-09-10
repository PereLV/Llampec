using Llampec.Interop;
using Llampec.Platform;

namespace Llampec.Actions.DisplayOff;

/// <summary>
/// "Turn off display": puts every monitor into power-off mode with WM_SYSCOMMAND / SC_MONITORPOWER.
/// The message is posted to Llampec's own message window (DefWindowProc does the work), so nothing is
/// broadcast to other applications. Any key press or mouse movement wakes the displays again.
/// </summary>
public sealed class DisplayOffAction(SystemEvents systemEvents) : QuickActionBase
{
    /// <summary>
    /// Time for the panel to hide and the user's hand to leave the mouse. Without it the click that
    /// pressed the tile, or the panel's fade-out, wakes the display straight away.
    /// </summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    public override string Id => "display-off";
    public override string Title => "Turn off display";
    public override string Glyph => "\uE7F4"; // TVMonitor
    public override string? GlyphBadge => "\uE7E8"; // PowerButton
    public override ActionKind Kind => ActionKind.Button;

    protected override async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        TurnOff(systemEvents.Handle);
    }

    /// <summary>Sends SC_MONITORPOWER / MONITOR_OFF to <paramref name="hWnd"/>.</summary>
    public static void TurnOff(nint hWnd)
    {
        // PostMessage is thread-safe (we are on the thread pool) and DefWindowProc handles the command.
        if (!User32.PostMessage(hWnd, User32.WM_SYSCOMMAND, (nuint)User32.SC_MONITORPOWER, User32.MONITOR_OFF))
        {
            throw new InvalidOperationException($"PostMessage(SC_MONITORPOWER) failed: {Marshal.GetLastPInvokeError()}");
        }
    }
}
