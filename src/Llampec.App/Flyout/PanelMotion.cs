using System.Diagnostics;
using System.Numerics;
using Llampec.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Llampec.Flyout;

/// <summary>Move the complete surface on the compositor, inside a stationary transparent HWND.</summary>
internal sealed class PanelMotion : IDisposable
{
    private readonly Visual _visual;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _deadline;
    private CompositionScopedBatch? _batch;
    private Action? _completed;
    private bool _warming, _opening, _disposed;
    private int _warmupFrames, _version;
    private float _distance;
    private long _started;

    public PanelMotion(UIElement surface, DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        ElementCompositionPreview.SetIsTranslationEnabled(surface, true);
        _visual = ElementCompositionPreview.GetElementVisual(surface);
        _deadline = dispatcher.CreateTimer();
        _deadline.IsRepeating = false;
        _deadline.Tick += OnDeadline;
    }

    public void PrepareOpening(float distance, bool fresh)
    {
        _distance = distance;
        if (!fresh) return;
        Cancel();
        _visual.Properties.InsertVector3("Translation", new Vector3(0, distance, 32));
        _visual.Opacity = 0;
    }

    public void Start(bool opening, bool animate, bool fresh, float distance, Action completed)
    {
        StopCompletion();
        _distance = distance;
        _opening = opening;
        _completed = completed;
        if (!animate)
        {
            SetEndpoint();
            Complete(timedOut: false);
            return;
        }

        // Submit rebuilt controls/material before the first visible frame. This
        // hook only primes entry; Composition owns every actual motion frame.
        if (opening && fresh)
        {
            _warming = true;
            _warmupFrames = 2;
            CompositionTarget.Rendering += OnWarmup;
        }
        else Animate();
        _deadline.Interval = TimeSpan.FromMilliseconds(1000);
        _deadline.Start();
    }

    private void OnWarmup(object? sender, object args)
    {
        if (--_warmupFrames > 0) return;
        StopWarmup();
        Animate();
    }

    private void Animate()
    {
        _started = Stopwatch.GetTimestamp();
        var compositor = _visual.Compositor;
        _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch.Completed += OnCompleted;

        using var easing = _opening
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.6f), new Vector2(0.25f, 1))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.8f, 0.55f));
        using var slide = compositor.CreateScalarKeyFrameAnimation();
        // Replacing an animation starts at its presented value. Reversal never
        // restores geometry or rebuilds the currently visible page.
        slide.InsertExpressionKeyFrame(0, "this.StartingValue");
        slide.InsertKeyFrame(1, _opening ? 0 : _distance, easing);
        slide.Duration = TimeSpan.FromMilliseconds(_opening ? 320 : 220);
        slide.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        _visual.StartAnimation("Translation.Y", slide);

        using var fade = compositor.CreateScalarKeyFrameAnimation();
        using var linear = compositor.CreateLinearEasingFunction();
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, _opening ? 1 : 0, linear);
        fade.Duration = TimeSpan.FromMilliseconds(_opening ? 70 : 100);
        fade.DelayTime = TimeSpan.FromMilliseconds(_opening ? 0 : 120);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        fade.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        _visual.StartAnimation("Opacity", fade);
        _batch.End();
    }

    private void OnCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        int version = _version;
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed && version == _version && ReferenceEquals(sender, _batch))
                Complete(timedOut: false);
        });
    }

    private void OnDeadline(DispatcherQueueTimer sender, object args)
    {
        SetEndpoint();
        Complete(timedOut: true);
    }

    private void SetEndpoint()
    {
        _visual.StopAnimation("Translation.Y");
        _visual.StopAnimation("Opacity");
        _visual.Properties.InsertVector3("Translation", new Vector3(0, _opening ? 0 : _distance, 32));
        _visual.Opacity = _opening ? 1 : 0;
    }

    private void Complete(bool timedOut)
    {
        var completed = _completed;
        double elapsed = _started == 0 ? 0 : Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        StopCompletion();
        Log.Info($"Panel {(_opening ? "open" : "close")}: compositor completion, {elapsed:F0} ms; fallback={timedOut}; HWND stationary");
        completed?.Invoke();
    }

    private void StopWarmup()
    {
        if (_warming) CompositionTarget.Rendering -= OnWarmup;
        _warming = false;
    }

    private void StopCompletion()
    {
        ++_version;
        StopWarmup();
        _deadline.Stop();
        if (_batch is not null)
        {
            _batch.Completed -= OnCompleted;
            _batch.Dispose();
            _batch = null;
        }
        _completed = null;
        _started = 0;
    }

    public void Cancel()
    {
        StopCompletion();
        _visual.StopAnimation("Translation.Y");
        _visual.StopAnimation("Opacity");
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
        _deadline.Tick -= OnDeadline;
    }
}
