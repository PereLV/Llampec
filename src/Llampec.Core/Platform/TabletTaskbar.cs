using Llampec.Interop;
using Microsoft.Win32;

namespace Llampec.Platform;

public enum TaskbarMode
{
    Normal,
    TabletOptimized,
    Unknown,
}

/// <summary>The preference is distinct from whether tablet posture currently activates it.</summary>
public sealed record TouchTaskbarStatus(bool? Enabled, bool IsAvailable, TaskbarMode Mode, string? UnavailableReason = null);

/// <summary>A verified preference change, not proof of Explorer's visual presentation.</summary>
public sealed record TouchTaskbarChangeResult(bool Succeeded, string? Message = null);

/// <summary>
/// Inference and preference control for the standard Windows shell, not a public API for its effective taskbar mode.
/// ExpandableTaskbar is an implementation detail: only explicit supported values are interpreted on a
/// tablet, and unvalidated TabletPostureTaskbar overrides make the result unknown. Writes only change
/// the current user's existing preference; they never force posture or the OEM device classification.
/// </summary>
public static class TabletTaskbar
{
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string PriorityControlKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string PreferenceName = "ExpandableTaskbar";
    private static readonly SemaphoreSlim ChangeGate = new(1, 1);

    public static TaskbarMode ReadMode()
    {
        try
        {
            var state = ReadNativeState();
            return Classify(state.Slate, state.Touch, state.Role, state.Expandable, state.Override,
                state.Remote, state.Convertibility);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            Diagnostics.Log.Warn($"Could not read taskbar mode: {ex.Message}");
            return TaskbarMode.Unknown;
        }
    }

    public static TouchTaskbarStatus ReadPreference()
    {
        try
        {
            var state = ReadNativeState();
            return ClassifyPreference(state.Slate, state.Touch, state.Role, state.Expandable,
                state.Override, state.Remote, state.Convertibility);
        }
        catch (Exception ex) when (IsNativeAccessFailure(ex))
        {
            Diagnostics.Log.Warn($"Could not read touch taskbar preference: {ex.Message}");
            return new(null, false, TaskbarMode.Unknown, "Touch taskbar unavailable");
        }
    }

    /// <summary>
    /// Changes only an existing, recognized DWORD preference on eligible hardware. The normal-user
    /// registry write is followed by WM_SETTINGCHANGE and read-back. Explorer is never restarted.
    /// This is an implementation-specific preference used by Microsoft's HOBL test tooling, not a
    /// supported Windows settings API: https://github.com/microsoft/HOBL/blob/main/docs/support/docs/HOBL_Prep.md
    /// </summary>
    public static async Task<TouchTaskbarChangeResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await ChangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Broadcasting synchronously from the UI thread can wait for another process which in
            // turn needs this UI thread. Keep the bounded native notification off the dispatcher.
            return await Task.Run(() => ChangePreference(enabled, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { ChangeGate.Release(); }
    }

    private static TouchTaskbarChangeResult ChangePreference(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = ReadPreference();
            if (!before.IsAvailable || before.Enabled is null)
                return new(false, before.UnavailableReason ?? "Touch taskbar unavailable");
            if (before.Enabled == enabled) return new(true);

            using var advanced = Registry.CurrentUser.OpenSubKey(ExplorerKey + @"\Advanced", writable: true);
            if (advanced is null || advanced.GetValue(PreferenceName) is not int current || current != (before.Enabled.Value ? 1 : 0))
                return new(false, "Touch taskbar changed elsewhere. Try again.");

            cancellationToken.ThrowIfCancellationRequested();
            advanced.SetValue(PreferenceName, enabled ? 1 : 0, RegistryValueKind.DWord);
            // After committing the preference, finish notification/read-back even if the panel closes.
            // WM_SETTINGCHANGE's documented lParam is the changed setting's registry leaf key.
            // https://learn.microsoft.com/windows/win32/winmsg/wm-settingchange
            nint notified = User32.SendMessageTimeout(User32.HWND_BROADCAST, User32.WM_SETTINGCHANGE, 0,
                "Advanced", User32.SMTO_ABORTIFHUNG, 200, out _);
            var after = ReadPreference();
            if (after.Enabled != enabled)
                return new(false, "Could not change touch taskbar.");
            // A hung unrelated top-level window can fail the broadcast even when Explorer received it.
            // Preserve the user's saved preference, but do not claim the visual change was confirmed.
            return notified != 0
                ? new(true)
                : new(true, "Touch taskbar saved; Windows refresh unconfirmed.");
        }
        catch (Exception ex) when (IsNativeAccessFailure(ex))
        {
            Diagnostics.Log.Warn($"Could not change touch taskbar preference: {ex.Message}");
            return new(false, "Could not change touch taskbar.");
        }
    }

    private static bool IsNativeAccessFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException;

    private sealed record NativeState(bool Slate, bool Touch, int Role, object? Expandable,
        bool Override, bool Remote, object? Convertibility);

    private static NativeState ReadNativeState()
    {
        using var explorer = Registry.CurrentUser.OpenSubKey(ExplorerKey, writable: false);
        using var advanced = Registry.CurrentUser.OpenSubKey(ExplorerKey + @"\Advanced", writable: false);
        bool hasOverride = HasPostureOverride(explorer) || HasPostureOverride(advanced);
        object? expandable = advanced?.GetValue(PreferenceName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        using var priorityControl = Registry.LocalMachine.OpenSubKey(PriorityControlKey, writable: false);
        object? convertibility = priorityControl?.GetValue("ConvertibilityEnabled", null, RegistryValueOptions.DoNotExpandEnvironmentNames);

        // SM_CONVERTIBLESLATEMODE does not apply to desktops. Touch capability and OEM classification
        // distinguish a convertible from a desktop with a touch monitor.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getsystemmetrics
        return new(User32.GetSystemMetrics(0x2003 /* SM_CONVERTIBLESLATEMODE */) == 0,
            (User32.GetSystemMetrics(94 /* SM_DIGITIZER */) & 0x01 /* NID_INTEGRATED_TOUCH */) != 0,
            PowrProf.PowerDeterminePlatformRoleEx(2 /* POWER_PLATFORM_ROLE_V2 */), expandable,
            hasOverride, User32.GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0, convertibility);
    }

    internal static TouchTaskbarStatus ClassifyPreference(bool slateMode, bool integratedTouch, int platformRole,
        object? expandableTaskbar, bool hasPostureOverride, bool remoteSession = false, object? convertibilityEnabled = null)
    {
        bool? enabled = expandableTaskbar is int v && v is 0 or 1 ? v == 1 : null;
        var mode = Classify(slateMode, integratedTouch, platformRole, expandableTaskbar, hasPostureOverride,
            remoteSession, convertibilityEnabled);
        if (hasPostureOverride || remoteSession || enabled is null || convertibilityEnabled is not null and not int)
            return new(enabled, false, mode, "Touch taskbar unavailable");

        bool eligible = integratedTouch && (convertibilityEnabled is int c ? c != 0 : platformRole is 2 or 8);
        return new(enabled, eligible, mode, eligible ? null : "Not supported");
    }

    private static bool HasPostureOverride(RegistryKey? key) => key?.GetValueNames()
        .Contains("TabletPostureTaskbar", StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Pure classification, separated from Windows reads so no settings need changing in tests.</summary>
    internal static TaskbarMode Classify(bool slateMode, bool integratedTouch, int platformRole,
        object? expandableTaskbar, bool hasPostureOverride, bool remoteSession = false, object? convertibilityEnabled = null)
    {
        // Override precedence and numeric meanings have not been validated; do not invent a mapping.
        if (hasPostureOverride) return TaskbarMode.Unknown;
        if (expandableTaskbar is int and 0 || convertibilityEnabled is int and 0) return TaskbarMode.Normal;
        if (!slateMode || !integratedTouch) return TaskbarMode.Normal;

        // Microsoft documents DWORD 0 as a convertibility opt-out and ANY nonzero DWORD as opt-in.
        // An explicit opt-in takes precedence over form-factor inference, including a Desktop role.
        // https://learn.microsoft.com/windows-hardware/customize/desktop/settings-for-better-tablet-experiences
        if (convertibilityEnabled is not null and not int) return TaskbarMode.Unknown;

        // Without an explicit convertibility preference, infer eligibility from the OEM power role.
        // Public POWER_PLATFORM_ROLE values: Mobile=2, Slate=8; Desktop=1 and roles 3..7 are not tablets.
        // https://learn.microsoft.com/windows/win32/api/winnt/ne-winnt-power_platform_role
        if (convertibilityEnabled is null && platformRole is not (2 or 8))
            return platformRole is 1 or >= 3 and <= 7 ? TaskbarMode.Normal : TaskbarMode.Unknown;

        // Local posture/touch information is insufficient to infer a remote session's tablet shell.
        if (remoteSession) return TaskbarMode.Unknown;

        // An absent/malformed setting on eligible tablet hardware is not evidence of an OS default.
        return expandableTaskbar is int and 1 ? TaskbarMode.TabletOptimized : TaskbarMode.Unknown;
    }
}
