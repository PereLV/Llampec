// Power plans and button actions use the documented power management API (powersetting.h,
// powerbase.h, winnt.h): https://learn.microsoft.com/windows/win32/power/power-management-functions
// Power mode is the one exception approved for Llampec: Windows has no documented setter, so it
// calls the powrprof.dll overlay functions that Windows Settings uses. Missing exports fail cleanly.
using System.ComponentModel;

namespace Llampec.Platform;

public enum PowerMode { BestEfficiency, Balanced, BestPerformance, Other }

public enum PowerButtonSetting { Lid, PowerButton }

public sealed record PowerPlan(Guid Id, string Name);

/// <summary>One choice for a lid or power-button action; Value is the Windows setting value.</summary>
public sealed record PowerActionChoice(uint Value, string? SystemName);

public sealed record PowerButtonState(PowerButtonSetting Setting, IReadOnlyList<PowerActionChoice> Choices, uint Battery, uint PluggedIn);

public sealed record BatteryHealth(int? Percent, bool Charging, double? RateWatts, double? FullWattHours, double? DesignWattHours)
{
    public int? HealthPercent => FullWattHours is > 0 && DesignWattHours is > 0
        ? (int)Math.Round(100 * FullWattHours.Value / DesignWattHours.Value) : null;
}

/// <summary>The few values the tile needs, read each time the panel opens.</summary>
public sealed record PowerSnapshot(
    bool OnBattery, bool HasBattery, int? BatteryPercent,
    PowerMode? Mode, bool ModeSupported, bool EnergySaverOn, Guid ActivePlan);

/// <summary>Options-page values, read only while that page is open.</summary>
/// <param name="EnergySaverThreshold">Battery percentage that turns energy saver on: 0 never, 100 always.</param>
public sealed record PowerDetails(
    int? EnergySaverThreshold, IReadOnlyList<PowerPlan> Plans,
    PowerButtonState? Lid, PowerButtonState? PowerButton);

public static partial class PowerOptions
{
    public static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid BestEfficiencyOverlay = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformanceOverlay = new("ded574b5-45a0-4f42-8737-46345c09c238");
    private static readonly Guid ButtonSubgroup = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid LidCloseAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    private static readonly Guid PowerButtonAction = new("7648efa3-dd9c-4e3e-b566-50f929386280");
    private static readonly Guid EnergySaverSubgroup = new("de830923-a562-41af-a086-e3a2c6bad2da");
    private static readonly Guid EnergySaverBatteryThreshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");
    private const uint AccessScheme = 16;
    private const int ErrorNoMoreItems = 259;
    private const uint HibernateValue = 2;

    public static PowerSnapshot Read()
    {
        var status = GetSystemPowerStatus(out var power) ? power : default;
        bool hasBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0;
        int? percent = hasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
        Guid active = ActivePlan();
        bool modeSupported = active == BalancedPlan;
        PowerMode? mode = null;
        if (modeSupported)
        {
            try { mode = CurrentMode(); }
            catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException or Win32Exception)
            {
                Diagnostics.Log.Warn($"Power mode is unavailable: {error.Message}");
                modeSupported = false;
            }
        }
        // SYSTEM_POWER_STATUS.SystemStatusFlag is the energy saver state Microsoft documents for apps.
        return new(status.ACLineStatus == 0, hasBattery, percent, mode, modeSupported, status.SystemStatusFlag == 1, active);
    }

    public static PowerDetails ReadDetails(PowerSnapshot snapshot)
    {
        Guid active = snapshot.ActivePlan;
        int? threshold = snapshot.HasBattery
            && PowerReadDCValueIndex(0, active, EnergySaverSubgroup, EnergySaverBatteryThreshold, out uint value) == 0
            ? (int)Math.Min(value, 100) : null;
        var capabilities = Capabilities();
        return new(threshold, Plans(),
            capabilities.LidPresent ? ReadButton(PowerButtonSetting.Lid, active, capabilities.CanHibernate) : null,
            capabilities.PowerButtonPresent ? ReadButton(PowerButtonSetting.PowerButton, active, capabilities.CanHibernate) : null);
    }

    /// <summary>The mode Windows applies for the current power source (plugged in or battery).</summary>
    public static PowerMode CurrentMode()
    {
        Check(PowerGetActualOverlayScheme(out Guid overlay));
        return overlay == Guid.Empty ? PowerMode.Balanced
            : overlay == BestEfficiencyOverlay ? PowerMode.BestEfficiency
            : overlay == BestPerformanceOverlay ? PowerMode.BestPerformance
            : PowerMode.Other;
    }

    public static void SetMode(PowerMode mode)
    {
        Guid overlay = mode switch
        {
            PowerMode.BestEfficiency => BestEfficiencyOverlay,
            PowerMode.Balanced => Guid.Empty,
            PowerMode.BestPerformance => BestPerformanceOverlay,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (ActivePlan() != BalancedPlan) throw new InvalidOperationException("Power mode requires the Balanced plan.");
        Check(PowerSetActiveOverlayScheme(overlay));
        if (CurrentMode() != mode) throw new InvalidOperationException("Windows did not apply the power mode.");
    }

    public static void SetPlan(Guid plan)
    {
        Check(PowerSetActiveScheme(0, plan));
        if (ActivePlan() != plan) throw new InvalidOperationException("Windows did not apply the power plan.");
    }

    /// <summary>
    /// Writes one button action to every plan, as Control Panel does, then reapplies the active plan.
    /// </summary>
    public static void SetButtonAction(PowerButtonSetting setting, bool battery, uint value)
    {
        Guid id = setting == PowerButtonSetting.Lid ? LidCloseAction : PowerButtonAction;
        Guid active = ActivePlan();
        Write(active);
        foreach (var plan in Plans().Where(plan => plan.Id != active))
        {
            try { Write(plan.Id); }
            catch (Win32Exception error) { Diagnostics.Log.Warn($"Could not update power plan {plan.Name}: {error.Message}"); }
        }
        Check(PowerSetActiveScheme(0, active));
        uint applied = 0;
        Check(battery ? PowerReadDCValueIndex(0, active, ButtonSubgroup, id, out applied)
            : PowerReadACValueIndex(0, active, ButtonSubgroup, id, out applied));
        if (applied != value) throw new InvalidOperationException("Windows did not apply the button action.");

        void Write(Guid plan) => Check(battery ? PowerWriteDCValueIndex(0, plan, ButtonSubgroup, id, value)
            : PowerWriteACValueIndex(0, plan, ButtonSubgroup, id, value));
    }

    public static BatteryHealth? ReadBattery()
    {
        var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
        if (report.Status == Windows.System.Power.BatteryStatus.NotPresent) return null;
        int? percent = report.RemainingCapacityInMilliwattHours is int remaining && report.FullChargeCapacityInMilliwattHours is int full and > 0
            ? (int)Math.Round(100.0 * remaining / full) : null;
        return new(percent, report.Status == Windows.System.Power.BatteryStatus.Charging,
            report.ChargeRateInMilliwatts is int rate && rate != 0 ? Math.Abs(rate) / 1000.0 : null,
            report.FullChargeCapacityInMilliwattHours / 1000.0, report.DesignCapacityInMilliwattHours / 1000.0);
    }

    private static Guid ActivePlan()
    {
        Check(PowerGetActiveScheme(0, out nint pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    private static unsafe IReadOnlyList<PowerPlan> Plans()
    {
        var plans = new List<PowerPlan>();
        for (uint index = 0; ; index++)
        {
            Guid plan;
            uint size = (uint)sizeof(Guid);
            int result = PowerEnumerate(0, 0, 0, AccessScheme, index, (byte*)&plan, ref size);
            if (result == ErrorNoMoreItems) break;
            Check(result);
            plans.Add(new(plan, FriendlyName(plan)));
        }
        return plans;
    }

    private static unsafe string FriendlyName(Guid plan)
    {
        uint size = 0;
        if (PowerReadFriendlyName(0, &plan, 0, 0, null, ref size) != 0 || size < 2) return plan.ToString();
        char[] buffer = new char[(size + 1) / 2];
        fixed (char* data = buffer)
        {
            if (PowerReadFriendlyName(0, &plan, 0, 0, (byte*)data, ref size) != 0) return plan.ToString();
            return new string(data).Trim();
        }
    }

    private static PowerButtonState? ReadButton(PowerButtonSetting setting, Guid plan, bool canHibernate)
    {
        Guid id = setting == PowerButtonSetting.Lid ? LidCloseAction : PowerButtonAction;
        if (PowerReadDCValueIndex(0, plan, ButtonSubgroup, id, out uint battery) != 0
            || PowerReadACValueIndex(0, plan, ButtonSubgroup, id, out uint pluggedIn) != 0) return null;
        // An action setting stores the index of its possible value (0 do nothing, 1 sleep,
        // 2 hibernate, 3 shut down, 4 turn off the display), so the index is the value.
        var choices = new List<PowerActionChoice>();
        for (uint index = 0; index < 16; index++)
        {
            if (PossibleName(id, index) is not { } name) break;
            if (index == HibernateValue && !canHibernate && battery != index && pluggedIn != index) continue;
            choices.Add(new(index, name));
        }
        if (choices.Count == 0) return null;
        return new(setting, choices, battery, pluggedIn);
    }

    private static unsafe string? PossibleName(Guid setting, uint index)
    {
        Guid subgroup = ButtonSubgroup;
        uint size = 0;
        if (PowerReadPossibleFriendlyName(0, &subgroup, &setting, index, null, ref size) != 0 || size < 2) return null;
        char[] buffer = new char[(size + 1) / 2];
        fixed (char* data = buffer)
        {
            if (PowerReadPossibleFriendlyName(0, &subgroup, &setting, index, (byte*)data, ref size) != 0) return null;
            return new string(data).Trim();
        }
    }

    private static unsafe (bool PowerButtonPresent, bool LidPresent, bool CanHibernate) Capabilities()
    {
        // SYSTEM_POWER_CAPABILITIES (winnt.h) is 76 bytes; only its leading BOOLEAN fields are read.
        byte* capabilities = stackalloc byte[128];
        if (!GetPwrCapabilities(capabilities)) return (false, false, false);
        return (capabilities[0] != 0, capabilities[2] != 0, capabilities[6] != 0 && capabilities[8] != 0);
    }

    private static void Check(int result)
    {
        if (result != 0) throw new Win32Exception(result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool GetPwrCapabilities(byte* capabilities);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerGetActiveScheme(nint rootPowerKey, out nint activePolicy);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerSetActiveScheme(nint rootPowerKey, in Guid scheme);

    [LibraryImport("powrprof.dll")]
    private static unsafe partial int PowerEnumerate(nint rootPowerKey, nint scheme, nint subgroup, uint accessFlags,
        uint index, byte* buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    private static unsafe partial int PowerReadFriendlyName(nint rootPowerKey, Guid* scheme, nint subgroup, nint setting,
        byte* buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerReadACValueIndex(nint rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerReadDCValueIndex(nint rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerWriteACValueIndex(nint rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerWriteDCValueIndex(nint rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    private static unsafe partial int PowerReadPossibleFriendlyName(nint rootPowerKey, Guid* subgroup, Guid* setting, uint index,
        byte* buffer, ref uint size);

    // Undocumented powrprof.dll exports used by Windows Settings for "Power mode".
    [LibraryImport("powrprof.dll")]
    private static partial int PowerGetActualOverlayScheme(out Guid overlay);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerSetActiveOverlayScheme(in Guid overlay);
}
