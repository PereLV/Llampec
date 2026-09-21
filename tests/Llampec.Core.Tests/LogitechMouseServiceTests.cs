using System.Collections.Concurrent;
using System.Threading.Channels;
using Llampec.Devices.Logitech;
using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public class LogitechMouseServiceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static LogitechMouseSettings Enabled(ushort dpi = 1600) => new()
    {
        Enabled = true, DevicePath = "selected-mouse", ProductId = 0xB034, Dpi = dpi,
        ButtonShortcuts = new() { [0xC3] = "Win+Tab" },
    };

    [Fact]
    public async Task Disabled_service_does_not_discover_devices_or_schedule_retries()
    {
        var backend = new Backend(); var store = new Store(backend.Log); var time = new ManualTime();
        await using var service = new LogitechMouseService(_ => { }, backend, store, time);
        await service.ApplyAsync(new());

        Assert.Equal(LogitechMouseConnectionState.Disabled, service.Status.State);
        Assert.Empty(backend.OpenedIdentities);
        Assert.Equal(0, backend.ScanCount);
        Assert.Empty(time.ActiveTimers);
        Assert.Null(store.Journal);
    }

    [Fact]
    public async Task Enable_journals_before_mutation_and_disable_restores_before_clearing_the_journal()
    {
        var backend = new Backend(); var store = new Store(backend.Log);
        await using var service = new LogitechMouseService(_ => { }, backend, store);
        var preferences = Enabled();
        await service.ApplyAsync(preferences);
        preferences.ButtonShortcuts.Clear(); // Caller cannot mutate the active copy.

        Assert.True(service.Status.Connected);
        Assert.False(service.Status.RecoveryPending);
        Assert.Equal((ushort)1600, backend.HardwareDpi);
        Assert.NotNull(store.Journal);
        Assert.True(backend.Log.IndexOf("save:1000") < backend.Log.IndexOf("apply:1600"));
        Assert.Single(Assert.Single(backend.Sessions).Bindings);
        await service.ApplyAsync(new());

        Assert.Equal((ushort)1000, backend.HardwareDpi);
        Assert.Null(store.Journal);
        Assert.True(backend.Log.IndexOf("restore:1000") < backend.Log.IndexOf("clear"));
        Assert.True(Assert.Single(backend.Sessions).Disposed);
    }

    [Fact]
    public async Task Reconfiguration_restores_original_baseline_before_capturing_a_new_session()
    {
        var backend = new Backend(); var store = new Store(backend.Log);
        await using var service = new LogitechMouseService(_ => { }, backend, store);
        await service.ApplyAsync(Enabled(1600));
        await service.ApplyAsync(Enabled(2000));

        Assert.Equal((ushort)2000, backend.HardwareDpi);
        Assert.Equal((ushort)1000, store.Journal!.State.Dpi);
        Assert.Equal(new[] { "capture:1000", "save:1000", "apply:1600", "restore:1000", "clear",
            "capture:1000", "save:1000", "apply:2000" }, backend.Log.Where(IsTransaction));
    }

    [Fact]
    public async Task Failed_journal_write_prevents_all_device_mutations()
    {
        var backend = new Backend(); var store = new Store(backend.Log) { SaveError = new UnauthorizedAccessException("Read only.") };
        await using var service = new LogitechMouseService(_ => { }, backend, store);

        await service.ApplyAsync(Enabled());

        Assert.Equal(LogitechMouseConnectionState.Error, service.Status.State);
        Assert.DoesNotContain(backend.Log, entry => entry.StartsWith("apply:", StringComparison.Ordinal));
        Assert.Equal((ushort)1000, backend.HardwareDpi);
        Assert.Null(store.Journal);
    }

    [Fact]
    public async Task Reconnect_recovers_durable_state_before_recapturing_the_baseline()
    {
        var backend = new Backend(); var store = new Store(backend.Log);
        await using var service = new LogitechMouseService(_ => { }, backend, store);
        await service.ApplyAsync(Enabled());
        var lostSession = backend.Sessions[0];
        lostSession.RestoreError = new IOException("Disconnected.");
        backend.Available = false;
        lostSession.LoseConnection();
        await WaitForAsync(service, status => status.State == LogitechMouseConnectionState.Disconnected);
        Assert.NotNull(store.Journal);
        Assert.True(service.Status.RecoveryPending);
        backend.Log.Clear();
        backend.Available = true;

        service.NotifyDeviceChange();
        await WaitForAsync(service, status => status.Connected);

        Assert.Equal(new[] { "restore:1000", "clear", "capture:1000", "save:1000", "apply:1600" }, backend.Log.Where(IsTransaction));
        Assert.Equal((ushort)1000, store.Journal!.State.Dpi);
        Assert.All(backend.OpenedIdentities, identity => Assert.Equal("selected-mouse", identity.Path));
    }

    [Fact]
    public async Task Disabled_pending_recovery_retries_on_device_change_without_a_polling_timer()
    {
        var backend = new Backend { Available = false, HardwareDpi = 1600 };
        var store = new Store(backend.Log) { Journal = Journal() };
        var time = new ManualTime();
        await using var service = new LogitechMouseService(_ => { }, backend, store, time);
        await service.ApplyAsync(new());
        Assert.True(service.Status.RecoveryPending);
        Assert.Empty(time.ActiveTimers);
        backend.Available = true;

        service.NotifyDeviceChange();
        await WaitForAsync(service, status => status.State == LogitechMouseConnectionState.Disabled && !status.RecoveryPending);

        Assert.Equal((ushort)1000, backend.HardwareDpi);
        Assert.Null(store.Journal);
        Assert.DoesNotContain(backend.Log, entry => entry.StartsWith("apply:", StringComparison.Ordinal));
        Assert.All(backend.OpenedIdentities, identity => Assert.Equal("selected-mouse", identity.Path));
    }

    [Fact]
    public async Task Different_device_identity_cannot_receive_recovery_writes()
    {
        var backend = new Backend { ReportedUnitId = "AABBCCDD" };
        var store = new Store(backend.Log)
        {
            Journal = Journal() with { Identity = new("selected-mouse", 0xB034, 1, null, "11223344") },
        };
        await using var service = new LogitechMouseService(_ => { }, backend, store);

        await service.ApplyAsync(new());

        Assert.True(service.Status.RecoveryPending);
        Assert.NotNull(store.Journal);
        Assert.DoesNotContain(backend.Log, entry => entry.StartsWith("restore:", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Log, entry => entry.StartsWith("apply:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disconnect_retries_at_two_five_fifteen_seconds_and_stops_after_connection()
    {
        var backend = new Backend { Available = false }; var store = new Store(backend.Log); var time = new ManualTime();
        await using var service = new LogitechMouseService(_ => { }, backend, store, time);
        await service.ApplyAsync(Enabled());
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(time.ActiveTimers).Delay);
        await backend.NextOpenAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await backend.NextOpenAsync();
        await service.ScanAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(time.ActiveTimers).Delay);
        time.Advance(TimeSpan.FromSeconds(5));
        await backend.NextOpenAsync();
        await service.ScanAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(time.ActiveTimers).Delay);
        backend.Available = true;
        time.Advance(TimeSpan.FromSeconds(15));
        await WaitForAsync(service, status => status.Connected);

        Assert.Empty(time.ActiveTimers);
        Assert.Equal(4, backend.OpenedIdentities.Count);
        time.Advance(TimeSpan.FromHours(1));
        await service.ScanAsync();
        Assert.Equal(4, backend.OpenedIdentities.Count);
    }

    [Fact]
    public async Task Permanent_configuration_error_does_not_retry_automatically()
    {
        var backend = new Backend { ApplyError = new NotSupportedException("Button unavailable.") };
        var store = new Store(backend.Log); var time = new ManualTime();
        await using var service = new LogitechMouseService(_ => { }, backend, store, time);

        await service.ApplyAsync(Enabled());

        Assert.Equal(LogitechMouseConnectionState.Error, service.Status.State);
        Assert.Empty(time.ActiveTimers);
        Assert.Equal((ushort)1000, backend.HardwareDpi);
        Assert.Null(store.Journal);
    }

    [Fact]
    public async Task Suspend_restores_and_resume_reopens_the_selected_mouse()
    {
        var backend = new Backend(); var store = new Store(backend.Log);
        await using var service = new LogitechMouseService(_ => { }, backend, store);
        await service.ApplyAsync(Enabled());

        service.Suspend();
        await WaitForAsync(service, status => status.State == LogitechMouseConnectionState.Suspended);
        Assert.Equal((ushort)1000, backend.HardwareDpi);
        Assert.Null(store.Journal);
        service.Resume();
        await WaitForAsync(service, status => status.Connected);

        Assert.Equal(2, backend.Sessions.Count);
        Assert.Equal((ushort)1600, backend.HardwareDpi);
    }

    [Fact]
    public async Task Disable_invalidates_shortcut_events_already_queued_behind_an_inflight_input()
    {
        var backend = new Backend(); var store = new Store(backend.Log); var shortcuts = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var service = new LogitechMouseService(text =>
        {
            started.TrySetResult();
            release.Wait(Deadline);
            shortcuts.Enqueue(text);
        }, backend, store);
        await service.ApplyAsync(Enabled());
        backend.Sessions[0].Press(0xC3);
        await started.Task.WaitAsync(Deadline);
        backend.Sessions[0].Press(0xC3);

        try { await service.ApplyAsync(new()).WaitAsync(Deadline); }
        finally { release.Set(); }
        await service.StopAsync();

        Assert.Equal("Win+Tab", Assert.Single(shortcuts)); // Only the action already in progress can finish.
        Assert.Equal(LogitechMouseConnectionState.Disabled, service.Status.State);
    }

    [Fact]
    public async Task Button_input_does_not_wait_for_discovery_of_other_devices()
    {
        var backend = new Backend(); var store = new Store(backend.Log);
        var shortcut = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new LogitechMouseService(text => shortcut.TrySetResult(text), backend, store);
        await service.ApplyAsync(Enabled());
        backend.ScanBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task scan = service.ScanAsync();
        await backend.ScanStarted.Task.WaitAsync(Deadline);

        backend.Sessions[0].Press(0xC3);

        try
        {
            Assert.Equal("Win+Tab", await shortcut.Task.WaitAsync(Deadline));
            Assert.False(scan.IsCompleted);
        }
        finally { backend.ScanBlock.SetResult(); }
        await scan;
    }

    [Fact]
    public async Task Old_queued_input_is_discarded_instead_of_replayed_later()
    {
        var backend = new Backend(); var store = new Store(backend.Log); var time = new ManualTime();
        var output = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int calls = 0;
        await using var service = new LogitechMouseService(text =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started.TrySetResult(); release.Wait(Deadline); }
            output.Enqueue(text);
            if (text == "Ctrl+C") fresh.TrySetResult();
        }, backend, store, time);
        var settings = Enabled();
        settings.ButtonShortcuts[0x53] = "Ctrl+C";
        await service.ApplyAsync(settings);
        backend.Sessions[0].Press(0xC3);
        await started.Task.WaitAsync(Deadline);
        backend.Sessions[0].Press(0xC3);
        time.Advance(TimeSpan.FromSeconds(1));
        backend.Sessions[0].Press(0x53);

        release.Set();
        await fresh.Task.WaitAsync(Deadline);

        Assert.Equal(new[] { "Win+Tab", "Ctrl+C" }, output);
    }

    [Fact]
    public async Task Cancelling_a_queued_apply_preserves_existing_live_button_bindings()
    {
        var backend = new Backend { ScanHonorsCancellation = false }; var store = new Store(backend.Log);
        var shortcut = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new LogitechMouseService(text => shortcut.TrySetResult(text), backend, store);
        await service.ApplyAsync(Enabled());
        backend.ScanBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task scan = service.ScanAsync();
        await backend.ScanStarted.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        Task apply = service.ApplyAsync(Enabled(2000), cancellation.Token);
        cancellation.Cancel();
        backend.ScanBlock.SetResult();
        await scan;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);

        backend.Sessions[0].Press(0xC3);

        Assert.Equal("Win+Tab", await shortcut.Task.WaitAsync(Deadline));
        Assert.True(service.Status.Connected);
        Assert.Single(backend.Sessions);
        Assert.Equal((ushort)1600, backend.HardwareDpi);
    }

    [Fact]
    public async Task Device_change_preempts_an_attempt_without_cancelling_the_accepted_preferences()
    {
        var backend = new Backend { ApplyBlock = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var store = new Store(backend.Log);
        await using var service = new LogitechMouseService(_ => { }, backend, store);
        Task apply = service.ApplyAsync(Enabled());
        await backend.ApplyStarted.Task.WaitAsync(Deadline);
        backend.ApplyBlock = null;

        service.NotifyDeviceChange();
        await apply.WaitAsync(Deadline);
        await WaitForAsync(service, status => status.Connected);

        Assert.Equal(2, backend.Sessions.Count);
        Assert.Equal((ushort)1600, backend.HardwareDpi);
        Assert.Equal((ushort)1000, store.Journal!.State.Dpi);
    }

    [Fact]
    public async Task Scan_reuses_current_descriptor_and_stop_restores_then_cancels_future_work()
    {
        var backend = new Backend(); var store = new Store(backend.Log); var time = new ManualTime();
        await using var service = new LogitechMouseService(_ => { }, backend, store, time);
        await service.ApplyAsync(Enabled());
        var scanned = await service.ScanAsync();
        Assert.NotNull(backend.LastActiveScan);
        Assert.Same(backend.LastActiveScan, Assert.Single(scanned));
        Assert.Equal(service.Status.Device!.Path, scanned[0].Path);
        Assert.Single(backend.Sessions);

        await service.StopAsync();
        time.Advance(TimeSpan.FromHours(1));
        service.NotifyDeviceChange();
        Assert.Null(store.Journal);
        Assert.Equal((ushort)1000, backend.HardwareDpi);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ApplyAsync(Enabled()));
    }

    [Fact]
    public async Task Concurrent_apply_and_scan_calls_all_complete_when_racing_shutdown()
    {
        var backend = new Backend(); var store = new Store(backend.Log);
        await using var service = new LogitechMouseService(_ => { }, backend, store);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] callers = Enumerable.Range(0, 64).Select(index => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                if ((index & 1) == 0) await service.ApplyAsync(new());
                else await service.ScanAsync();
            }
            catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException) { }
        })).ToArray();
        Task stop = Task.Run(async () => { await start.Task; await service.StopAsync(); });

        start.SetResult();
        await Task.WhenAll(callers.Append(stop)).WaitAsync(Deadline);

        Assert.Equal(LogitechMouseConnectionState.Disabled, service.Status.State);
        Assert.Empty(backend.OpenedIdentities);
    }

    private static bool IsTransaction(string entry) => entry.StartsWith("capture:", StringComparison.Ordinal)
        || entry.StartsWith("save:", StringComparison.Ordinal) || entry.StartsWith("apply:", StringComparison.Ordinal)
        || entry.StartsWith("restore:", StringComparison.Ordinal) || entry == "clear";

    private static LogitechRecoveryJournal Journal() => new(1, new("selected-mouse", 0xB034, 255, null),
        new(1000, null, null, null, null, []));

    private static async Task WaitForAsync(LogitechMouseService service, Func<LogitechMouseStatus, bool> predicate, Func<bool>? additional = null)
    {
        bool Ready() => predicate(service.Status) && (additional?.Invoke() ?? true);
        if (Ready()) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args) { if (Ready()) completion.TrySetResult(); }
        service.Changed += Changed;
        try
        {
            if (Ready()) return;
            await completion.Task.WaitAsync(Deadline);
        }
        finally { service.Changed -= Changed; }
    }

    private sealed class Store(List<string> log) : ILogitechRecoveryStore
    {
        public LogitechRecoveryJournal? Journal;
        public Exception? SaveError;
        public LogitechRecoveryJournal? Load() => Journal;
        public void Save(LogitechRecoveryJournal journal)
        {
            if (SaveError is { } error) throw error;
            Journal = journal;
            log.Add($"save:{journal.State.Dpi}");
        }
        public void Clear() { log.Add("clear"); Journal = null; }
    }

    private sealed class Backend : ILogitechMouseBackend
    {
        public List<string> Log { get; } = [];
        public List<LogitechMouseIdentity> OpenedIdentities { get; } = [];
        public List<Session> Sessions { get; } = [];
        public bool Available = true;
        public ushort HardwareDpi = 1000;
        public string? ReportedUnitId;
        public Exception? ApplyError;
        public int ScanCount;
        public LogitechMouseDevice? LastActiveScan;
        public TaskCompletionSource? ScanBlock;
        public bool ScanHonorsCancellation = true;
        public TaskCompletionSource ScanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ApplyBlock;
        public TaskCompletionSource ApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<int> _opens = Channel.CreateUnbounded<int>();
        public Task<int> NextOpenAsync() => _opens.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        public Task<ILogitechMouseSession> OpenAsync(LogitechMouseIdentity identity, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            OpenedIdentities.Add(identity);
            _opens.Writer.TryWrite(OpenedIdentities.Count);
            if (!Available) throw new IOException("Selected mouse missing.");
            var session = new Session(this, identity);
            Sessions.Add(session);
            return Task.FromResult<ILogitechMouseSession>(session);
        }
        public async Task<IReadOnlyList<LogitechMouseDevice>> ScanAsync(LogitechMouseDevice? active, CancellationToken ct)
        {
            ScanCount++;
            LastActiveScan = active;
            ScanStarted.TrySetResult();
            if (ScanBlock is { } block)
            {
                if (ScanHonorsCancellation) await block.Task.WaitAsync(ct);
                else await block.Task;
            }
            return active is null ? [] : [active];
        }
    }

    private sealed class Session(Backend backend, LogitechMouseIdentity identity) : ILogitechMouseSession
    {
        public bool Disposed;
        public Exception? RestoreError;
        public Exception? ConnectionError { get; private set; }
        public Dictionary<ushort, string> Bindings = [];
        private Action<ushort, bool>? _button;
        public LogitechMouseDevice Device => new(identity.Path, identity.ProductId, identity.SerialNumber,
            identity.DeviceIndex, "MX test mouse", [new(0, 0xC3, 0xC3, 0x20, 0, 0, 0, 0, 0)],
            new(1, backend.HardwareDpi, 1000, [], new(200, 8000, 50)), null, false, null, null)
            { UnitId = backend.ReportedUnitId ?? identity.UnitId };
        public event Action<Exception>? ConnectionLost;
        public Task<LogitechRestoreState> CaptureAsync(LogitechMouseSettings settings, CancellationToken ct)
        {
            backend.Log.Add($"capture:{backend.HardwareDpi}");
            return Task.FromResult(new LogitechRestoreState(settings.Dpi.HasValue ? backend.HardwareDpi : null,
                null, null, null, null, settings.ButtonShortcuts.Keys.Select(id => new LogitechControlRestoreState(id, false)).ToArray()));
        }
        public async Task ApplyAsync(LogitechMouseSettings settings, Action<ushort, bool> button, CancellationToken ct)
        {
            backend.Log.Add($"apply:{settings.Dpi}");
            if (backend.ApplyError is { } error) throw error;
            if (settings.Dpi is ushort dpi) backend.HardwareDpi = dpi;
            Bindings = new(settings.ButtonShortcuts);
            _button = button;
            backend.ApplyStarted.TrySetResult();
            if (backend.ApplyBlock is { } block) await block.Task.WaitAsync(ct);
        }
        public Task RestoreAsync(LogitechRestoreState state, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (RestoreError is { } error) throw error;
            backend.Log.Add($"restore:{state.Dpi}");
            if (state.Dpi is ushort dpi) backend.HardwareDpi = dpi;
            return Task.CompletedTask;
        }
        public void Press(ushort cid) => _button?.Invoke(cid, true);
        public void LoseConnection()
        {
            ConnectionError = new IOException("Reader disconnected.");
            ConnectionLost?.Invoke(ConnectionError);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private TimeSpan _elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_timers) return _elapsed.Ticks; }
        public IReadOnlyList<Timer> ActiveTimers { get { lock (_timers) return _timers.Where(t => !t.Disposed && !t.Fired).ToArray(); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (_timers)
            {
                var timer = new Timer(() => callback(state), dueTime, _elapsed + dueTime);
                _timers.Add(timer);
                return timer;
            }
        }
        public void Advance(TimeSpan elapsed)
        {
            Timer[] ready;
            lock (_timers)
            {
                _elapsed += elapsed;
                ready = _timers.Where(t => !t.Disposed && !t.Fired && t.Due <= _elapsed).ToArray();
                foreach (var timer in ready) timer.Fired = true;
            }
            foreach (var timer in ready) timer.Callback();
        }
        public sealed class Timer(Action callback, TimeSpan delay, TimeSpan due) : ITimer
        {
            public Action Callback => callback;
            public TimeSpan Delay => delay;
            public TimeSpan Due => due;
            public bool Disposed;
            public bool Fired;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
