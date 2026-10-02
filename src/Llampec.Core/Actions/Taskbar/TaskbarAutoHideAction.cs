using Llampec.Interop;
using Llampec.Platform;

namespace Llampec.Actions.Taskbar;

/// <summary>
/// "Auto-hide taskbar": the same switch as Settings &gt; Personalization &gt; Taskbar &gt; "Automatically hide
/// the taskbar", driven through SHAppBarMessage (ABM_GETSTATE / ABM_SETSTATE). The conventional setting
/// does not control the tablet-optimized taskbar, so only the normal taskbar permits writes. Explorer
/// owns persistence; Llampec does not write taskbar preferences to the registry.
/// </summary>
public sealed class TaskbarAutoHideAction : QuickActionBase
{
    private readonly Func<TaskbarMode> _readMode;
    private readonly Func<uint> _readState;
    private readonly Action<uint> _writeState;

    public TaskbarAutoHideAction() : this(TabletTaskbar.ReadMode, GetState, SetState) { }

    public TaskbarAutoHideAction(Func<TaskbarMode> readMode, Func<uint> readState, Action<uint> writeState)
    {
        _readMode = readMode ?? throw new ArgumentNullException(nameof(readMode));
        _readState = readState ?? throw new ArgumentNullException(nameof(readState));
        _writeState = writeState ?? throw new ArgumentNullException(nameof(writeState));
    }

    public override string Id => "taskbar-autohide";
    public override string Title => "Auto-hide taskbar";
    public override string Glyph => "\uE90E"; // DockBottom
    public override ActionKind Kind => ActionKind.Toggle;

    public override void Refresh()
    {
        State = (_readState() & Shell32.ABS_AUTOHIDE) != 0 ? ActionState.On : ActionState.Off;
        ApplyMode(_readMode());
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint state = _readState();
        // A posture/setting change can occur after the tile's last refresh. Recheck immediately before
        // writing so a still-enabled control cannot change a preference ineffective in tablet mode.
        ApplyMode(_readMode());
        if (!IsAvailable) return Task.CompletedTask;
        bool enable = (state & Shell32.ABS_AUTOHIDE) == 0;
        _writeState(enable ? state | Shell32.ABS_AUTOHIDE : state & ~Shell32.ABS_AUTOHIDE);
        return Task.CompletedTask;
    }

    private void ApplyMode(TaskbarMode mode)
    {
        IsAvailable = mode == TaskbarMode.Normal;
        Subtitle = mode switch
        {
            TaskbarMode.Normal => null,
            TaskbarMode.TabletOptimized => "Tablet taskbar active",
            _ => "Taskbar mode unavailable",
        };
    }

    /// <summary>Current ABS_* flags of the taskbar.</summary>
    public static uint GetState()
    {
        var data = NewData();
        return (uint)Shell32.SHAppBarMessage(Shell32.ABM_GETSTATE, ref data);
    }

    /// <summary>Applies ABS_* flags to the taskbar (ABS_ALWAYSONTOP is kept as it was).</summary>
    public static void SetState(uint flags)
    {
        var data = NewData();
        data.lParam = (nint)flags;
        Shell32.SHAppBarMessage(Shell32.ABM_SETSTATE, ref data);
    }

    private static Shell32.APPBARDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Shell32.APPBARDATA>(),
        // SETSTATE requires the taskbar window handle.
        hWnd = User32.FindWindow("Shell_TrayWnd", null),
    };
}
