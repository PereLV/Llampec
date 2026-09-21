namespace Llampec.Devices.Logitech;

/// <summary>A single vendor HID collection. Reports include their report-ID byte.</summary>
public interface IHidTransport : IAsyncDisposable
{
    int InputReportLength { get; }
    int OutputReportLength { get; }
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);
}

public sealed record HidppNotification(byte DeviceIndex, byte FeatureIndex, byte Function, byte[] Parameters);
