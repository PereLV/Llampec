using System.Reflection;
using System.Runtime.InteropServices;
using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

/// <summary>Only the test's own hidden window is used; no system power/device changes occur.</summary>
public sealed class SystemEventsDeviceTests
{
    [Fact]
    public void RoutesOnlyExpectedHidAndPowerMessages()
    {
        using var events = new SystemEvents();
        var paths = new List<string?>();
        int suspended = 0, resumed = 0, clock = 0;
        events.HidDeviceChanged += (_, path) => paths.Add(path);
        events.Suspending += (_, _) => suspended++;
        events.Resumed += (_, _) => resumed++;
        events.ClockChanged += (_, _) => clock++;
        var procedure = typeof(SystemEvents).GetMethod("WindowProcedure", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Route(uint message, nuint parameter, nint data = 0) =>
            procedure.Invoke(events, [events.Handle, message, parameter, data]);

        Route(0x0219, 7);
        Route(0x0218, 4);
        Route(0x0218, 0x12);
        Route(0x0218, 7);
        Assert.Single(paths);
        Assert.Null(paths[0]);
        Assert.Equal(1, suspended);
        Assert.Equal(1, resumed);
        Assert.Equal(2, clock);

        const string path = @"\\?\hid#llampec-test-device";
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(path + '\0');
        nint data = Marshal.AllocHGlobal(28 + bytes.Length);
        try
        {
            Marshal.WriteInt32(data, 28 + bytes.Length);
            Marshal.WriteInt32(data, 4, 5); // DBT_DEVTYP_DEVICEINTERFACE
            Marshal.Copy(bytes, 0, data + 28, bytes.Length);
            Route(0x0219, 0x8000, data);
            Route(0x0219, 0x8004, data);
            Marshal.WriteInt32(data, 4, 2); // A volume is not a HID interface.
            Route(0x0219, 0x8000, data);
            Assert.Equal(new string?[] { null, path, path }, paths);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    [Fact]
    public void SessionEnding_is_raised_only_for_committed_end_session()
    {
        using var events = new SystemEvents();
        int endings = 0;
        events.SessionEnding += (_, _) => endings++;
        var procedure = typeof(SystemEvents).GetMethod("WindowProcedure", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Route(uint message, nuint parameter) =>
            procedure.Invoke(events, [events.Handle, message, parameter, (nint)0]);

        Route(0x0011, 0); // WM_QUERYENDSESSION must not trigger teardown.
        Route(0x0016, 0); // A cancelled shutdown must not trigger teardown.
        Assert.Equal(0, endings);
        Route(0x0016, 1); // Committed WM_ENDSESSION.
        Assert.Equal(1, endings);
    }

    [Fact]
    public void DeviceInterfaceFilterHasNativeLayout()
    {
        var type = typeof(SystemEvents).GetNestedType("DeviceInterfaceFilter", BindingFlags.NonPublic)!;
        Assert.Equal(32, Marshal.SizeOf(type));
        Assert.Equal(12, Marshal.OffsetOf(type, "ClassGuid").ToInt32());
        Assert.Equal(28, Marshal.OffsetOf(type, "Name").ToInt32());
    }
}
