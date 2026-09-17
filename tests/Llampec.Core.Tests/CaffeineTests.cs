using Llampec.Actions;
using Llampec.Actions.Caffeine;
using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public class CaffeineTests
{
    private sealed class Request : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Elapsed;
        public TimeSpan WallShift;
        public List<Timer> Timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed + WallShift;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new Timer(() => callback(state), Elapsed + dueTime);
            Timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            Elapsed += elapsed;
            foreach (var timer in Timers.ToArray())
                if (!timer.Disposed && timer.Due <= Elapsed) timer.Fire();
        }
        public sealed class Timer(Action callback, TimeSpan due) : ITimer
        {
            public TimeSpan Due = due;
            public bool Disposed;
            public void Fire() => callback(); // Can represent a callback already queued before Dispose.
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task OffAndIndefiniteHaveNoTimerAndMainButtonToggles()
    {
        var clock = new Clock(); var request = new Request(); int requests = 0;
        using var action = new CaffeineAction(() => new() { Minutes = 0 }, _ => { requests++; return request; }, clock);
        Assert.Equal(ActionState.Off, action.State);
        Assert.Equal(0, requests); Assert.Empty(clock.Timers);
        await action.ExecuteAsync(default);
        Assert.Equal(ActionState.On, action.State); Assert.Null(action.EndsAt); Assert.Empty(clock.Timers);
        await action.ExecuteAsync(default);
        Assert.Equal(ActionState.Off, action.State); Assert.True(request.Disposed);
    }

    [Theory]
    [InlineData(1)] [InlineData(60)] [InlineData(120)] [InlineData(180)] [InlineData(1440)]
    public void TimedSessionsExpireAtTheirDeadline(int minutes)
    {
        var clock = new Clock(); var request = new Request();
        using var action = new CaffeineAction(() => new(), _ => request, clock);
        Assert.True(action.Start(new() { Minutes = minutes }));
        clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
        Assert.Equal(ActionState.On, action.State); Assert.False(request.Disposed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ActionState.Off, action.State); Assert.True(request.Disposed);
        Assert.True(Assert.Single(clock.Timers).Disposed);
    }

    [Fact]
    public void OldQueuedCallbackCannotCancelReplacement()
    {
        var clock = new Clock(); var requests = new List<Request>();
        using var action = new CaffeineAction(() => new(), _ => { var r = new Request(); requests.Add(r); return r; }, clock);
        action.Start(new() { Minutes = 60 });
        var stale = Assert.Single(clock.Timers);
        action.Start(new() { Minutes = 0, KeepDisplayOn = true });
        stale.Fire();
        Assert.True(requests[0].Disposed); Assert.False(requests[1].Disposed);
        Assert.Equal(ActionState.On, action.State); Assert.True(action.KeepDisplayOn);
        action.Dispose();
        Assert.True(requests[1].Disposed);
    }

    [Fact]
    public void FailureLeavesPreviousSessionAndDeadlineIntact()
    {
        var clock = new Clock(); var request = new Request(); bool fail = false;
        using var action = new CaffeineAction(() => new(), _ => fail ? throw new IOException("test failure") : request, clock);
        action.Start(new() { Minutes = 1 }); fail = true;
        Assert.False(action.Start(new() { Minutes = 0 }));
        Assert.NotNull(action.Error); Assert.False(request.Disposed);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(request.Disposed); Assert.Equal(ActionState.Off, action.State);
    }

    [Fact]
    public void ClockAdjustmentDoesNotChangeInterval()
    {
        var clock = new Clock();
        using var action = new CaffeineAction(() => new(), _ => new Request(), clock);
        action.Start(new() { Minutes = 60 });
        clock.WallShift = TimeSpan.FromDays(1); action.Refresh();
        Assert.Equal(ActionState.On, action.State);
        Assert.Equal(clock.GetUtcNow().AddHours(1), action.EndsAt);
        clock.Advance(TimeSpan.FromHours(1)); Assert.Equal(ActionState.Off, action.State);
    }

    [Theory]
    [InlineData(-1)] [InlineData(1441)]
    public void InvalidPreferencesNeverAcquirePowerRequest(int minutes)
    {
        using var action = new CaffeineAction(() => new(), _ => throw new Xunit.Sdk.XunitException("Must not acquire"));
        Assert.Throws<ArgumentOutOfRangeException>(() => action.Start(new() { Minutes = minutes }));
    }

    [Fact]
    public void StopAndDisposeCancelPendingTimer()
    {
        var clock = new Clock(); var request = new Request();
        var action = new CaffeineAction(() => new(), _ => request, clock);
        action.Start(new()); action.Stop(); action.Dispose();
        Assert.True(request.Disposed); Assert.True(Assert.Single(clock.Timers).Disposed);
        clock.Timers[0].Fire(); Assert.Equal(ActionState.Off, action.State);
        Assert.Throws<ObjectDisposedException>(() => action.Start(new()));
    }
}
