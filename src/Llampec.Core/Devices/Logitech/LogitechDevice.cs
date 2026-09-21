// Selected HID++ feature behavior adapted with reference to Mouser core/hid_gesture.py
// (Tom Badash, MIT), commit e780641d3e709f914d6273985da9ac2ab85a7322:
// gesture CID preference, control reporting, DPI and SmartShift function selection.
// Protocol formats: Logitech x1b04, x2201, x2110 and x2121 documents at
// https://lekensteyn.nl/files/logitech/ ; 0x2111 and 0x2150 wire descriptions at
// https://openlogi.org/hidpp/features/ . See the repository third-party notices.

using System.Collections.ObjectModel;
using System.Text;

namespace Llampec.Devices.Logitech;

/// <summary>
/// Narrow, opt-in HID++ feature session. Discovery never writes device settings.
/// Call RestoreAsync before disposing the client/transport. Restoration is best-effort
/// after disconnect; some firmware settings can survive process exit or power cycling.
/// No polling, wheel diversion, persistent button diversion, or raw XY is enabled.
/// </summary>
public sealed class LogitechDevice : IAsyncDisposable
{
    private readonly HidppClient _client;
    private readonly Dictionary<ushort, byte> _features = [];
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly object _buttonLock = new();
    private ushort? _originalDpi;
    private byte? _originalSmartMode;
    private byte? _originalSmartThreshold;
    private bool? _originalVerticalInvert;
    private bool? _originalHorizontalInvert;
    private readonly Dictionary<ushort, OwnedControl> _ownedControls = [];
    private bool _disposed;

    private LogitechDevice(HidppClient client)
    {
        _client = client;
        Features = new ReadOnlyDictionary<ushort, byte>(_features);
    }

    public string Name { get; private set; } = "Logitech HID++ device";
    /// <summary>HID++ 0x0003 per-unit identifier, independent of receiver slot/transport; absent on older devices.</summary>
    public string? UnitId { get; private set; }
    public byte DeviceIndex => _client.DeviceIndex;
    public IReadOnlyDictionary<ushort, byte> Features { get; }
    public IReadOnlyList<LogitechControl> Controls { get; private set; } = [];
    public LogitechDpiState? Dpi { get; private set; }
    public LogitechSmartShiftState? SmartShift { get; private set; }
    public byte? VerticalWheelMode { get; private set; }
    public bool VerticalWheelCanInvert { get; private set; }
    public LogitechThumbWheelState? HorizontalWheelState { get; private set; }

    public static async Task<LogitechDevice> CreateAsync(HidppClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var device = new LogitechDevice(client);
        foreach (ushort id in new ushort[] { 0x0003, 0x0005, 0x1B04, 0x2201, 0x2110, 0x2111, 0x2121, 0x2150 })
        {
            var index = await client.FindFeatureAsync(id, ct).ConfigureAwait(false);
            if (index is > 0) device._features.Add(id, index.Value);
        }
        if (device._features.ContainsKey(0x0003)) await device.ReadUnitIdAsync(ct).ConfigureAwait(false);
        if (device._features.ContainsKey(0x0005)) await device.ReadNameAsync(ct).ConfigureAwait(false);
        if (device._features.ContainsKey(0x1B04)) await device.ReadControlsAsync(ct).ConfigureAwait(false);
        if (device._features.ContainsKey(0x2201)) await device.DiscoverDpiAsync(ct).ConfigureAwait(false);
        if (device._features.ContainsKey(0x2111) || device._features.ContainsKey(0x2110))
            device.SmartShift = await device.ReadSmartShiftAsync(ct).ConfigureAwait(false);
        if (device._features.ContainsKey(0x2121))
        {
            var capabilities = await device.RequestAsync(0x2121, 0, [], ct).ConfigureAwait(false);
            RequireLength(capabilities, 2);
            device.VerticalWheelCanInvert = (capabilities[1] & 0x08) != 0;
            device.VerticalWheelMode = await device.ReadVerticalModeAsync(ct).ConfigureAwait(false);
        }
        if (device._features.ContainsKey(0x2150))
            device.HorizontalWheelState = await device.ReadHorizontalAsync(ct).ConfigureAwait(false);
        client.NotificationReceived += device.OnNotification;
        return device;
    }

    private async Task ReadUnitIdAsync(CancellationToken ct)
    {
        // Logitech x0003 getDeviceInfo v1: byte 0 entity count; bytes 1..4 unit ID.
        // Version 0 returns only entity count, so short/zero-padded replies have no ID.
        // https://lekensteyn.nl/files/logitech/x0003_deviceinfo.html
        try
        {
            var response = await RequestAsync(0x0003, 0, [], ct).ConfigureAwait(false);
            if (response.Length >= 5 && response.AsSpan(1, 4).IndexOfAnyExcept((byte)0) >= 0)
                UnitId = Convert.ToHexString(response.AsSpan(1, 4));
        }
        catch (HidppException error) when (error.ErrorCode is 0x07 or 0x09)
        {
            // Some firmware advertises this feature but omits getDeviceInfo support.
            // Lack of identity must not hide its independent read-only capabilities.
        }
    }

    private async Task ReadNameAsync(CancellationToken ct)
    {
        var response = await RequestAsync(0x0005, 0, [], ct).ConfigureAwait(false);
        RequireLength(response, 1);
        var bytes = new byte[response[0]];
        for (int offset = 0; offset < bytes.Length;)
        {
            response = await RequestAsync(0x0005, 1, [(byte)offset], ct).ConfigureAwait(false);
            RequireLength(response, 1);
            int length = Math.Min(response.Length, bytes.Length - offset);
            response.AsSpan(0, length).CopyTo(bytes.AsSpan(offset));
            offset += length;
        }
        string name = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
        if (name.Length != 0) Name = name;
    }

    private async Task ReadControlsAsync(CancellationToken ct)
    {
        var count = await RequestAsync(0x1B04, 0, [], ct).ConfigureAwait(false);
        RequireLength(count, 1);
        var controls = new List<LogitechControl>();
        for (int index = 0; index < count[0]; index++)
        {
            var p = await RequestAsync(0x1B04, 1, [(byte)index], ct).ConfigureAwait(false);
            RequireLength(p, 8);
            ushort cid = U16(p, 0);
            var reporting = await ReadReportingAsync(cid, ct).ConfigureAwait(false);
            controls.Add(new((byte)index, cid, U16(p, 2),
                (ushort)(p[4] | (p.Length >= 9 ? p[8] << 8 : 0)), p[5], p[6], p[7],
                reporting.Flags, reporting.MappedTo));
        }
        Controls = controls.AsReadOnly();
    }

    private async Task DiscoverDpiAsync(CancellationToken ct)
    {
        var count = await RequestAsync(0x2201, 0, [], ct).ConfigureAwait(false);
        RequireLength(count, 1);
        if (count[0] == 0) return;
        var p = await RequestAsync(0x2201, 1, [0], ct).ConfigureAwait(false);
        RequireLength(p, 3);
        Require(p[0] == 0, "DPI list response belongs to another sensor.");
        var values = new List<ushort>();
        for (int i = 1; i + 1 < p.Length; i += 2)
        {
            ushort value = U16(p, i);
            if (value == 0) break;
            values.Add(value);
        }
        LogitechDpiRange? range = null;
        if (values.Count == 3 && values[1] > 0xE000)
        {
            range = new(values[0], values[2], (ushort)(values[1] & 0x1FFF));
            Require(range.Minimum > 0 && range.Maximum < 0xE000 && range.Minimum <= range.Maximum,
                "Invalid DPI range.");
            values.Clear();
        }
        else Require(values.Count > 0 && values.All(v => v < 0xE000), "Unsupported DPI list encoding.");
        var current = await ReadDpiAsync(ct).ConfigureAwait(false);
        Dpi = new(count[0], current.Current, current.Default, values.AsReadOnly(), range);
    }

    public Task SetDpiAsync(ushort dpi, CancellationToken ct = default) => MutateAsync(async () =>
    {
        if (Dpi is null) throw new NotSupportedException("Adjustable DPI sensor zero is unavailable.");
        if (!Dpi.Supports(dpi)) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI is not in the sensor's advertised values/range.");
        var before = await ReadDpiAsync(ct).ConfigureAwait(false);
        if (before.Current == dpi) return;
        _originalDpi ??= before.Current;
        await WriteDpiAsync(dpi, ct).ConfigureAwait(false);
    }, ct);

    /// <summary>Mode 1=free spin, 2=ratchet. Threshold 1..254, 255=always ratchet; 0 leaves it unchanged.</summary>
    public Task SetSmartShiftAsync(byte mode, byte threshold, CancellationToken ct = default) => MutateAsync(async () =>
    {
        if (mode is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(mode));
        var before = await ReadSmartShiftAsync(ct).ConfigureAwait(false);
        byte targetThreshold = threshold == 0 ? before.Threshold : threshold;
        if (before.Mode == mode && before.Threshold == targetThreshold) return;
        if (before.Mode != mode) _originalSmartMode ??= before.Mode;
        if (before.Threshold != targetThreshold) _originalSmartThreshold ??= before.Threshold;
        await WriteSmartShiftAsync(mode, targetThreshold, ct).ConfigureAwait(false);
    }, ct);

    /// <summary>Null leaves that axis unchanged. Changes only inversion; preserves HID routing and resolution.</summary>
    public Task SetWheelInversionAsync(bool? vertical, bool? horizontal, CancellationToken ct = default) => MutateAsync(async () =>
    {
        // Read/validate both axes before performing either write.
        byte? v = vertical.HasValue ? await ReadVerticalModeAsync(ct).ConfigureAwait(false) : null;
        var h = horizontal.HasValue ? await ReadHorizontalAsync(ct).ConfigureAwait(false) : null;
        if (vertical.HasValue && !VerticalWheelCanInvert)
            throw new NotSupportedException("The vertical wheel does not advertise native inversion.");
        if (v.HasValue && (v.Value & 1) != 0)
            throw new InvalidOperationException("Vertical wheel is already routed to another HID++ consumer.");
        if (h is not null && h.ReportingMode != 0)
            throw new InvalidOperationException("Horizontal wheel is already routed to another HID++ consumer.");
        if (vertical.HasValue && ((v!.Value & 4) != 0) != vertical.Value)
        {
            _originalVerticalInvert ??= (v.Value & 4) != 0;
            await WriteVerticalInvertAsync(vertical.Value, ct).ConfigureAwait(false);
        }
        if (horizontal.HasValue && h!.Inverted != horizontal.Value)
        {
            _originalHorizontalInvert ??= h.Inverted;
            await WriteHorizontalInvertAsync(horizontal.Value, ct).ConfigureAwait(false);
        }
    }, ct);

    /// <summary>Compatibility helper selecting a discovered thumb control. Never enables raw XY.</summary>
    public Task DivertThumbAsync(Action<bool> callback, ushort? cid = null, CancellationToken ct = default)
    {
        var control = cid.HasValue
            ? Controls.FirstOrDefault(c => c.Id == cid.Value)
            : new ushort[] { 0x00C3, 0x00D7 }.Select(id => Controls.FirstOrDefault(c => c.Id == id && c.Divertable)).FirstOrDefault(c => c is not null);
        if (control is null || !control.Divertable)
            return Task.FromException(new NotSupportedException("No requested divertable thumb control was discovered."));
        return DivertButtonAsync(control.Id, callback, ct);
    }

    /// <summary>
    /// Temporarily diverts one discovered button; callback true=press, false=release.
    /// Existing external diversion/raw XY is rejected without taking ownership.
    /// Several buttons can be owned simultaneously; persistent flags and mappings are untouched.
    /// </summary>
    public Task DivertButtonAsync(ushort cid, Action<bool> callback, CancellationToken ct = default) => MutateAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_buttonLock)
            if (_ownedControls.ContainsKey(cid)) throw new InvalidOperationException("This control is already owned by this session.");
        RequireDivertableControl(cid);
        var before = await ReadReportingAsync(cid, ct).ConfigureAwait(false);
        if ((before.Flags & 0x55) != 0)
            throw new InvalidOperationException("Control is already diverted or uses raw XY; close its existing controller first.");
        lock (_buttonLock)
            _ownedControls.Add(cid, new OwnedControl(false, callback));
        await WriteControlDivertAsync(cid, true, ct).ConfigureAwait(false);
    }, ct);

    /// <summary>
    /// Reads actual pre-change values for a write-ahead recovery record. It never writes
    /// or changes ownership. Requested controls already controlled elsewhere are rejected.
    /// </summary>
    public async Task<LogitechRestoreState> CaptureRestoreStateAsync(IEnumerable<ushort> controlIds,
        bool dpi, bool smart, bool vertical, bool horizontal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(controlIds);
        ushort[] ids = controlIds.Distinct().ToArray();
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var controls = new List<LogitechControlRestoreState>();
            foreach (ushort cid in ids)
            {
                RequireDivertableControl(cid);
                var reporting = await ReadReportingAsync(cid, ct).ConfigureAwait(false);
                if ((reporting.Flags & 0x55) != 0)
                    throw new InvalidOperationException($"Control 0x{cid:X4} is already diverted or uses raw XY.");
                controls.Add(new(cid, (reporting.Flags & 1) != 0));
            }
            ushort? sensorDpi = dpi ? (await ReadDpiAsync(ct).ConfigureAwait(false)).Current : null;
            var smartState = smart ? await ReadSmartShiftAsync(ct).ConfigureAwait(false) : null;
            byte? verticalMode = vertical ? await ReadVerticalModeAsync(ct).ConfigureAwait(false) : null;
            var horizontalState = horizontal ? await ReadHorizontalAsync(ct).ConfigureAwait(false) : null;
            if (vertical && (!VerticalWheelCanInvert || (verticalMode!.Value & 1) != 0))
                throw new NotSupportedException("Vertical wheel native inversion is unavailable or its events are diverted.");
            if (horizontalState is not null && horizontalState.ReportingMode != 0)
                throw new InvalidOperationException("Horizontal wheel events are already diverted.");
            return new(sensorDpi, smartState?.Mode, smartState?.Threshold,
                verticalMode.HasValue ? (verticalMode.Value & 4) != 0 : null,
                horizontalState?.Inverted, controls.AsReadOnly());
        }
        finally { _mutationLock.Release(); }
    }

    /// <summary>
    /// Restores a durable snapshot after a reconnect. Required missing features are errors.
    /// The caller's durable baseline takes precedence over per-write snapshots, since
    /// physical controls may change settings between capture and the first write.
    /// Imports restoration obligations before writing, so failures retain targets for retry;
    /// successful restoration does not create an undo back to the interrupted settings.
    /// </summary>
    public async Task ApplyRestoreStateAsync(LogitechRestoreState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateRestoreState(state);
            _originalDpi = state.Dpi ?? _originalDpi;
            _originalSmartMode = state.SmartMode ?? _originalSmartMode;
            _originalSmartThreshold = state.SmartThreshold ?? _originalSmartThreshold;
            _originalVerticalInvert = state.VerticalInvert ?? _originalVerticalInvert;
            _originalHorizontalInvert = state.HorizontalInvert ?? _originalHorizontalInvert;
            lock (_buttonLock)
                foreach (var control in state.Controls)
                {
                    if (_ownedControls.TryGetValue(control.Id, out var owned)) owned.OriginalDiverted = control.Diverted;
                    else _ownedControls.Add(control.Id, new OwnedControl(control.Diverted, null));
                }
            await RestoreCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _mutationLock.Release(); }
    }

    /// <summary>Restores only settings changed by this instance, continuing after individual failures.</summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try { await RestoreCoreAsync(ct).ConfigureAwait(false); }
        finally { _mutationLock.Release(); }
    }

    private async Task MutateAsync(Func<Task> operation, CancellationToken ct)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { await operation().ConfigureAwait(false); }
            catch (Exception original)
            {
                // A cancelled/timed-out write may already have reached the device. Keep
                // snapshots before writing and roll the session back independently of ct.
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await RestoreCoreAsync(rollback.Token).ConfigureAwait(false); }
                catch (Exception restore) { throw new AggregateException("Operation failed and restoration is incomplete.", original, restore); }
                throw;
            }
        }
        finally { _mutationLock.Release(); }
    }

    private async Task RestoreCoreAsync(CancellationToken ct)
    {
        var errors = new List<Exception>();
        async Task Attempt(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        KeyValuePair<ushort, OwnedControl>[] controls;
        lock (_buttonLock) controls = _ownedControls.ToArray();
        foreach (var entry in controls)
        {
            await Attempt(async () =>
            {
                await WriteControlDivertAsync(entry.Key, entry.Value.OriginalDiverted, ct).ConfigureAwait(false);
                ReleaseButtonCallbacks(entry.Key);
                lock (_buttonLock) _ownedControls.Remove(entry.Key);
            }).ConfigureAwait(false);
        }
        await Attempt(async () =>
        {
            if (_originalHorizontalInvert is bool original)
            {
                await WriteHorizontalInvertAsync(original, ct).ConfigureAwait(false);
                _originalHorizontalInvert = null;
            }
        }).ConfigureAwait(false);
        await Attempt(async () =>
        {
            if (_originalVerticalInvert is bool original)
            {
                await WriteVerticalInvertAsync(original, ct).ConfigureAwait(false);
                _originalVerticalInvert = null;
            }
        }).ConfigureAwait(false);
        await Attempt(async () =>
        {
            if (_originalSmartMode.HasValue || _originalSmartThreshold.HasValue)
            {
                var current = await ReadSmartShiftAsync(ct).ConfigureAwait(false);
                await WriteSmartShiftAsync(_originalSmartMode ?? current.Mode, _originalSmartThreshold ?? current.Threshold, ct).ConfigureAwait(false);
                _originalSmartMode = _originalSmartThreshold = null;
            }
        }).ConfigureAwait(false);
        await Attempt(async () =>
        {
            if (_originalDpi is ushort original)
            {
                await WriteDpiAsync(original, ct).ConfigureAwait(false);
                _originalDpi = null;
            }
        }).ConfigureAwait(false);
        ReleaseButtonCallbacks();
        if (errors.Count != 0) throw new AggregateException("Some device settings could not be restored; snapshots retained for retry.", errors);
    }

    private async Task<(ushort Current, ushort Default)> ReadDpiAsync(CancellationToken ct)
    {
        var p = await RequestAsync(0x2201, 2, [0], ct).ConfigureAwait(false);
        RequireLength(p, 5);
        Require(p[0] == 0 && U16(p, 1) > 0, "Invalid sensor-zero DPI response.");
        return (U16(p, 1), U16(p, 3));
    }

    private async Task WriteDpiAsync(ushort dpi, CancellationToken ct)
    {
        await RequestAsync(0x2201, 3, [0, (byte)(dpi >> 8), (byte)dpi], ct).ConfigureAwait(false);
        var read = await ReadDpiAsync(ct).ConfigureAwait(false);
        Require(read.Current == dpi, "DPI readback differs from requested DPI.");
        if (Dpi is not null) Dpi = Dpi with { Current = read.Current, Default = read.Default };
    }

    private async Task<LogitechSmartShiftState> ReadSmartShiftAsync(CancellationToken ct)
    {
        ushort feature = _features.ContainsKey(0x2111) ? (ushort)0x2111 : (ushort)0x2110;
        var p = await RequestAsync(feature, (byte)(feature == 0x2111 ? 1 : 0), [], ct).ConfigureAwait(false);
        RequireLength(p, 3);
        Require(p[0] is 1 or 2 && p[1] != 0, "Invalid SmartShift status.");
        return new(feature, p[0], p[1], p[2]);
    }

    private async Task WriteSmartShiftAsync(byte mode, byte threshold, CancellationToken ct)
    {
        var before = await ReadSmartShiftAsync(ct).ConfigureAwait(false);
        await RequestAsync(before.FeatureId, (byte)(before.FeatureId == 0x2111 ? 2 : 1), [mode, threshold, 0], ct).ConfigureAwait(false);
        var read = await ReadSmartShiftAsync(ct).ConfigureAwait(false);
        Require(read.Mode == mode && read.Threshold == threshold && read.ThirdByte == before.ThirdByte,
            "SmartShift readback differs or an unrelated parameter changed.");
        SmartShift = read;
    }

    private async Task<byte> ReadVerticalModeAsync(CancellationToken ct)
    {
        var p = await RequestAsync(0x2121, 1, [], ct).ConfigureAwait(false);
        RequireLength(p, 1);
        return p[0];
    }

    private async Task WriteVerticalInvertAsync(bool inverted, CancellationToken ct)
    {
        byte before = await ReadVerticalModeAsync(ct).ConfigureAwait(false);
        byte target = (byte)((before & ~4) | (inverted ? 4 : 0));
        if (before != target) await RequestAsync(0x2121, 2, [target], ct).ConfigureAwait(false);
        byte read = await ReadVerticalModeAsync(ct).ConfigureAwait(false);
        Require(read == target, "Vertical wheel readback differs (routing/resolution must be preserved).");
        VerticalWheelMode = read;
    }

    private async Task<LogitechThumbWheelState> ReadHorizontalAsync(CancellationToken ct)
    {
        var p = await RequestAsync(0x2150, 1, [], ct).ConfigureAwait(false);
        RequireLength(p, 2);
        Require(p[0] <= 1, "Unknown horizontal wheel reporting mode.");
        return new(p[0], (p[1] & 1) != 0);
    }

    private async Task WriteHorizontalInvertAsync(bool inverted, CancellationToken ct)
    {
        var before = await ReadHorizontalAsync(ct).ConfigureAwait(false);
        if (before.Inverted != inverted)
            await RequestAsync(0x2150, 2, [before.ReportingMode, (byte)(inverted ? 1 : 0), 0], ct).ConfigureAwait(false);
        var read = await ReadHorizontalAsync(ct).ConfigureAwait(false);
        Require(read.ReportingMode == before.ReportingMode && read.Inverted == inverted, "Horizontal wheel readback differs.");
        HorizontalWheelState = read;
    }

    private async Task<(ushort Flags, ushort MappedTo)> ReadReportingAsync(ushort cid, CancellationToken ct)
    {
        var p = await RequestAsync(0x1B04, 2, [(byte)(cid >> 8), (byte)cid], ct).ConfigureAwait(false);
        RequireLength(p, 5);
        Require(U16(p, 0) == cid, "Control reporting response belongs to another control.");
        return ((ushort)(p[2] | (p.Length >= 6 ? p[5] << 8 : 0)), U16(p, 3));
    }

    private async Task WriteControlDivertAsync(ushort cid, bool diverted, CancellationToken ct)
    {
        var before = await ReadReportingAsync(cid, ct).ConfigureAwait(false);
        // Only dvalid is set. RawXY/persistent flags are untouched; remap=0 means leave unchanged.
        await RequestAsync(0x1B04, 3, [(byte)(cid >> 8), (byte)cid, (byte)(diverted ? 3 : 2), 0, 0], ct).ConfigureAwait(false);
        var read = await ReadReportingAsync(cid, ct).ConfigureAwait(false);
        Require((read.Flags & 1) == (diverted ? 1 : 0)
            && (read.Flags & ~1) == (before.Flags & ~1) && read.MappedTo == before.MappedTo,
            "Control diversion readback differs or unrelated reporting/mapping changed.");
        Controls = Array.AsReadOnly(Controls.Select(control => control.Id == cid
            ? control with { ReportingFlags = read.Flags, MappedTo = read.MappedTo }
            : control).ToArray());
    }

    private void OnNotification(HidppNotification notification)
    {
        if (notification.DeviceIndex != DeviceIndex || notification.Function != 0
            || !_features.TryGetValue(0x1B04, out byte feature) || notification.FeatureIndex != feature) return;
        var pressedControls = new HashSet<ushort>();
        for (int i = 0; i + 1 < Math.Min(8, notification.Parameters.Length); i += 2)
        {
            ushort pressed = U16(notification.Parameters, i);
            if (pressed == 0) break;
            pressedControls.Add(pressed);
        }
        var callbacks = new List<(Action<bool> Callback, bool Down)>();
        lock (_buttonLock)
        {
            foreach (var entry in _ownedControls)
            {
                bool down = pressedControls.Contains(entry.Key);
                if (down == entry.Value.Down) continue;
                entry.Value.Down = down;
                if (entry.Value.Callback is { } callback) callbacks.Add((callback, down));
            }
        }
        // User callbacks must remain short; an exception must never kill the HID reader.
        foreach (var (callback, down) in callbacks)
            try { callback(down); } catch { }
    }

    private void ReleaseButtonCallbacks(ushort? cid = null)
    {
        var callbacks = new List<Action<bool>>();
        lock (_buttonLock)
        {
            foreach (var entry in _ownedControls)
            {
                if (!entry.Value.Down || (cid.HasValue && entry.Key != cid.Value)) continue;
                entry.Value.Down = false;
                if (entry.Value.Callback is { } callback) callbacks.Add(callback);
            }
        }
        foreach (var callback in callbacks)
            try { callback(false); } catch { }
    }

    private void RequireDivertableControl(ushort cid)
    {
        if (!Controls.Any(c => c.Id == cid && c.Divertable))
            throw new NotSupportedException($"Control 0x{cid:X4} was not discovered as divertable.");
    }

    private void ValidateRestoreState(LogitechRestoreState state)
    {
        ArgumentNullException.ThrowIfNull(state.Controls);
        if (state.Dpi is ushort dpi && (Dpi is null || !Dpi.Supports(dpi)))
            throw new NotSupportedException("Recovery DPI is unavailable on sensor zero.");
        if (state.SmartMode.HasValue || state.SmartThreshold.HasValue)
        {
            if (!_features.ContainsKey(0x2110) && !_features.ContainsKey(0x2111))
                throw new NotSupportedException("Recovery requires SmartShift support.");
            if (state.SmartMode.HasValue && state.SmartMode is not (1 or 2))
                throw new ArgumentException("Recovery wheel mode must be 1 or 2.", nameof(state));
            if (state.SmartThreshold == 0)
                throw new ArgumentException("Recovery SmartShift threshold cannot be zero.", nameof(state));
        }
        if (state.VerticalInvert.HasValue && (!_features.ContainsKey(0x2121) || !VerticalWheelCanInvert))
            throw new NotSupportedException("Recovery requires vertical wheel native inversion.");
        if (state.HorizontalInvert.HasValue && !_features.ContainsKey(0x2150))
            throw new NotSupportedException("Recovery requires horizontal wheel support.");
        var ids = new HashSet<ushort>();
        foreach (var control in state.Controls)
        {
            ArgumentNullException.ThrowIfNull(control);
            if (!ids.Add(control.Id)) throw new ArgumentException("Duplicate recovery control.", nameof(state));
            RequireDivertableControl(control.Id);
        }
    }

    private sealed class OwnedControl(bool originalDiverted, Action<bool>? callback)
    {
        public bool OriginalDiverted { get; set; } = originalDiverted;
        public Action<bool>? Callback { get; } = callback;
        public bool Down { get; set; }
    }

    private Task<byte[]> RequestAsync(ushort feature, byte function, byte[] parameters, CancellationToken ct)
    {
        if (!_features.TryGetValue(feature, out byte index))
            throw new NotSupportedException($"HID++ feature 0x{feature:X4} is unavailable.");
        return _client.RequestAsync(index, function, parameters, ct);
    }

    private static ushort U16(byte[] bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    private static void RequireLength(byte[] bytes, int length) => Require(bytes.Length >= length, "Truncated HID++ feature response.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    public async ValueTask DisposeAsync()
    {
        await _mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            // Exclude queued mutations before restoring so none can write between
            // the final restoration and disposal becoming visible.
            _disposed = true;
            try { await RestoreCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { _client.NotificationReceived -= OnNotification; }
        }
        finally
        {
            _mutationLock.Release();
        }
    }
}
