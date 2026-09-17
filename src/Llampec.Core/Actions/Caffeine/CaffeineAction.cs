using Llampec.Platform;
using Llampec.Settings;

namespace Llampec.Actions.Caffeine;

public sealed class CaffeineAction : QuickActionBase, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<CaffeineSettings> _preferences;
    private readonly Func<bool, IDisposable> _createRequest;
    private readonly TimeProvider _clock;
    private IDisposable? _request;
    private ITimer? _timer;
    private long _generation;
    private long _startedAt;
    private TimeSpan _duration;
    private bool _disposed;
    public DateTimeOffset? EndsAt { get; private set; }
    public bool KeepDisplayOn { get; private set; }
    public string? Error { get; private set; }
    public override string Id => "caffeine";
    public override string Title => "Caffeine mode";
    public override string Glyph => "\uEC32";
    public override ActionKind Kind => ActionKind.ToggleWithSubpage;
    public string StatusText
    {
        get
        {
            lock (_gate)
                return Error is not null ? UiText.Get(Error) : _request is null ? UiText.Get("Off")
                    : EndsAt is null ? UiText.Get("Indefinite") : UiText.Format("Until {0}", EndsAt.Value.ToLocalTime().ToString("t"));
        }
    }

    public CaffeineAction(Func<CaffeineSettings> preferences, Func<bool, IDisposable>? createRequest = null, TimeProvider? clock = null)
    {
        _preferences = preferences;
        _createRequest = createRequest ?? CaffeineRequest.Create;
        _clock = clock ?? TimeProvider.System;
        Refresh();
    }

    public bool Start(CaffeineSettings preferences)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CaffeineSettings.IsValid(preferences.Minutes)) throw new ArgumentOutOfRangeException(nameof(preferences));
            // Acquire first: a failed replacement leaves the existing session intact.
            IDisposable? request = null;
            ITimer? timer = null;
            try
            {
                request = _createRequest(preferences.KeepDisplayOn);
                long generation = _generation + 1;
                var end = preferences.Minutes == 0 ? (DateTimeOffset?)null : _clock.GetUtcNow().AddMinutes(preferences.Minutes);
                if (end is not null)
                    timer = _clock.CreateTimer(_ => Expire(generation), null, TimeSpan.FromMinutes(preferences.Minutes), Timeout.InfiniteTimeSpan);
                Release();
                _generation = generation;
                _request = request; _timer = timer;
                EndsAt = end; KeepDisplayOn = preferences.KeepDisplayOn; Error = null;
                _startedAt = _clock.GetTimestamp(); _duration = TimeSpan.FromMinutes(preferences.Minutes);
                Diagnostics.Log.Info("Caffeine session started");
            }
            catch (Exception ex)
            {
                timer?.Dispose(); request?.Dispose();
                Error = "Could not activate caffeine mode.";
                Diagnostics.Log.Warn($"Caffeine request failed: {ex.Message}");
            }
            Refresh();
            return Error is null;
        }
    }

    private void Expire(long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            Release(); Refresh();
        }
    }

    public void Stop()
    {
        lock (_gate) { Release(); Error = null; Refresh(); }
    }

    private void Release()
    {
        ++_generation;
        _timer?.Dispose(); _timer = null;
        _request?.Dispose(); _request = null;
        EndsAt = null; KeepDisplayOn = false;
    }

    public override void Refresh()
    {
        lock (_gate)
        {
            if (_request is not null && _duration > TimeSpan.Zero)
            {
                TimeSpan remaining = _duration - _clock.GetElapsedTime(_startedAt);
                if (remaining <= TimeSpan.Zero) Release();
                else EndsAt = _clock.GetUtcNow() + remaining;
            }
            State = _request is null ? ActionState.Off : ActionState.On;
            Subtitle = Error ?? (_request is null ? "Off" : EndsAt is null ? "Indefinite" : UiText.Format("Until {0}", EndsAt.Value.ToLocalTime().ToString("t")));
            OnChanged();
        }
    }

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_request is null) Start(_preferences()); else Stop();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Release(); }
    }
}
