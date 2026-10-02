namespace Llampec.Platform;

/// <summary>
/// Applies and saves each selected orientation immediately. Only failed operations retain a
/// recovery snapshot; successful choices are never reverted by a timer or by closing a window.
/// </summary>
public sealed class DisplayOrientationService : IAsyncDisposable
{
    private sealed record Recovery(string DisplayId, DisplaySnapshot Before, DisplaySnapshot Applied,
        bool ChangedLock = false, bool PersistenceAttempted = false, bool RestoreSavedAttempted = false);

    private readonly IDisplayOrientationBackend _backend;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Recovery? _recovery;
    private volatile bool _busy;
    private bool _disposed;
    private DisplaySnapshot _snapshot = DisplaySnapshot.Unavailable;
    private string? _messageKey;

    public DisplayOrientationService(IDisplayOrientationBackend? backend = null, TimeProvider? timeProvider = null)
    {
        _backend = backend ?? new DisplayOrientationBackend();
        _time = timeProvider ?? TimeProvider.System;
    }

    public DisplaySnapshot Snapshot => _snapshot;
    public bool RecoveryPending => _recovery is not null;
    public string? RecoveryDisplayId => _recovery?.DisplayId;
    public string? MessageKey => _messageKey;
    public bool IsBusy => _busy;
    public event EventHandler? Changed;

    public void Refresh()
    {
        if (_busy || _disposed) return;
        CaptureSafely();
        Notify();
    }

    public Task ToggleLockAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        RequireNoRecovery();
        var before = _backend.Capture();
        await SetLockAsync(!before.Rotation.IsLocked, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    public Task ChangeOrientationAsync(string displayId, DisplayOrientation orientation, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            RequireNoRecovery();
            var before = _backend.Capture();
            var display = RequireDisplay(before, displayId);
            if (display.SavedMode is null) throw new DisplayOrientationException("Display configuration unavailable");
            var desired = display.Mode.WithOrientation(orientation);
            _backend.TestOrientation(before, displayId, desired);
            var recovery = new Recovery(displayId, before, WithMode(before, displayId, desired));
            try
            {
                // CDS_TEST and keyboard/dock transitions can overlap. Decide whether a lock is
                // needed using a fresh state, independently of manual mode support.
                var prepared = _backend.Capture();
                if (!before.SameDisplays(prepared))
                    throw new DisplayOrientationException("Display configuration changed; try again");
                if (display.IsInternal)
                {
                    if (!prepared.Rotation.IsKnown) throw new DisplayOrientationException("Rotation state unavailable");
                    if (!prepared.Rotation.CanSetOrientationWithoutChangingLock)
                    {
                        await SetLockAsync(true, cancellationToken,
                            () => recovery = recovery with { ChangedLock = true }).ConfigureAwait(false);
                        prepared = _backend.Capture();
                        if (!before.SameDisplays(prepared))
                            throw new DisplayOrientationException("Display configuration changed; try again");
                    }
                    if (!prepared.Rotation.CanSetOrientationWithoutChangingLock)
                        throw new DisplayOrientationException("Rotation state unavailable");
                }

                cancellationToken.ThrowIfCancellationRequested();
                recovery = recovery with { Applied = WithMode(prepared, displayId, desired) };
                if (display.Mode != desired || display.SavedMode != desired)
                {
                    // CDS_UPDATEREGISTRY commits the live and saved modes together. The call can
                    // still partially succeed, so track both original modes before invoking it.
                    recovery = recovery with { PersistenceAttempted = true };
                    _backend.ApplyAndSaveOrientation(prepared, displayId, desired);
                }
                var applied = _backend.Capture();
                var expected = recovery.Applied with
                {
                    Displays = recovery.Applied.Displays.Select(d => d.Id == displayId ? d with { SavedMode = desired } : d).ToArray()
                };
                VerifyApplied(expected, applied, display);
                _messageKey = "Orientation applied";
            }
            catch
            {
                if (recovery.ChangedLock || recovery.PersistenceAttempted)
                {
                    _recovery = recovery;
                    try { await RevertCoreAsync(recovery, reportSuccess: false).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Diagnostics.Log.Warn($"Rotation recovery failed: {ex.Message}");
                        // An obsolete snapshot is discarded; a transient failure retains a retry.
                        if (_recovery is not null) _messageKey = "Windows could not restore the previous orientation";
                    }
                }
                throw;
            }
        }, cancellationToken);

    public Task RevertRecoveryAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        if (_recovery is { } recovery) await RevertCoreAsync(recovery, reportSuccess: true).ConfigureAwait(false);
    }, cancellationToken);

    private async Task RevertCoreAsync(Recovery recovery, bool reportSuccess)
    {
        var current = _backend.Capture();
        RequireStillOurs(recovery, current);
        var original = RequireDisplay(recovery.Before, recovery.DisplayId);
        if (recovery.PersistenceAttempted && current.Find(original.Id)?.SavedMode != original.SavedMode)
        {
            recovery = recovery with { RestoreSavedAttempted = true };
            _recovery = recovery;
            _backend.ApplyAndSaveOrientation(current, original.Id,
                original.SavedMode ?? throw new DisplayOrientationException("Display configuration unavailable"));
            current = _backend.Capture();
            RequireStillOurs(recovery, current);
            if (current.Find(original.Id)?.SavedMode != original.SavedMode)
                throw new DisplayOrientationException("Windows could not restore the previous orientation");
        }
        if (current.Find(original.Id)?.Mode != original.Mode)
            _backend.ApplyOrientation(current, original.Id, original.Mode);
        var restored = _backend.Capture();
        if (!recovery.Before.SameDisplays(restored))
            throw new DisplayOrientationException("Windows could not restore the previous orientation");
        if (recovery.ChangedLock)
        {
            if (!restored.Rotation.IsKnown)
                throw new DisplayOrientationException("Rotation changed elsewhere; lock was not restored");
            if (restored.Rotation.IsLocked) await SetLockAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
        _recovery = null;
        if (reportSuccess) _messageKey = "Previous orientation restored";
    }

    private static DisplaySnapshot WithMode(DisplaySnapshot snapshot, string id, DisplayMode mode) => snapshot with
    {
        Displays = snapshot.Displays.Select(d => d.Id == id ? d with { Mode = mode } : d).ToArray()
    };

    private static void VerifyApplied(DisplaySnapshot expected, DisplaySnapshot current, DisplayInfo display)
    {
        if (!expected.SameDisplays(current))
            throw new DisplayOrientationException("Windows could not verify the orientation");
        if (display.IsInternal && !current.Rotation.CanSetOrientationWithoutChangingLock)
            throw new DisplayOrientationException("Windows could not change rotation lock");
    }

    private async Task SetLockAsync(bool desired, CancellationToken cancellationToken, Action? sent = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = _backend.Capture().Rotation;
        if (!before.IsKnown) throw new DisplayOrientationException("Rotation state unavailable");
        if (before.IsLocked == desired) return;
        if (!before.CanToggle) throw new DisplayOrientationException(before.ReasonKeys[0]);
        // A shortcut can throw after partial insertion; recovery must inspect the actual lock.
        sent?.Invoke();
        if (!_backend.SendRotationLockShortcut())
            throw new DisplayOrientationException("Release the keyboard keys and try again");
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var current = _backend.Capture().Rotation;
            if (current.IsKnown && current.IsLocked == desired) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), _time, CancellationToken.None).ConfigureAwait(false);
        }
        throw new DisplayOrientationException("Windows could not change rotation lock");
    }

    private async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _busy = true;
            _messageKey = null;
            Notify();
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _messageKey ??= ex is DisplayOrientationException known ? known.MessageKey : "Display configuration unavailable";
            Diagnostics.Log.Warn($"Rotation operation failed: {ex.Message}");
        }
        finally
        {
            CaptureSafely();
            _busy = false;
            _gate.Release();
            Notify();
        }
    }

    private void RequireStillOurs(Recovery recovery, DisplaySnapshot current)
    {
        if (StillOurs(recovery, current)) return;
        _recovery = null;
        throw new DisplayOrientationException("Display changed elsewhere; previous settings were not restored");
    }

    private static bool StillOurs(Recovery recovery, DisplaySnapshot current)
    {
        // A failed write may leave the original mode in place, or recovery may have restored it
        // before a query failed. Both states can still require restoring our rotation lock.
        if (recovery.Before.SameDisplays(current))
            return !recovery.ChangedLock || current.Rotation.IsKnown;
        var expected = recovery.Applied;
        if (recovery.PersistenceAttempted)
        {
            var original = recovery.Before.Find(recovery.DisplayId)!;
            var target = expected.Find(recovery.DisplayId)!;
            var actual = current.Find(recovery.DisplayId);
            // A combined commit can update either state before failing (for example, a driver
            // requests a restart after saving). Recovery also passes through these same pairs.
            // Accept only the exact old/new modes; all other displays and topology must match.
            bool allowedLive = actual is not null && (actual.Mode == original.Mode || actual.Mode == target.Mode ||
                recovery.RestoreSavedAttempted && actual.Mode == original.SavedMode);
            if (!allowedLive || actual is null ||
                (actual.SavedMode != original.SavedMode && actual.SavedMode != target.Mode))
                return false;
            expected = expected with
            {
                Displays = expected.Displays.Select(d => d.Id == recovery.DisplayId
                    ? d with { Mode = actual.Mode, SavedMode = actual.SavedMode } : d).ToArray()
            };
        }
        return expected.SameDisplays(current) && (!recovery.ChangedLock || current.Rotation.IsKnown && current.Rotation.IsLocked);
    }

    private static DisplayInfo RequireDisplay(DisplaySnapshot snapshot, string id)
    {
        var display = snapshot.Find(id) ?? throw new DisplayOrientationException("The selected display was disconnected");
        if (display.IsCloned) throw new DisplayOrientationException("Use Extend to rotate displays separately");
        if (!display.CanOrient) throw new DisplayOrientationException("This orientation is not supported");
        return display;
    }

    private void RequireNoRecovery()
    {
        if (_recovery is not null)
            throw new DisplayOrientationException("Restore the previous orientation before making another change");
    }

    private void CaptureSafely()
    {
        try { _snapshot = _backend.Capture(); }
        catch (Exception ex)
        {
            _snapshot = DisplaySnapshot.Unavailable;
            _messageKey ??= ex is DisplayOrientationException known ? known.MessageKey : "Display configuration unavailable";
        }
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _busy = true;
            if (_recovery is { } recovery) await RevertCoreAsync(recovery, reportSuccess: false).ConfigureAwait(false);
        }
        catch (Exception ex) { Diagnostics.Log.Warn($"Rotation shutdown recovery failed: {ex.Message}"); }
        finally
        {
            _disposed = true;
            _recovery = null;
            _busy = false;
            _gate.Release();
        }
    }
}
