using Llampec.Settings;

namespace Llampec.Devices.Logitech;

public enum LogitechMouseConnectionState { Disabled, Connecting, Connected, Disconnected, Suspended, Error }

public sealed record LogitechMouseDevice(
    string Path, ushort ProductId, string? SerialNumber, byte DeviceIndex, string Name,
    IReadOnlyList<LogitechControl> Controls, LogitechDpiState? Dpi, LogitechSmartShiftState? SmartShift,
    bool VerticalWheelCanInvert, byte? VerticalWheelMode, LogitechThumbWheelState? HorizontalWheelState)
{
    public string? UnitId { get; init; }
}

public sealed record LogitechMouseStatus(LogitechMouseConnectionState State, LogitechMouseDevice? Device,
    string? Error, bool RecoveryPending)
{
    public bool Connected => State == LogitechMouseConnectionState.Connected;
}

internal sealed record LogitechMouseIdentity(string Path, ushort ProductId, byte DeviceIndex, string? SerialNumber, string? UnitId = null)
{
    public bool Matches(LogitechMouseDevice device) => ProductId == device.ProductId && DeviceIndex == device.DeviceIndex
        && string.Equals(Path, device.Path, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(SerialNumber) || string.Equals(SerialNumber, device.SerialNumber, StringComparison.Ordinal))
        && (string.IsNullOrEmpty(UnitId) || string.Equals(UnitId, device.UnitId, StringComparison.OrdinalIgnoreCase));
}

internal interface ILogitechMouseBackend
{
    Task<IReadOnlyList<LogitechMouseDevice>> ScanAsync(LogitechMouseDevice? active, CancellationToken ct);
    Task<ILogitechMouseSession> OpenAsync(LogitechMouseIdentity identity, CancellationToken ct);
}

internal interface ILogitechMouseSession : IAsyncDisposable
{
    LogitechMouseDevice Device { get; }
    Exception? ConnectionError { get; }
    event Action<Exception>? ConnectionLost;
    Task<LogitechRestoreState> CaptureAsync(LogitechMouseSettings settings, CancellationToken ct);
    Task ApplyAsync(LogitechMouseSettings settings, Action<ushort, bool> button, CancellationToken ct);
    Task RestoreAsync(LogitechRestoreState state, CancellationToken ct);
}

internal sealed class WindowsLogitechMouseBackend : ILogitechMouseBackend
{
    public async Task<IReadOnlyList<LogitechMouseDevice>> ScanAsync(LogitechMouseDevice? active, CancellationToken ct)
    {
        var result = new List<LogitechMouseDevice>();
        foreach (var info in WindowsHidTransport.EnumerateLogitech())
        {
            ct.ThrowIfCancellationRequested();
            if (active is not null && string.Equals(info.Path, active.Path, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(active);
                continue; // Never compete with this process's active HID reader.
            }
            foreach (byte slot in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var session = await OpenNativeAsync(info, slot, ct).ConfigureAwait(false);
                    var device = session.Device;
                    if (device.Dpi is not null || device.VerticalWheelMode.HasValue || device.HorizontalWheelState is not null
                        || device.Controls.Any(control => control.Divertable))
                        result.Add(device);
                    if (slot == 0xFF && result.Any(d => string.Equals(d.Path, info.Path, StringComparison.OrdinalIgnoreCase))) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    // Unpaired receiver slots and unrelated vendor collections are expected.
                }
            }
        }
        return result.AsReadOnly();
    }

    public async Task<ILogitechMouseSession> OpenAsync(LogitechMouseIdentity identity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = WindowsHidTransport.EnumerateLogitech().FirstOrDefault(d =>
            d.ProductId == identity.ProductId && string.Equals(d.Path, identity.Path, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(identity.SerialNumber) || string.Equals(d.SerialNumber, identity.SerialNumber, StringComparison.Ordinal)));
        if (info is null) throw new IOException("The selected Logitech mouse is disconnected.");
        return await OpenNativeAsync(info, identity.DeviceIndex, ct).ConfigureAwait(false);
    }

    private static async Task<ILogitechMouseSession> OpenNativeAsync(HidDeviceInfo info, byte slot, CancellationToken ct)
    {
        var client = new HidppClient(WindowsHidTransport.Open(info), slot);
        try
        {
            var device = await LogitechDevice.CreateAsync(client, ct).ConfigureAwait(false);
            return new NativeSession(info, client, device);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class NativeSession : ILogitechMouseSession
    {
        private readonly HidDeviceInfo _info;
        private readonly HidppClient _client;
        private readonly LogitechDevice _device;
        public NativeSession(HidDeviceInfo info, HidppClient client, LogitechDevice device)
        {
            _info = info; _client = client; _device = device;
        }
        public LogitechMouseDevice Device => new(_info.Path, _info.ProductId, _info.SerialNumber,
            _device.DeviceIndex, _device.Name, _device.Controls.ToArray(), _device.Dpi, _device.SmartShift,
            _device.VerticalWheelCanInvert, _device.VerticalWheelMode, _device.HorizontalWheelState) { UnitId = _device.UnitId };
        public Exception? ConnectionError => _client.ConnectionError;
        public event Action<Exception>? ConnectionLost
        {
            add => _client.ConnectionLost += value;
            remove => _client.ConnectionLost -= value;
        }
        public Task<LogitechRestoreState> CaptureAsync(LogitechMouseSettings settings, CancellationToken ct) =>
            _device.CaptureRestoreStateAsync(settings.ButtonShortcuts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key),
                settings.Dpi.HasValue, settings.WheelMode is not null, settings.InvertVertical.HasValue, settings.InvertHorizontal.HasValue, ct);
        public async Task ApplyAsync(LogitechMouseSettings settings, Action<ushort, bool> button, CancellationToken ct)
        {
            if (settings.Dpi is ushort dpi) await _device.SetDpiAsync(dpi, ct).ConfigureAwait(false);
            if (settings.WheelMode is { } mode)
                await _device.SetSmartShiftAsync((byte)(mode == "free" ? 1 : 2),
                    mode == "free" ? (byte)0 : mode == "ratchet" ? (byte)255 : settings.SmartShiftThreshold, ct).ConfigureAwait(false);
            if (settings.InvertVertical.HasValue || settings.InvertHorizontal.HasValue)
                await _device.SetWheelInversionAsync(settings.InvertVertical, settings.InvertHorizontal, ct).ConfigureAwait(false);
            foreach (var pair in settings.ButtonShortcuts.Where(p => !string.IsNullOrWhiteSpace(p.Value)))
            {
                ushort cid = pair.Key;
                await _device.DivertButtonAsync(cid, down => button(cid, down), ct).ConfigureAwait(false);
            }
        }
        public Task RestoreAsync(LogitechRestoreState state, CancellationToken ct) => _device.ApplyRestoreStateAsync(state, ct);
        public async ValueTask DisposeAsync()
        {
            // The service restores the durable snapshot explicitly. Closing input first
            // prevents an unbounded second hardware rollback if the connection was lost.
            try { await _client.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { await _device.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Diagnostics.Log.Warn($"Logitech session closed with pending recovery: {ex.Message}"); }
            }
        }
    }
}
