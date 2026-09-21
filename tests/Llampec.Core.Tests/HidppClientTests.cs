using System.Threading.Channels;
using Llampec.Devices.Logitech;
using Xunit;

namespace Llampec.Tests;

public class HidppClientTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Notification_between_request_and_reply_does_not_consume_the_reply()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        var notification = new TaskCompletionSource<HidppNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += value => notification.TrySetResult(value);

        Task<byte[]> pending = client.RequestAsync(0x07, 2, new byte[] { 0xAB });
        byte[] write = await transport.NextWriteAsync();
        Assert.Equal(new byte[] { 0xFF, 0x07, 0x21, 0xAB, 0, 0 }, write[1..7]);

        transport.Receive(Report(0x10, 0xFF, 0x09, 0x30, 0xC3, 0x00, 0x56));
        transport.Receive(Reply(write, 0x12, 0x34, 0x56));

        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, await pending.WaitAsync(TestDeadline));
        HidppNotification observed = await notification.Task.WaitAsync(TestDeadline);
        Assert.Equal((byte)0xFF, observed.DeviceIndex);
        Assert.Equal((byte)0x09, observed.FeatureIndex);
        Assert.Equal((byte)3, observed.Function);
        Assert.Equal(new byte[] { 0xC3, 0, 0x56 }, observed.Parameters);
    }

    [Fact]
    public async Task Unrelated_and_malformed_reports_cannot_complete_a_request()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport, deviceIndex: 2);
        var fence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += _ => fence.TrySetResult();

        Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] write = await transport.NextWriteAsync();
        transport.Receive(Report(0x10, 3, 7, write[3], 1, 2, 3)); // Another receiver slot.
        transport.Receive(Report(0x10, 2, 7, 0x22, 1, 2, 3)); // Another software ID.
        transport.Receive(Report(0x10, 2, 8, write[3], 1, 2, 3)); // Another feature.
        transport.Receive(Report(0x10, 2, 7, 0x31, 1, 2, 3)); // Another function.
        transport.Receive([0x10, 2, 7, write[3]]); // Truncated short report.
        transport.Receive([0x11, 2, 7, write[3], 1, 2, 3]); // Truncated long report.
        transport.Receive([0x12, 2, 7, write[3], 1, 2, 3]); // Unknown report ID.
        transport.Receive(Report(0x10, 2, 9, 0)); // A notification proves earlier input was processed.

        await fence.Task.WaitAsync(TestDeadline);
        Assert.False(pending.IsCompleted);
        transport.Receive(Reply(write, 4, 5, 6));
        Assert.Equal(new byte[] { 4, 5, 6 }, await pending.WaitAsync(TestDeadline));
    }

    [Fact]
    public async Task Concurrent_requests_are_serialized_and_receive_distinct_software_ids()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        Task<byte[]> first = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] firstWrite = await transport.NextWriteAsync();
        Task<byte[]> second = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        Assert.False(transport.TryReadWrite(out _));

        transport.Receive(Reply(firstWrite, 1));
        Assert.Equal((byte)1, (await first.WaitAsync(TestDeadline))[0]);
        byte[] secondWrite = await transport.NextWriteAsync();
        Assert.NotEqual(firstWrite[3], secondWrite[3]);
        transport.Receive(Reply(secondWrite, 2));
        Assert.Equal((byte)2, (await second.WaitAsync(TestDeadline))[0]);
    }

    [Fact]
    public async Task Long_requests_preserve_payload_and_short_replies_ignore_transport_padding()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        byte[] parameters = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        Task<byte[]> pending = client.RequestAsync(7, 2, parameters);
        byte[] write = await transport.NextWriteAsync();
        Assert.Equal((byte)0x11, write[0]);
        Assert.Equal(parameters, write[4..20]);
        byte[] padded = new byte[20];
        Reply(write, 1, 2, 3).CopyTo(padded, 0);
        padded[19] = 0xEE;
        transport.Receive(padded);

        Assert.Equal(new byte[] { 1, 2, 3 }, await pending.WaitAsync(TestDeadline));
    }

    [Fact]
    public async Task Long_replies_return_all_parameter_bytes()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] write = await transport.NextWriteAsync();
        byte[] parameters = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        transport.Receive(Report(0x11, write[1], write[2], write[3], parameters));

        Assert.Equal(parameters, await pending.WaitAsync(TestDeadline));
    }

    [Fact]
    public async Task Failing_notification_subscriber_does_not_stop_other_subscribers_or_reply_routing()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += _ => throw new InvalidOperationException("Consumer failure.");
        client.NotificationReceived += _ => notification.TrySetResult();
        Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] write = await transport.NextWriteAsync();
        transport.Receive(Report(0x10, write[1], 9, 0));
        transport.Receive(Reply(write, 0x42));

        Assert.Equal((byte)0x42, (await pending.WaitAsync(TestDeadline))[0]);
        await notification.Task.WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task Software_id_wraps_without_using_the_notification_id()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        for (int request = 0; request < 16; request++)
        {
            Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
            byte[] write = await transport.NextWriteAsync();
            Assert.Equal(request % 15 + 1, write[3] & 0x0F);
            transport.Receive(Reply(write, (byte)request));
            Assert.Equal((byte)request, (await pending.WaitAsync(TestDeadline))[0]);
        }
    }

    [Theory]
    [InlineData(0xFF)]
    [InlineData(0x8F)]
    public async Task Protocol_error_replies_fail_the_matching_request_and_release_the_next(int errorReportFeature)
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] write = await transport.NextWriteAsync();
        transport.Receive(Report(0x10, write[1], (byte)errorReportFeature, write[2], write[3], 0x09, 0));

        HidppException error = await Assert.ThrowsAsync<HidppException>(() => pending.WaitAsync(TestDeadline));
        Assert.Equal((byte)0x09, error.ErrorCode);

        Task<byte[]> next = client.RequestAsync(8, 0, ReadOnlyMemory<byte>.Empty);
        byte[] nextWrite = await transport.NextWriteAsync();
        transport.Receive(Reply(nextWrite, 0x42));
        Assert.Equal((byte)0x42, (await next.WaitAsync(TestDeadline))[0]);
    }

    [Fact]
    public async Task Timeout_releases_request_and_a_late_reply_does_not_match_its_successor()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport, requestTimeout: TimeSpan.FromMilliseconds(200));
        Task<byte[]> timedOut = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] oldWrite = await transport.NextWriteAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut.WaitAsync(TestDeadline));

        Task<byte[]> next = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] nextWrite = await transport.NextWriteAsync();
        transport.Receive(Reply(oldWrite, 0x11));
        transport.Receive(Reply(nextWrite, 0x22));
        Assert.Equal((byte)0x22, (await next.WaitAsync(TestDeadline))[0]);
    }

    [Fact]
    public async Task Caller_cancellation_releases_request_without_cancelling_the_reader()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        using var cancellation = new CancellationTokenSource();
        Task<byte[]> cancelled = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty, cancellation.Token);
        byte[] oldWrite = await transport.NextWriteAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TestDeadline));

        Task<byte[]> next = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] nextWrite = await transport.NextWriteAsync();
        transport.Receive(Reply(oldWrite, 0x11));
        transport.Receive(Reply(nextWrite, 0x22));
        Assert.Equal((byte)0x22, (await next.WaitAsync(TestDeadline))[0]);
    }

    [Fact]
    public async Task Ambiguous_software_id_is_not_reused_after_the_counter_wraps()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        using var cancellation = new CancellationTokenSource();
        Task<byte[]> interrupted = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty, cancellation.Token);
        byte[] oldWrite = await transport.NextWriteAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.WaitAsync(TestDeadline));

        // Enough successful exchanges to wrap the 4-bit counter. A delayed old
        // response must never match one of these requests, despite identical feature/function.
        for (int request = 0; request < 30; request++)
        {
            Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
            byte[] write = await transport.NextWriteAsync();
            Assert.NotEqual(oldWrite[3], write[3]);
            transport.Receive(Reply(oldWrite, 0xAA));
            transport.Receive(Reply(write, 0xBB));
            Assert.Equal((byte)0xBB, (await pending.WaitAsync(TestDeadline))[0]);
        }
    }

    [Fact]
    public async Task Exhausted_ambiguous_ids_require_reopening_without_sending_another_request()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        for (int request = 0; request < 15; request++)
        {
            using var cancellation = new CancellationTokenSource();
            Task<byte[]> interrupted = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty, cancellation.Token);
            await transport.NextWriteAsync();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.WaitAsync(TestDeadline));
        }

        await Assert.ThrowsAsync<IOException>(() => client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty));
        Assert.False(transport.TryReadWrite(out _));
    }

    [Fact]
    public async Task Intentional_disposal_does_not_raise_connection_lost_when_transport_ends_with_io_error()
    {
        var transport = new FakeTransport { ReadErrorOnCancellation = true };
        var client = new HidppClient(transport);
        int lost = 0;
        client.ConnectionLost += _ => Interlocked.Increment(ref lost);
        // A completed request proves the reader is active before it is cancelled.
        Task<byte[]> request = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        transport.Receive(Reply(await transport.NextWriteAsync(), 0x42));
        await request.WaitAsync(TestDeadline);

        await client.DisposeAsync().AsTask().WaitAsync(TestDeadline);

        Assert.Equal(0, lost);
        Assert.Null(client.ConnectionError);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_does_not_write_it_or_cancel_the_active_request()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        Task<byte[]> active = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        byte[] write = await transport.NextWriteAsync();
        using var cancellation = new CancellationTokenSource();
        Task<byte[]> queued = client.RequestAsync(8, 0, ReadOnlyMemory<byte>.Empty, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TestDeadline));
        Assert.False(transport.TryReadWrite(out _));
        Assert.False(active.IsCompleted);

        transport.Receive(Reply(write, 0x42));
        Assert.Equal((byte)0x42, (await active.WaitAsync(TestDeadline))[0]);
        Assert.False(transport.TryReadWrite(out _));
    }

    [Fact]
    public async Task Read_failure_releases_pending_request_and_rejects_future_requests()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        await transport.NextWriteAsync();
        transport.FailRead(new IOException("Receiver disconnected."));

        await Assert.ThrowsAnyAsync<IOException>(() => pending.WaitAsync(TestDeadline));
        await Assert.ThrowsAnyAsync<IOException>(() => client.RequestAsync(8, 0, ReadOnlyMemory<byte>.Empty).WaitAsync(TestDeadline));
        Assert.False(transport.TryReadWrite(out _));
    }

    [Fact]
    public async Task End_of_stream_releases_pending_request()
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        await transport.NextWriteAsync();
        transport.Receive([]);

        await Assert.ThrowsAnyAsync<IOException>(() => pending.WaitAsync(TestDeadline));
    }

    [Fact]
    public async Task Disposal_releases_pending_requests_and_disposes_the_transport()
    {
        var transport = new FakeTransport();
        var client = new HidppClient(transport);
        Task<byte[]> pending = client.RequestAsync(7, 2, ReadOnlyMemory<byte>.Empty);
        await transport.NextWriteAsync();
        Task<byte[]> queued = client.RequestAsync(8, 0, ReadOnlyMemory<byte>.Empty);

        await client.DisposeAsync().AsTask().WaitAsync(TestDeadline);
        Assert.True(transport.IsDisposed);
        Exception? pendingError = await Record.ExceptionAsync(() => pending.WaitAsync(TestDeadline));
        Exception? queuedError = await Record.ExceptionAsync(() => queued.WaitAsync(TestDeadline));
        Assert.True(pendingError is ObjectDisposedException or OperationCanceledException, pendingError?.ToString());
        Assert.True(queuedError is ObjectDisposedException or OperationCanceledException, queuedError?.ToString());
        Assert.False(transport.TryReadWrite(out _));
        await client.DisposeAsync().AsTask().WaitAsync(TestDeadline);
    }

    [Theory]
    [InlineData(0x04, 0x04)]
    [InlineData(0x00, null)]
    public async Task Feature_lookup_encodes_big_endian_feature_id_and_reports_absence(int index, int? expected)
    {
        var transport = new FakeTransport();
        await using var client = new HidppClient(transport);
        Task<byte?> pending = client.FindFeatureAsync(0x2201);
        byte[] write = await transport.NextWriteAsync();
        Assert.Equal(new byte[] { 0xFF, 0, 1, 0x22, 0x01, 0 }, write[1..7]);
        transport.Receive(Reply(write, (byte)index));

        Assert.Equal(expected is { } value ? (byte?)value : null, await pending.WaitAsync(TestDeadline));
    }

    private static byte[] Reply(byte[] request, params byte[] parameters) =>
        Report(0x10, request[1], request[2], request[3], parameters);

    private static byte[] Report(byte id, byte device, byte feature, byte functionAndSoftwareId, params byte[] parameters)
    {
        byte[] report = new byte[id == 0x11 ? 20 : 7];
        report[0] = id;
        report[1] = device;
        report[2] = feature;
        report[3] = functionAndSoftwareId;
        parameters.CopyTo(report, 4);
        return report;
    }

    private sealed class FakeTransport : IHidTransport
    {
        private readonly Channel<Incoming> _incoming = Channel.CreateUnbounded<Incoming>();
        private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>();
        public int InputReportLength => 20;
        public int OutputReportLength => 20;
        public bool IsDisposed { get; private set; }
        public bool ReadErrorOnCancellation { get; init; }

        public void Receive(byte[] report) => Assert.True(_incoming.Writer.TryWrite(new(report, null)));
        public void FailRead(Exception error) => Assert.True(_incoming.Writer.TryWrite(new(null, error)));
        public Task<byte[]> NextWriteAsync() => _writes.Reader.ReadAsync().AsTask().WaitAsync(TestDeadline);
        public bool TryReadWrite(out byte[]? report) => _writes.Reader.TryRead(out report);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Incoming incoming;
            try { incoming = await _incoming.Reader.ReadAsync(cancellationToken); }
            catch (OperationCanceledException) when (ReadErrorOnCancellation)
            { throw new IOException("The I/O operation was aborted while closing the device."); }
            if (incoming.Error is { } error) throw error;
            byte[] report = incoming.Report!;
            report.CopyTo(buffer);
            return report.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(IsDisposed);
            Assert.True(_writes.Writer.TryWrite(report.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private sealed record Incoming(byte[]? Report, Exception? Error);
    }
}
