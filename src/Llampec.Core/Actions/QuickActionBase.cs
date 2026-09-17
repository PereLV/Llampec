namespace Llampec.Actions;

/// <summary>
/// Convenience base class: stores state, raises <see cref="Changed"/>, and wraps <see cref="ExecuteAsync"/>
/// with the busy flag and error isolation so a failing action never brings the panel down.
/// </summary>
public abstract class QuickActionBase : IQuickAction
{
    private string? _subtitle;
    private bool _isAvailable = true;
    private ActionState _state;
    private bool _isBusy;

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public virtual string? GlyphBadge => null;
    public abstract ActionKind Kind { get; }

    public string? Subtitle
    {
        get => _subtitle;
        protected set => Set(ref _subtitle, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        protected set => Set(ref _isAvailable, value);
    }

    public ActionState State
    {
        get => _state;
        protected set => Set(ref _state, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public virtual IReadOnlyList<IQuickAction> SubActions => [];
    public virtual bool SubActionsAreExclusive => false;

    public event EventHandler? Changed;

    public virtual void Refresh()
    {
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || !IsAvailable)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Actions talk to drivers and the shell; failures are expected occasionally.
            // Surface them through state (re-read) rather than crashing.
            Diagnostics.Log.Warn($"Action '{Id}' failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>The actual work. State should be re-read in <see cref="Refresh"/>, not assumed here.</summary>
    protected abstract Task ExecuteCoreAsync(CancellationToken cancellationToken);

    protected bool Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnChanged();
        return true;
    }

    protected void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
