using Llampec.Interop;

namespace Llampec.Actions.Taskbar;

/// <summary>
/// "Auto-hide taskbar": the same switch as Settings &gt; Personalization &gt; Taskbar &gt; "Automatically hide
/// the taskbar", driven through SHAppBarMessage (ABM_GETSTATE / ABM_SETSTATE). Explorer applies the change
/// immediately and persists it itself, so there is nothing to write to the registry.
/// </summary>
public sealed class TaskbarAutoHideAction : QuickActionBase
{
    public override string Id => "taskbar-autohide";
    public override string Title => "Auto-hide taskbar";
    public override string Glyph => "\uE90E"; // DockBottom
    public override ActionKind Kind => ActionKind.Toggle;

    public override void Refresh()
    {
        State = (GetState() & Shell32.ABS_AUTOHIDE) != 0 ? ActionState.On : ActionState.Off;
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        uint state = GetState();
        bool enable = (state & Shell32.ABS_AUTOHIDE) == 0;
        SetState(enable ? state | Shell32.ABS_AUTOHIDE : state & ~Shell32.ABS_AUTOHIDE);
        return Task.CompletedTask;
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
        // Documented as unused for GETSTATE/SETSTATE, but the shell is happier with the taskbar's own HWND.
        hWnd = User32.FindWindow("Shell_TrayWnd", null),
    };
}
