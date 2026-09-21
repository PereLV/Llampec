using System.Threading.Channels;
using Llampec.Diagnostics;
using Llampec.Settings;

namespace Llampec.Devices.Logitech;

/// <summary>
/// Optional mouse ownership independent of the panel. A single worker serializes
/// configuration, restoration and discovery. A separate bounded queue dispatches
/// input promptly without waiting for hardware discovery. Healthy sessions do
/// not poll; only disconnected enabled sessions have a cancellable retry deadline.
/// </summary>
public sealed class LogitechMouseService : IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaximumInputAge = TimeSpan.FromMilliseconds(500);
    private readonly ILogitechMouseBackend _backend;
    private readonly ILogitechRecoveryStore _store;
    private readonly Action<string> _shortcutSender;
    private readonly TimeProvider _time;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new() { SingleReader = true });
    private readonly Channel<Shortcut> _shortcuts = Channel.CreateBounded<Shortcut>(new BoundedChannelOptions(32)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly object _operationLock = new();
    private readonly object _stopLock = new();
    private readonly Task _worker;
    private readonly Task _inputWorker;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _retry;
    private Task? _retryTask;
    private Task? _stopTask;
    private ILogitechMouseSession? _session;
    private LogitechRecoveryJournal? _journal;
    private LogitechMouseSettings _settings = new();
    private LogitechMouseStatus _status = new(LogitechMouseConnectionState.Disabled, null, null, false);
    private bool _journalLoaded;
    private bool _journalLoadFailed;
    private bool _suspended;
    private int _suspendRequested;
    private int _stopping;
    private int _deviceChangeQueued;
    private int _retryRound;
    private long _retryVersion;
    private long _sessionGeneration;
    private long _inputEpoch;
    private long _sessionInputEpoch;

    public LogitechMouseService(Action<string> shortcutSender)
        : this(shortcutSender, new WindowsLogitechMouseBackend(), new LogitechRecoveryStore(LogitechRecoveryStore.DefaultPath)) { }

    internal LogitechMouseService(Action<string> shortcutSender, ILogitechMouseBackend backend,
        ILogitechRecoveryStore store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(shortcutSender);
        _shortcutSender = shortcutSender;
        _backend = backend;
        _store = store;
        _time = time ?? TimeProvider.System;
        _worker = Task.Run(WorkAsync);
        _inputWorker = Task.Run(DispatchInputAsync);
    }

    public LogitechMouseStatus Status => Volatile.Read(ref _status);
    public event EventHandler? Changed;

    /// <summary>Copies preferences, invalidates old queued input, and attempts the requested connection once.</summary>
    public Task ApplyAsync(LogitechMouseSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        var copy = settings.Clone();
        copy.Validate();
        long epoch = Interlocked.Increment(ref _inputEpoch);
        CancelCurrentOperation();
        var completion = NewCompletion();
        if (!_commands.Writer.TryWrite(new Apply(copy, epoch, cancellationToken, completion)))
            completion.TrySetException(new ObjectDisposedException(nameof(LogitechMouseService)));
        return completion.Task;
    }

    /// <summary>Read-only discovery; uses the current descriptor instead of opening a competing active HID reader.</summary>
    public Task<IReadOnlyList<LogitechMouseDevice>> ScanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        var completion = new TaskCompletionSource<IReadOnlyList<LogitechMouseDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new Scan(cancellationToken, completion)))
            completion.TrySetException(new ObjectDisposedException(nameof(LogitechMouseService)));
        return completion.Task;
    }

    public void NotifyDeviceChange()
    {
        if (Volatile.Read(ref _stopping) != 0 || Interlocked.Exchange(ref _deviceChangeQueued, 1) != 0) return;
        Interlocked.Increment(ref _inputEpoch);
        CancelCurrentOperation();
        _commands.Writer.TryWrite(new Wake(WakeReason.DeviceChange));
    }

    public void Suspend()
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        Volatile.Write(ref _suspendRequested, 1);
        Interlocked.Increment(ref _inputEpoch);
        CancelCurrentOperation();
        _commands.Writer.TryWrite(new Wake(WakeReason.Suspend));
    }

    public void Resume()
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        Volatile.Write(ref _suspendRequested, 0);
        _commands.Writer.TryWrite(new Wake(WakeReason.Resume));
    }

    /// <summary>Stops input immediately, cancels retries, and completes a bounded best-effort restoration.</summary>
    public Task StopAsync()
    {
        lock (_stopLock)
        {
            if (_stopTask is not null) return _stopTask;
            Volatile.Write(ref _stopping, 1);
            Interlocked.Increment(ref _inputEpoch);
            _shortcuts.Writer.TryComplete();
            CancelCurrentOperation();
            var completion = NewCompletion();
            _commands.Writer.TryWrite(new Stop(completion));
            // Producers racing the stop request must fail their completion source,
            // rather than enqueue work behind a stop command that ends the reader.
            _commands.Writer.TryComplete();
            _stopTask = completion.Task;
            return _stopTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
    }

    private async Task WorkAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (command)
                {
                    case Apply apply:
                        apply.Cancellation.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref _stopping) != 0) throw new OperationCanceledException("Mouse service is stopping.");
                        _settings = apply.Settings;
                        _retryRound = 0;
                        await RunOperationAsync(ct => ReconcileAsync(reopen: true, ct), apply.Cancellation).ConfigureAwait(false);
                        apply.Completion.TrySetResult();
                        break;
                    case Scan scan:
                        if (Volatile.Read(ref _stopping) != 0) throw new OperationCanceledException("Mouse service is stopping.");
                        await RunOperationAsync(async ct => scan.Completion.TrySetResult(
                            await _backend.ScanAsync(_session?.Device, ct).ConfigureAwait(false)), scan.Cancellation).ConfigureAwait(false);
                        break;
                    case Wake wake:
                        if (wake.Reason == WakeReason.DeviceChange) Interlocked.Exchange(ref _deviceChangeQueued, 0);
                        if (Volatile.Read(ref _stopping) != 0) break;
                        if (wake.Reason == WakeReason.Suspend) _suspended = true;
                        else if (wake.Reason == WakeReason.Resume) _suspended = false;
                        _retryRound = 0;
                        await RunOperationAsync(ct => ReconcileAsync(reopen: true, ct)).ConfigureAwait(false);
                        break;
                    case Lost lost:
                        if (lost.Generation != Volatile.Read(ref _sessionGeneration) || _session is null) break;
                        Log.Warn($"Logitech mouse disconnected: {lost.Error.Message}");
                        await DisconnectAsync().ConfigureAwait(false);
                        SetStatus(LogitechMouseConnectionState.Disconnected, null, lost.Error.Message);
                        ScheduleRetry();
                        break;
                    case Retry retry:
                        if (retry.Version != _retryVersion || Volatile.Read(ref _stopping) != 0 || _suspended) break;
                        await RunOperationAsync(ct => ReconcileAsync(reopen: false, ct)).ConfigureAwait(false);
                        break;
                    case InputFailure failure:
                        if (failure.Epoch == Volatile.Read(ref _inputEpoch) && Status.Connected)
                            SetStatus(LogitechMouseConnectionState.Connected, Status.Device, failure.Error.Message);
                        break;
                    case Stop stop:
                        CancelRetry();
                        try
                        {
                            await DisconnectAsync().ConfigureAwait(false);
                            if (_retryTask is not null) await _retryTask.ConfigureAwait(false);
                            await _inputWorker.ConfigureAwait(false);
                        }
                        catch (Exception error) { Log.Warn($"Logitech shutdown recovery pending: {error.Message}"); }
                        SetStatus(LogitechMouseConnectionState.Disabled, null, Status.Error);
                        _commands.Writer.TryComplete();
                        while (_commands.Reader.TryRead(out var abandoned))
                        {
                            // A producer may have won the narrow race between the
                            // Stop write and writer completion. Never leave its task pending.
                            if (abandoned is Apply pendingApply)
                                pendingApply.Completion.TrySetException(new ObjectDisposedException(nameof(LogitechMouseService)));
                            else if (abandoned is Scan pendingScan)
                                pendingScan.Completion.TrySetException(new ObjectDisposedException(nameof(LogitechMouseService)));
                        }
                        stop.Completion.TrySetResult();
                        return;
                }
            }
            catch (Exception error)
            {
                switch (command)
                {
                    case Apply apply when error is OperationCanceledException:
                        // A caller can cancel while its command waits behind a scan.
                        // Keep the previous live bindings usable, but never resurrect
                        // input invalidated by a newer configuration or suspend request.
                        if (_session is not null && Status.Connected && apply.Epoch == Volatile.Read(ref _inputEpoch))
                            Volatile.Write(ref _sessionInputEpoch, apply.Epoch);
                        if (apply.Cancellation.IsCancellationRequested || Volatile.Read(ref _stopping) != 0)
                            apply.Completion.TrySetCanceled();
                        else apply.Completion.TrySetResult(); // Internal reconnection preempts an attempt, not the saved request.
                        break;
                    case Apply apply: apply.Completion.TrySetException(error); break;
                    case Scan scan when error is OperationCanceledException:
                        scan.Completion.TrySetCanceled(); break;
                    case Scan scan: scan.Completion.TrySetException(error); break;
                    default: Log.Warn($"Logitech operation interrupted: {error.Message}"); break;
                }
            }
        }
    }

    private async Task ReconcileAsync(bool reopen, CancellationToken ct)
    {
        CancelRetry();
        try
        {
            if (!_journalLoaded)
            {
                try { _journal = _store.Load(); _journalLoaded = true; _journalLoadFailed = false; }
                catch { _journalLoadFailed = true; throw; }
            }
            if (reopen && _session is not null) await DisconnectAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_suspended)
            {
                SetStatus(LogitechMouseConnectionState.Suspended, null, null);
                return;
            }
            if (_journal is not null && _session is null)
                await RecoverAsync(ct).ConfigureAwait(false);
            if (!_settings.Enabled)
            {
                SetStatus(LogitechMouseConnectionState.Disabled, null, null);
                return;
            }
            if (_session is not null) return;
            SetStatus(LogitechMouseConnectionState.Connecting, null, null);
            _session = await _backend.OpenAsync(_settings.Identity(), ct).ConfigureAwait(false);
            if (!_settings.Identity().Matches(_session.Device)) throw new InvalidDataException("The selected mouse identity changed; select the intended mouse again.");
            long generation = Interlocked.Increment(ref _sessionGeneration);
            _session.ConnectionLost += error => OnConnectionLost(generation, error);
            if (_session.ConnectionError is { } initialError) throw new IOException("The mouse disconnected during discovery.", initialError);
            var snapshot = await _session.CaptureAsync(_settings, ct).ConfigureAwait(false);
            var journal = new LogitechRecoveryJournal(1,
                _settings.Identity() with { UnitId = _session.Device.UnitId ?? _settings.UnitId }, snapshot);
            // Durable baseline must exist before the first device setting or diversion write.
            _store.Save(journal);
            _journal = journal;
            Volatile.Write(ref _sessionInputEpoch, Volatile.Read(ref _inputEpoch));
            var activeSettings = _settings;
            await _session.ApplyAsync(activeSettings, (cid, down) =>
            {
                long epoch = Volatile.Read(ref _sessionInputEpoch);
                if (down && generation == Volatile.Read(ref _sessionGeneration) && epoch == Volatile.Read(ref _inputEpoch)
                    && activeSettings.ButtonShortcuts.TryGetValue(cid, out string? shortcut) && !string.IsNullOrWhiteSpace(shortcut))
                    _shortcuts.Writer.TryWrite(new Shortcut(epoch, shortcut, _time.GetTimestamp()));
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_session.ConnectionError is { } activationError) throw new IOException("The mouse disconnected while applying preferences.", activationError);
            _retryRound = 0;
            SetStatus(LogitechMouseConnectionState.Connected, _session.Device, null);
            Log.Info($"Logitech mouse connected: {_session.Device.Name}, slot {_session.Device.DeviceIndex}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisconnectAsync().ConfigureAwait(false);
            SetStatus(_suspended || Volatile.Read(ref _suspendRequested) != 0
                ? LogitechMouseConnectionState.Suspended
                : _settings.Enabled ? LogitechMouseConnectionState.Disconnected : LogitechMouseConnectionState.Disabled,
                null, null);
            ScheduleRetry();
            throw;
        }
        catch (Exception error)
        {
            await DisconnectAsync().ConfigureAwait(false);
            bool transient = IsTransient(error) && !_journalLoadFailed;
            SetStatus(transient ? LogitechMouseConnectionState.Disconnected : LogitechMouseConnectionState.Error, null, error.Message);
            Log.Warn($"Logitech mouse configuration unavailable: {error.Message}");
            if (transient) ScheduleRetry();
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        var journal = _journal!;
        SetStatus(LogitechMouseConnectionState.Connecting, null, "Restoring the previous mouse settings.");
        await using var recovery = await _backend.OpenAsync(journal.Identity, ct).ConfigureAwait(false);
        if (!journal.Identity.Matches(recovery.Device)) throw new InvalidDataException("The recovery mouse identity changed; the original mouse settings were not replayed.");
        await recovery.RestoreAsync(journal.State, ct).ConfigureAwait(false);
        _store.Clear();
        _journal = null;
        Log.Info("Logitech recovery completed before applying saved preferences.");
    }

    private async Task DisconnectAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        Interlocked.Increment(ref _sessionGeneration);
        Interlocked.Increment(ref _inputEpoch);
        try
        {
            if (_journal is { } journal && journal.Identity.Matches(session.Device))
            {
                using var cleanup = new CancellationTokenSource(RestoreTimeout);
                await session.RestoreAsync(journal.State, cleanup.Token).ConfigureAwait(false);
                _store.Clear();
                _journal = null;
                Log.Info("Logitech session settings restored.");
            }
        }
        catch (Exception error)
        {
            Log.Warn($"Logitech restoration pending: {error.Message}");
            SetStatus(LogitechMouseConnectionState.Disconnected, null, $"Mouse restoration is pending: {error.Message}");
        }
        finally
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                Log.Warn($"Logitech connection could not close cleanly: {error.Message}");
                SetStatus(LogitechMouseConnectionState.Error, null, error.Message);
            }
        }
    }

    private void OnConnectionLost(long generation, Exception error)
    {
        if (generation != Volatile.Read(ref _sessionGeneration)) return;
        Interlocked.Increment(ref _inputEpoch);
        CancelCurrentOperation();
        _commands.Writer.TryWrite(new Lost(generation, error));
    }

    private async Task DispatchInputAsync()
    {
        await foreach (var shortcut in _shortcuts.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (shortcut.Epoch != Volatile.Read(ref _inputEpoch) || !Status.Connected
                || Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _suspendRequested) != 0
                || _time.GetElapsedTime(shortcut.Timestamp) > MaximumInputAge) continue;
            try
            {
                _shortcutSender(shortcut.Text);
                Log.Info($"Logitech mouse shortcut: {shortcut.Text}");
            }
            catch (Exception error)
            {
                Log.Warn($"Logitech mouse shortcut failed: {error.Message}");
                _commands.Writer.TryWrite(new InputFailure(shortcut.Epoch, error));
            }
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, CancellationToken caller = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(OperationTimeout);
        lock (_operationLock) _operation = deadline;
        try { await operation(deadline.Token).ConfigureAwait(false); }
        finally { lock (_operationLock) if (ReferenceEquals(_operation, deadline)) _operation = null; }
    }

    private void CancelCurrentOperation()
    {
        lock (_operationLock) _operation?.Cancel();
    }

    private void ScheduleRetry()
    {
        if (!_settings.Enabled || _suspended || Volatile.Read(ref _suspendRequested) != 0 || Volatile.Read(ref _stopping) != 0) return;
        CancelRetry();
        int seconds = _retryRound++ switch { 0 => 2, 1 => 5, _ => 15 };
        var cancellation = new CancellationTokenSource();
        _retry = cancellation;
        long version = _retryVersion;
        _retryTask = QueueRetryAsync(version, TimeSpan.FromSeconds(seconds), cancellation.Token);
    }

    private async Task QueueRetryAsync(long version, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            _commands.Writer.TryWrite(new Retry(version));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void CancelRetry()
    {
        _retryVersion++;
        _retry?.Cancel();
        _retry?.Dispose();
        _retry = null;
    }

    private void SetStatus(LogitechMouseConnectionState state, LogitechMouseDevice? device, string? error)
    {
        Volatile.Write(ref _status, new(state, device, error,
            state != LogitechMouseConnectionState.Connected && (_journal is not null || _journalLoadFailed)));
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { Log.Warn($"Logitech status observer failed: {exception.Message}"); }
    }

    private static bool IsTransient(Exception error) => error is IOException and not HidppException
        or TimeoutException or System.ComponentModel.Win32Exception
        || error is AggregateException aggregate && aggregate.InnerExceptions.All(IsTransient);

    private void ThrowIfStopping() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private abstract record Command;
    private sealed record Apply(LogitechMouseSettings Settings, long Epoch, CancellationToken Cancellation, TaskCompletionSource Completion) : Command;
    private sealed record Scan(CancellationToken Cancellation, TaskCompletionSource<IReadOnlyList<LogitechMouseDevice>> Completion) : Command;
    private sealed record Wake(WakeReason Reason) : Command;
    private sealed record Lost(long Generation, Exception Error) : Command;
    private sealed record Retry(long Version) : Command;
    private sealed record InputFailure(long Epoch, Exception Error) : Command;
    private sealed record Shortcut(long Epoch, string Text, long Timestamp);
    private sealed record Stop(TaskCompletionSource Completion) : Command;
    private enum WakeReason { DeviceChange, Suspend, Resume }
}
