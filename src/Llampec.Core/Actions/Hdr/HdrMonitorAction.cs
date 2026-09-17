using Llampec.Interop;
using Llampec.Platform;

namespace Llampec.Actions.Hdr;

/// <summary>
/// One physical display's HDR switch, identified by the adapter/target pair QueryDisplayConfig reports
/// for it. A sub-tile of <see cref="HdrAction"/>; never registered in <see cref="ActionCatalog"/> directly.
/// </summary>
public sealed class HdrMonitorAction : QuickActionBase
{
    private readonly string _title;

    public HdrMonitorAction(HdrDisplays.Display display)
    {
        AdapterId = display.AdapterId;
        TargetId = display.TargetId;
        _title = display.Name;
        IsAvailable = display.Supported;
        State = display.Enabled ? ActionState.On : ActionState.Off;
    }

    public DisplayConfig.LUID AdapterId { get; }
    public uint TargetId { get; }

    // Not a real hardware id, just something stable and unique enough for the sub-tile's lifetime.
    public override string Id => $"hdr.{unchecked((uint)AdapterId.HighPart):x8}{AdapterId.LowPart:x8}.{TargetId}";
    public override string Title => _title;
    public override string Glyph => ""; // TVMonitor
    public override ActionKind Kind => ActionKind.Toggle;

    public override void Refresh()
    {
        if (HdrDisplays.TryGetColorState(AdapterId, TargetId, out bool supported, out bool enabled))
        {
            IsAvailable = supported;
            State = enabled ? ActionState.On : ActionState.Off;
        }
        else
        {
            // Most likely this monitor was unplugged since the sub-page was built; nothing to switch.
            IsAvailable = false;
        }
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        HdrDisplays.SetEnabled(AdapterId, TargetId, enable: State != ActionState.On);
        return Task.CompletedTask;
    }
}
