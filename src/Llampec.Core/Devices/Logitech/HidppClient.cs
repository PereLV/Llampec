namespace Llampec.Devices.Logitech;

/// <summary>HID++ 2.0 request/reply routing for one device or receiver slot.</summary>
/// <remarks>
/// Packet layout: https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst
/// One reader owns the collection. Requests are serialized; notifications can arrive
/// between a request and its reply. No keep-alive or periodic settings queries run.
/// </remarks>
public sealed class HidppClient : IAsyncDisposable
{
    private readonly IHidTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _sync = new();
    private readonly Task _reader;
    private PendingRequest? _pending;
    private Exception? _readerFailure;
    private byte _softwareId;
    private ushort _retiredSoftwareIds;
    private bool _disposed;
    private Task? _disposeTask;

    public byte DeviceIndex { get; }
    public Exception? ConnectionError { get { lock (_sync) return _readerFailure; } }
    public Task Completion => _reader;

    /// <summary>Runs on the reader task; handlers must queue work and return promptly.</summary>
    public event Action<HidppNotification>? NotificationReceived;
    /// <summary>Raised once if the input stream fails; handlers must queue work and return promptly.</summary>
    public event Action<Exception>? ConnectionLost;

    public HidppClient(IHidTransport transport, byte deviceIndex = 0xFF, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (transport.InputReportLength < 20 || transport.OutputReportLength < 20)
            throw new ArgumentException("HID++ requires a collection supporting 20-byte reports.", nameof(transport));
        _requestTimeout = requestTimeout ?? TimeSpan.FromMilliseconds(900);
        if (_requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _transport = transport;
        DeviceIndex = deviceIndex;
        _reader = Task.Run(ReadLoopAsync);
    }

    public async Task<byte?> FindFeatureAsync(ushort featureId, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(0, 0, new byte[] { (byte)(featureId >> 8), (byte)featureId, 0 }, cancellationToken).ConfigureAwait(false);
        return reply[0] == 0 ? null : reply[0];
    }

    public async Task<byte[]> RequestAsync(byte featureIndex, byte function, ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken = default)
    {
        if (function > 15) throw new ArgumentOutOfRangeException(nameof(function));
        if (parameters.Length > 16) throw new ArgumentException("A long HID++ report holds at most 16 parameter bytes.", nameof(parameters));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _requests.WaitAsync(caller.Token).ConfigureAwait(false);
        PendingRequest? pending = null;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_readerFailure is not null)
                    throw new IOException("The HID connection is no longer readable. Reopen the device.", _readerFailure);
                _softwareId = NextSoftwareId();
                pending = new PendingRequest(featureIndex, (byte)((function << 4) | _softwareId));
                _pending = pending;
            }
            byte[] report = new byte[_transport.OutputReportLength];
            report[0] = 0x11;
            report[1] = DeviceIndex;
            report[2] = featureIndex;
            report[3] = pending.FunctionAndSoftwareId;
            parameters.CopyTo(report.AsMemory(4));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
            deadline.CancelAfter(_requestTimeout);
            try
            {
                await _transport.WriteAsync(report, deadline.Token).ConfigureAwait(false);
                return await pending.Reply.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                throw new TimeoutException($"HID++ device 0x{DeviceIndex:X2}, feature index 0x{featureIndex:X2}, function {function} did not reply within {_requestTimeout.TotalMilliseconds:0} ms.");
            }
        }
        finally
        {
            lock (_sync)
            {
                // Cancellation/timeout does not prove that the mouse discarded the
                // request. Never reuse an ambiguous ID on this connection, even after
                // the four-bit counter wraps: a delayed reply could otherwise verify
                // a different write or be mistaken for that write's readback.
                if (pending is not null && !pending.Reply.Task.IsCompleted)
                    _retiredSoftwareIds |= (ushort)(1 << (pending.FunctionAndSoftwareId & 0x0F));
                if (ReferenceEquals(_pending, pending)) _pending = null;
            }
            _requests.Release();
        }
    }

    // Called with _sync held. Completed requests can safely reuse IDs; ambiguous
    // requests reserve theirs until the transport is reopened.
    private byte NextSoftwareId()
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            _softwareId = (byte)(_softwareId % 15 + 1);
            if ((_retiredSoftwareIds & (1 << _softwareId)) == 0) return _softwareId;
        }
        throw new IOException("All HID++ request IDs have unresolved replies. Reopen the device before sending more requests.");
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[_transport.InputReportLength];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                int count = await _transport.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The HID device closed its report stream.");
                ProcessReport(buffer, count);
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _readerFailure = exception;
                _pending?.Reply.TrySetException(exception);
            }
            var handlers = ConnectionLost;
            if (handlers is not null)
                foreach (Action<Exception> handler in handlers.GetInvocationList())
                    try { handler(exception); }
                    catch (Exception observerError) { System.Diagnostics.Debug.WriteLine(observerError); }
        }
    }

    private void ProcessReport(byte[] buffer, int count)
    {
        if (count < 7) return;
        int length = buffer[0] switch { 0x10 => 7, 0x11 => 20, _ => 0 };
        if (length == 0 || count < length || buffer[1] != DeviceIndex) return;
        byte feature = buffer[2];
        byte functionAndSoftware = buffer[3];
        lock (_sync)
        {
            if (_pending is { } pending)
            {
                if ((feature == 0xFF || feature == 0x8F)
                    && buffer[3] == pending.FeatureIndex && buffer[4] == pending.FunctionAndSoftwareId)
                {
                    pending.Reply.TrySetException(new HidppException(buffer[5], feature == 0x8F));
                    return;
                }
                if (feature == pending.FeatureIndex && functionAndSoftware == pending.FunctionAndSoftwareId)
                {
                    pending.Reply.TrySetResult(buffer.AsSpan(4, length - 4).ToArray());
                    return;
                }
            }
        }
        // Software ID zero denotes an unsolicited device notification. A late
        // reply must never become a physical button press.
        if (feature is 0xFF or 0x8F || (functionAndSoftware & 0x0F) != 0) return;
        var notification = new HidppNotification(DeviceIndex, feature,
            (byte)(functionAndSoftware >> 4), buffer.AsSpan(4, length - 4).ToArray());
        var handlers = NotificationReceived;
        if (handlers is null) return;
        foreach (Action<HidppNotification> handler in handlers.GetInvocationList())
        {
            try { handler(notification); }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            _pending?.Reply.TrySetException(new ObjectDisposedException(nameof(HidppClient)));
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _transport.DisposeAsync().ConfigureAwait(false); }
        finally { await _reader.ConfigureAwait(false); }
        // Keep the cancellation source alive for callers already queued behind
        // the semaphore; they must observe cancellation rather than a race with Dispose.
    }

    private sealed record PendingRequest(byte FeatureIndex, byte FunctionAndSoftwareId)
    {
        public TaskCompletionSource<byte[]> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class HidppException(byte errorCode, bool legacy = false)
    : IOException($"HID++ {(legacy ? "1.0" : "2.0")} device error 0x{errorCode:X2}.")
{
    public byte ErrorCode { get; } = errorCode;
}
