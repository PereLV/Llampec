using System.Threading.Channels;
using System.Text.Json;
using Llampec.Devices.Logitech;
using Xunit;

namespace Llampec.Tests;

public class LogitechDeviceTests
{
    [Fact]
    public async Task Device_unit_id_is_read_from_device_info_without_writes()
    {
        var transport = new FeatureDeviceTransport { DeviceUnitId = 0xA012C4F8 };
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        Assert.Equal("A012C4F8", device.UnitId);
        Assert.Empty(transport.Mutations);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("zero")]
    [InlineData("version0")]
    [InlineData("unsupported")]
    public async Task Missing_unit_identity_does_not_hide_other_capabilities(string identity)
    {
        var transport = new FeatureDeviceTransport();
        if (identity == "absent") transport.RemoveFeature(0x0003);
        if (identity == "zero") transport.DeviceUnitId = 0;
        if (identity == "version0") transport.DeviceInfoVersionZero = true;
        if (identity == "unsupported") transport.DeviceInfoUnsupported = true;
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        Assert.Null(device.UnitId);
        Assert.NotNull(device.Dpi);
        Assert.NotEmpty(device.Controls);
        Assert.Empty(transport.Mutations);
    }

    [Fact]
    public async Task Discovery_without_a_thumb_button_still_reads_dpi_and_never_mutates_the_device()
    {
        var transport = new FeatureDeviceTransport { ControlId = 0x0056 };
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        Assert.Equal((ushort)0x0056, Assert.Single(device.Controls).Id);
        Assert.Equal((ushort)1000, Assert.IsType<LogitechDpiState>(device.Dpi).Current);
        Assert.Equal(new LogitechDpiRange(200, 8000, 50), device.Dpi.Range);
        Assert.Empty(transport.Mutations);
        await Assert.ThrowsAsync<NotSupportedException>(() => device.DivertThumbAsync(_ => { }));
        Assert.Empty(transport.Mutations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(225)]
    [InlineData(8050)]
    public async Task Invalid_dpi_never_writes_sensor_settings(int dpi)
    {
        var transport = new FeatureDeviceTransport();
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);
        int requestsBefore = transport.Requests.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.SetDpiAsync((ushort)dpi));

        Assert.Equal(requestsBefore, transport.Requests.Count);
        Assert.Empty(transport.Mutations);
        Assert.Equal((ushort)1000, transport.Dpi);
    }

    [Fact]
    public async Task Discrete_dpi_values_are_discovered_without_becoming_a_continuous_range()
    {
        var transport = new FeatureDeviceTransport { DpiValues = [400, 800, 1600] };
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        Assert.Null(device.Dpi!.Range);
        Assert.Equal(new ushort[] { 400, 800, 1600 }, device.Dpi.Values);
        Assert.True(device.Dpi.Supports(800));
        Assert.False(device.Dpi.Supports(1000));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.SetDpiAsync(1000));
        Assert.Empty(transport.Mutations);
    }

    [Fact]
    public async Task Inversion_changes_and_restoration_preserve_current_unowned_wheel_bits()
    {
        var transport = new FeatureDeviceTransport { VerticalMode = 2 };
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        await device.SetWheelInversionAsync(vertical: true, horizontal: true);
        Assert.Equal((byte)6, transport.VerticalMode); // Keep high resolution, add inversion.
        Assert.True(transport.HorizontalInverted);
        Assert.Equal(new byte[] { 6 }, transport.Mutations.Single(r => r.Feature == 0x2121).Parameters[..1]);
        Assert.Equal(new byte[] { 0, 1, 0 }, transport.Mutations.Single(r => r.Feature == 0x2150).Parameters[..3]);

        // Simulate another owner changing routing and vertical resolution after our change.
        transport.VerticalMode = 5;
        transport.HorizontalReportingMode = 1;
        await device.RestoreAsync();

        Assert.Equal((byte)1, transport.VerticalMode); // Restore only inversion; retain current routing/resolution.
        Assert.Equal((byte)1, transport.HorizontalReportingMode);
        Assert.False(transport.HorizontalInverted);
        Assert.Equal(new byte[] { 1, 0, 0 }, transport.Mutations.Last(r => r.Feature == 0x2150).Parameters[..3]);
        int count = transport.Mutations.Count;
        await device.RestoreAsync();
        Assert.Equal(count, transport.Mutations.Count);
    }

    [Fact]
    public async Task Both_wheel_axes_are_validated_before_either_is_changed()
    {
        var transport = new FeatureDeviceTransport { HorizontalReportingMode = 1 };
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.SetWheelInversionAsync(true, true));

        Assert.Empty(transport.Mutations);
        Assert.Equal((byte)2, transport.VerticalMode);
    }

    [Fact]
    public async Task Thumb_diversion_changes_only_temporary_diversion_and_preserves_existing_mapping()
    {
        var transport = new FeatureDeviceTransport { ReportingFlags = 0x0200, MappedTo = 0x0056 };
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        await device.DivertThumbAsync(_ => { });

        Assert.Equal((ushort)0x0201, transport.ReportingFlags);
        Assert.Equal((ushort)0x0201, Assert.Single(device.Controls).ReportingFlags);
        Assert.Equal((ushort)0x0056, transport.MappedTo);
        Assert.Equal(new byte[] { 0, 0xC3, 3, 0, 0, 0 }, Assert.Single(transport.Mutations).Parameters[..6]);
        await device.RestoreAsync();
        Assert.Equal((ushort)0x0200, transport.ReportingFlags);
        Assert.Equal((ushort)0x0200, Assert.Single(device.Controls).ReportingFlags);
        Assert.Equal((ushort)0x0056, transport.MappedTo);
        Assert.Equal(new byte[] { 0, 0xC3, 2, 0, 0, 0 }, transport.Mutations.Last().Parameters[..6]);
    }

    [Fact]
    public async Task Dpi_write_applied_without_a_reply_is_rolled_back_using_the_pre_write_snapshot()
    {
        var transport = new FeatureDeviceTransport { DroppedDpiSetReplies = 1 };
        await using var client = new HidppClient(transport, requestTimeout: TimeSpan.FromMilliseconds(150));
        await using var device = await LogitechDevice.CreateAsync(client);

        await Assert.ThrowsAsync<TimeoutException>(() => device.SetDpiAsync(1600));

        Assert.Equal(new ushort[] { 1600, 1000 }, transport.DpiWrites);
        Assert.Equal((ushort)1000, transport.Dpi);
        Assert.Equal((ushort)1000, device.Dpi!.Current);
        await device.RestoreAsync();
        Assert.Equal(2, transport.DpiWrites.Count);
    }

    [Fact]
    public async Task Restoration_failure_retains_the_original_snapshot_for_an_explicit_retry()
    {
        var transport = new FeatureDeviceTransport { DroppedDpiSetReplies = 2 };
        await using var client = new HidppClient(transport, requestTimeout: TimeSpan.FromMilliseconds(150));
        await using var device = await LogitechDevice.CreateAsync(client);

        await Assert.ThrowsAsync<AggregateException>(() => device.SetDpiAsync(1600));
        Assert.Equal(new ushort[] { 1600, 1000 }, transport.DpiWrites);

        await device.RestoreAsync();

        Assert.Equal(new ushort[] { 1600, 1000, 1000 }, transport.DpiWrites);
        Assert.Equal((ushort)1000, device.Dpi!.Current);
        await device.RestoreAsync();
        Assert.Equal(3, transport.DpiWrites.Count);
    }

    [Fact]
    public async Task Repeated_dpi_changes_restore_the_first_snapshot_instead_of_the_last_change()
    {
        var transport = new FeatureDeviceTransport();
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        await device.SetDpiAsync(1600);
        await device.SetDpiAsync(2000);
        await device.RestoreAsync();

        Assert.Equal(new ushort[] { 1600, 2000, 1000 }, transport.DpiWrites);
        Assert.Equal((ushort)1000, transport.Dpi);
    }

    [Fact]
    public async Task Multiple_controls_track_simultaneous_presses_and_restore_each_original_mapping()
    {
        var transport = new FeatureDeviceTransport { ReportingFlags = 0x0200, MappedTo = 0x0056 };
        transport.AdditionalControls.Add(0x0056, new(0x0100, 0x0052));
        var events = Channel.CreateUnbounded<(ushort Id, bool Down)>();
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        await device.DivertThumbAsync(down => events.Writer.TryWrite((0x00C3, down)));
        await device.DivertButtonAsync(0x0056, down => events.Writer.TryWrite((0x0056, down)));
        transport.NotifyButtons(0x00C3, 0x0056);
        Assert.Equal(((ushort)0x00C3, true), await NextEvent(events));
        Assert.Equal(((ushort)0x0056, true), await NextEvent(events));
        transport.NotifyButtons(0x0056);
        Assert.Equal(((ushort)0x00C3, false), await NextEvent(events));

        await device.RestoreAsync();

        Assert.Equal(((ushort)0x0056, false), await NextEvent(events));
        Assert.False(events.Reader.TryRead(out _));
        Assert.Equal((ushort)0x0200, transport.ReportingFlags);
        Assert.Equal((ushort)0x0056, transport.MappedTo);
        Assert.Equal(new FeatureDeviceTransport.ControlState(0x0100, 0x0052), transport.AdditionalControls[0x0056]);
        Assert.All(device.Controls, control => Assert.Equal(0, control.ReportingFlags & 1));
        int writes = transport.Mutations.Count;
        await device.RestoreAsync();
        Assert.Equal(writes, transport.Mutations.Count);
    }

    [Fact]
    public async Task Externally_diverted_control_is_never_claimed_or_restored_by_this_session()
    {
        var transport = new FeatureDeviceTransport { ReportingFlags = 1 };
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.DivertButtonAsync(0x00C3, _ => { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            device.CaptureRestoreStateAsync([0x00C3], false, false, false, false));
        await device.RestoreAsync();

        Assert.Empty(transport.Mutations);
        Assert.Equal((ushort)1, transport.ReportingFlags);
    }

    [Fact]
    public async Task Cancellation_after_a_second_button_write_restores_both_owned_controls()
    {
        var transport = new FeatureDeviceTransport();
        transport.AdditionalControls.Add(0x0056, new(0x0200, 0x0052));
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);
        await device.DivertButtonAsync(0x00C3, _ => { });
        using var cancellation = new CancellationTokenSource();
        transport.CancelAfterNextControlWrite = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            device.DivertButtonAsync(0x0056, _ => { }, cancellation.Token));

        Assert.Equal((ushort)0, transport.ReportingFlags);
        Assert.Equal(new FeatureDeviceTransport.ControlState(0x0200, 0x0052), transport.AdditionalControls[0x0056]);
        Assert.Equal(4, transport.Mutations.Count); // Two diverts, two restores.
        await device.RestoreAsync();
        Assert.Equal(4, transport.Mutations.Count);
    }

    [Fact]
    public async Task Durable_snapshot_is_read_only_serializable_and_restores_a_reconnected_device_without_undo_on_dispose()
    {
        var transport = new FeatureDeviceTransport { ReportingFlags = 0x0200, MappedTo = 0x0056 };
        transport.AdditionalControls.Add(0x0056, new(0x0100, 0x0052));
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);
        var captured = await device.CaptureRestoreStateAsync([0x00C3, 0x0056], true, true, true, true);

        Assert.Empty(transport.Mutations);
        Assert.Equal((ushort)1000, captured.Dpi);
        Assert.Equal((byte)2, captured.SmartMode);
        Assert.Equal((byte)25, captured.SmartThreshold);
        Assert.Equal(false, captured.VerticalInvert);
        Assert.Equal(false, captured.HorizontalInvert);
        Assert.Equal(new ushort[] { 0x00C3, 0x0056 }, captured.Controls.Select(c => c.Id));
        Assert.All(captured.Controls, control => Assert.False(control.Diverted));
        string journal = JsonSerializer.Serialize(captured);
        var persisted = JsonSerializer.Deserialize<LogitechRestoreState>(journal)!;

        // A new transport/session sees settings left by an interrupted process.
        var recoveredTransport = new FeatureDeviceTransport
        {
            ReportingFlags = 0x0201, MappedTo = 0x0056, Dpi = 1600,
            VerticalMode = 0x86, HorizontalInverted = true,
            SmartMode = 1, SmartThreshold = 40, SmartThirdByte = 85,
        };
        recoveredTransport.AdditionalControls.Add(0x0056, new(0x0101, 0x0052));
        await using var recoveredClient = new HidppClient(recoveredTransport);
        var recoveredDevice = await LogitechDevice.CreateAsync(recoveredClient);
        await recoveredDevice.ApplyRestoreStateAsync(persisted);

        Assert.Equal((ushort)1000, recoveredTransport.Dpi);
        Assert.Equal((byte)2, recoveredTransport.SmartMode);
        Assert.Equal((byte)25, recoveredTransport.SmartThreshold);
        Assert.Equal((byte)85, recoveredTransport.SmartThirdByte); // Torque was never owned.
        Assert.Equal((byte)0x82, recoveredTransport.VerticalMode); // Preserve current reserved/resolution bits.
        Assert.False(recoveredTransport.HorizontalInverted);
        Assert.Equal((ushort)0x0200, recoveredTransport.ReportingFlags);
        Assert.Equal(new FeatureDeviceTransport.ControlState(0x0100, 0x0052), recoveredTransport.AdditionalControls[0x0056]);
        int writes = recoveredTransport.Mutations.Count;
        await recoveredDevice.DisposeAsync();
        Assert.Equal(writes, recoveredTransport.Mutations.Count);
    }

    [Fact]
    public async Task Recovery_fails_before_any_write_if_a_required_capability_is_missing()
    {
        var transport = new FeatureDeviceTransport();
        transport.RemoveFeature(0x2150);
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);
        var state = new LogitechRestoreState(1000, null, null, false, false, []);

        await Assert.ThrowsAsync<NotSupportedException>(() => device.ApplyRestoreStateAsync(state));

        Assert.Empty(transport.Mutations);
    }

    [Fact]
    public async Task Recovery_retains_failed_targets_for_retry_and_does_not_restore_unselected_properties()
    {
        var transport = new FeatureDeviceTransport { Dpi = 1600, DroppedDpiSetReplies = 1 };
        await using var client = new HidppClient(transport, requestTimeout: TimeSpan.FromMilliseconds(100));
        await using var device = await LogitechDevice.CreateAsync(client);
        var state = new LogitechRestoreState(1000, null, null, null, null, []);

        await Assert.ThrowsAsync<AggregateException>(() => device.ApplyRestoreStateAsync(state));
        await device.ApplyRestoreStateAsync(state);

        Assert.Equal((ushort)1000, transport.Dpi);
        Assert.All(transport.Mutations, request => Assert.Equal((ushort)0x2201, request.Feature));
        int writes = transport.Mutations.Count;
        await device.RestoreAsync();
        Assert.Equal(writes, transport.Mutations.Count);
    }

    [Fact]
    public async Task Durable_baseline_wins_when_device_changes_between_capture_and_first_write()
    {
        var transport = new FeatureDeviceTransport();
        await using var client = new HidppClient(transport);
        await using var device = await LogitechDevice.CreateAsync(client);
        var baseline = await device.CaptureRestoreStateAsync([], true, false, false, false);
        transport.Dpi = 1200; // Changed externally after the durable snapshot was captured.
        await device.SetDpiAsync(1600);
        await device.ApplyRestoreStateAsync(baseline);

        Assert.Equal(new ushort[] { 1600, 1000 }, transport.DpiWrites);
        await device.DisposeAsync();
        Assert.Equal(new ushort[] { 1600, 1000 }, transport.DpiWrites);
    }

    private static async Task<(ushort Id, bool Down)> NextEvent(Channel<(ushort Id, bool Down)> events) =>
        await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

    private sealed class FeatureDeviceTransport : IHidTransport
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Dictionary<ushort, byte> _features = new()
        {
            [0x1B04] = 1,
            [0x2201] = 2,
            [0x2121] = 3,
            [0x2150] = 4,
            [0x2111] = 5,
            [0x0003] = 6,
        };

        public int InputReportLength => 20;
        public int OutputReportLength => 20;
        public uint DeviceUnitId { get; set; } = 0x12345678;
        public bool DeviceInfoVersionZero { get; set; }
        public bool DeviceInfoUnsupported { get; set; }
        public ushort ControlId { get; init; } = 0x00C3;
        public ushort ReportingFlags { get; set; }
        public ushort MappedTo { get; set; } = 0x00C3;
        public Dictionary<ushort, ControlState> AdditionalControls { get; } = [];
        public ushort Dpi { get; set; } = 1000;
        public ushort[] DpiValues { get; init; } = [200, 0xE032, 8000];
        public byte VerticalMode { get; set; } = 2;
        public byte HorizontalReportingMode { get; set; }
        public bool HorizontalInverted { get; set; }
        public byte SmartMode { get; set; } = 2;
        public byte SmartThreshold { get; set; } = 25;
        public byte SmartThirdByte { get; set; } = 70;
        public int DroppedDpiSetReplies { get; set; }
        public CancellationTokenSource? CancelAfterNextControlWrite { get; set; }
        public List<Request> Requests { get; } = [];
        public List<Request> Mutations { get; } = [];
        public List<ushort> DpiWrites { get; } = [];

        public void RemoveFeature(ushort feature) => _features.Remove(feature);

        public void NotifyButtons(params ushort[] controls)
        {
            byte[] report = new byte[20];
            report[0] = 0x11;
            report[1] = 0xFF;
            report[2] = _features[0x1B04];
            for (int i = 0; i < controls.Length; i++) PutU16(report, 4 + i * 2, controls[i]);
            Assert.True(_incoming.Writer.TryWrite(report));
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] report = await _incoming.Reader.ReadAsync(cancellationToken);
            report.CopyTo(buffer);
            return report.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] wire = report.ToArray();
            byte[] reply = new byte[20];
            wire.AsSpan(0, 4).CopyTo(reply);
            reply[0] = 0x11;
            ushort feature = wire[2] == 0 ? (ushort)0 : _features.Single(pair => pair.Value == wire[2]).Key;
            byte function = (byte)(wire[3] >> 4);
            var request = new Request(feature, function, wire[4..]);
            Requests.Add(request);
            byte[] parameters = request.Parameters;
            switch ((feature, function))
            {
                case (0, 0):
                    _features.TryGetValue(U16(parameters, 0), out byte index);
                    reply[4] = index;
                    break;
                case (0x0003, 0):
                    if (DeviceInfoUnsupported)
                    {
                        reply[2] = 0xFF;
                        reply[3] = wire[2];
                        reply[4] = wire[3];
                        reply[5] = 0x09;
                    }
                    else
                    {
                        reply[4] = 1;
                        if (DeviceInfoVersionZero) reply[0] = 0x10;
                        else
                        {
                            PutU16(reply, 5, (ushort)(DeviceUnitId >> 16));
                            PutU16(reply, 7, (ushort)DeviceUnitId);
                        }
                    }
                    break;
                case (0x1B04, 0):
                    reply[4] = (byte)(1 + AdditionalControls.Count);
                    break;
                case (0x1B04, 1):
                    ushort infoCid = parameters[0] == 0 ? ControlId : AdditionalControls.Keys.ElementAt(parameters[0] - 1);
                    PutU16(reply, 4, infoCid);
                    PutU16(reply, 6, infoCid);
                    reply[8] = 0x20; // Divertable.
                    reply[9] = 1;
                    break;
                case (0x1B04, 2):
                    ushort readCid = U16(parameters, 0);
                    var readControl = GetControl(readCid);
                    PutU16(reply, 4, readCid);
                    reply[6] = (byte)readControl.Flags;
                    PutU16(reply, 7, readControl.MappedTo);
                    reply[9] = (byte)(readControl.Flags >> 8);
                    break;
                case (0x1B04, 3):
                    Mutations.Add(request);
                    ushort writeCid = U16(parameters, 0);
                    var writeControl = GetControl(writeCid);
                    if ((parameters[2] & 2) != 0)
                        writeControl = writeControl with { Flags = (ushort)((writeControl.Flags & ~1) | (parameters[2] & 1)) };
                    ushort mappedTo = U16(parameters, 3);
                    if (mappedTo != 0) writeControl = writeControl with { MappedTo = mappedTo };
                    if (writeCid == ControlId) { ReportingFlags = writeControl.Flags; MappedTo = writeControl.MappedTo; }
                    else AdditionalControls[writeCid] = writeControl;
                    if (CancelAfterNextControlWrite is { } cancellation)
                    {
                        CancelAfterNextControlWrite = null;
                        cancellation.Cancel();
                        return ValueTask.CompletedTask;
                    }
                    break;
                case (0x2201, 0):
                    reply[4] = 1;
                    break;
                case (0x2201, 1):
                    for (int i = 0; i < DpiValues.Length; i++) PutU16(reply, 5 + 2 * i, DpiValues[i]);
                    break;
                case (0x2201, 2):
                    PutU16(reply, 5, Dpi);
                    PutU16(reply, 7, 1000);
                    break;
                case (0x2201, 3):
                    Mutations.Add(request);
                    Dpi = U16(parameters, 1);
                    DpiWrites.Add(Dpi);
                    if (DroppedDpiSetReplies > 0)
                    {
                        DroppedDpiSetReplies--;
                        return ValueTask.CompletedTask;
                    }
                    break;
                case (0x2121, 0):
                    reply[5] = 8; // Native inversion supported.
                    break;
                case (0x2121, 1):
                    reply[4] = VerticalMode;
                    break;
                case (0x2121, 2):
                    Mutations.Add(request);
                    VerticalMode = parameters[0];
                    break;
                case (0x2150, 1):
                    reply[4] = HorizontalReportingMode;
                    reply[5] = (byte)(HorizontalInverted ? 1 : 0);
                    break;
                case (0x2150, 2):
                    Mutations.Add(request);
                    HorizontalReportingMode = parameters[0];
                    HorizontalInverted = (parameters[1] & 1) != 0;
                    break;
                case (0x2111, 1):
                    reply[4] = SmartMode;
                    reply[5] = SmartThreshold;
                    reply[6] = SmartThirdByte;
                    break;
                case (0x2111, 2):
                    Mutations.Add(request);
                    if (parameters[0] != 0) SmartMode = parameters[0];
                    if (parameters[1] != 0) SmartThreshold = parameters[1];
                    if (parameters[2] != 0) SmartThirdByte = parameters[2];
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected HID++ request: feature {feature:X4}, function {function}.");
            }
            Assert.True(_incoming.Writer.TryWrite(reply));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private static ushort U16(byte[] bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
        private ControlState GetControl(ushort id) => id == ControlId
            ? new(ReportingFlags, MappedTo) : AdditionalControls[id];
        private static void PutU16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)(value >> 8);
            bytes[offset + 1] = (byte)value;
        }

        public sealed record Request(ushort Feature, byte Function, byte[] Parameters);
        public sealed record ControlState(ushort Flags, ushort MappedTo);
    }
}
